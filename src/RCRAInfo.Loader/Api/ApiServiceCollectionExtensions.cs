using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Registers the RCRAInfo API client: options, the two <c>HttpClient</c>s, and the token provider.
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>Adds everything needed to call EPA's RCRAInfo REST service.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">
    /// Configuration holding the <c>RCRAInfoApi</c> section. Validated at startup rather than on first
    /// use — a scheduled task that runs at 2am and fails on its first HTTP call has already burned the
    /// window it was given.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>The caller must have registered an <c>ApplicationCredentials</c> singleton already</b>, from
    /// <c>CredentialBootstrapResult.Require ()</c>. That ordering is not a quirk of this method: the AR4
    /// bootstrap has to run, and be allowed to fail, before there is a host to put anything into — the
    /// credential file may need sealing, the SQL password may be wrong, and both of those are exit codes
    /// rather than start-up exceptions (plan §4.2).
    /// </para>
    /// <para>
    /// <b>Both clients are registered with <c>RemoveAllLoggers ()</c>, and that is the security control
    /// here rather than a preference.</b> Measured on this solution's package versions: with stock
    /// <c>IHttpClientFactory</c> logging, one request produces eight log entries, two of which print the
    /// full request URI at <b>Information</b> — for the auth call that is the API ID and Key, in whatever
    /// sink the host has attached. A third hazard sits at <c>Trace</c>, where the stock logging writes
    /// request headers, which for the data client is <c>Authorization: Bearer …</c>. Removing the loggers
    /// removes all three at once and costs nothing this project wanted: the loader's HTTP record is
    /// <c>logs.HandlerLoadAttempt</c>, which stores the <b>path only</b> — never a query string, never a
    /// header — because the monitoring web application can read it (AR8).
    /// </para>
    /// <para>
    /// Polly's resilience telemetry was measured in the same run and reports the pipeline name, the
    /// attempt number and the status code, never the URI, so the resilience handler keeps its logging.
    /// </para>
    /// <para>
    /// <b>The data client's handler order is load-bearing: token → resilience → pacing.</b> Handlers run in
    /// registration order, outermost first, so the pacing gate ends up closest to the network and every
    /// request EPA receives is paced — <i>including</i> the retries Polly generates, which is the traffic
    /// that most needs it. See <see cref="RequestPacingHandler"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRcraInfoApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RcraInfoApiOptions>()
            .Bind(configuration.GetSection(RcraInfoApiOptions.SectionName))
            .Validate(
                options => options.Validate().Count == 0,
                "The RCRAInfoApi configuration section is not usable.")
            .ValidateOnStart();

        services.AddOptions<RcraInfoThrottleOptions>()
            .Bind(configuration.GetSection(RcraInfoThrottleOptions.SectionName))
            .Validate(
                options => options.Validate().Count == 0,
                "The RCRAInfoApi:Throttle configuration section is not usable.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Singleton, and it must be: two pacers are two independent rate limits and EPA sees their sum.
        services.TryAddSingleton<RequestPacer>();
        services.TryAddTransient<RequestPacingHandler>();

        services.AddHttpClient<IRcraInfoAuthClient, RcraInfoAuthClient>(
                RcraInfoApiClients.Auth,
                ConfigureAuthClient)
            .RemoveAllLoggers();

        // Registered as a typed client so that the only HttpClient carrying a bearer token is reachable
        // through one interface. A bare named client would leave IHttpClientFactory.CreateClient
        // ("rcrainfo-data") available to anything with the factory, which is the shape that lets a second
        // caller appear later without the classification -- and the classification is what keeps a 404
        // from becoming a soft delete.
        IHttpClientBuilder dataClient = services
            .AddHttpClient<IRcraInfoDataClient, RcraInfoDataClient>(
                RcraInfoApiClients.Data,
                ConfigureDataClient)
            .RemoveAllLoggers()
            .AddHttpMessageHandler<ApiTokenHandler>();

        // Split across three statements rather than chained because AddStandardResilienceHandler returns its
        // own builder. The call ORDER is what sets the handler order, so the pacing handler is added after
        // the resilience handler and is therefore inside it.
        dataClient.AddStandardResilienceHandler().Configure(ConfigureResilience);
        dataClient.AddHttpMessageHandler<RequestPacingHandler>();

        services.TryAddTransient<ApiTokenHandler>();
        services.TryAddSingleton<IApiTokenProvider, RcraInfoTokenProvider>();

        return services;
    }

    private static void ConfigureAuthClient(IServiceProvider provider, HttpClient client)
    {
        RcraInfoApiOptions options = provider
            .GetRequiredService<IOptions<RcraInfoApiOptions>>()
            .Value;

        if (!options.TryGetBaseUri(out Uri? baseUri, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        client.BaseAddress = baseUri;
        client.Timeout = options.RequestTimeout;

        // A token response is a few hundred bytes. The cap is here so that a portal login page or a proxy
        // error served in place of the API -- the most likely wrong answer to this request, and the one a
        // misconfigured BaseAddress produces -- is refused rather than buffered.
        client.MaxResponseContentBufferSize = 64 * 1024;
    }

    private static void ConfigureDataClient(IServiceProvider provider, HttpClient client)
    {
        RcraInfoApiOptions options = provider
            .GetRequiredService<IOptions<RcraInfoApiOptions>>()
            .Value;

        if (!options.TryGetBaseUri(out Uri? baseUri, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        client.BaseAddress = baseUri;

        // No Timeout here, and no content-size cap: the resilience handler owns per-attempt timeouts for
        // this client, and a HandlerSource detail payload is 377 fields deep. Setting HttpClient.Timeout as
        // well would cancel a request the retry pipeline was still working on, and the failure would be
        // reported as a timeout that no configured value explains.
    }

    /// <summary>
    /// Puts this project's numbers on the resilience pipeline that has been running on package defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here is new machinery.</b> <c>AddStandardResilienceHandler</c> has been on this client
    /// since §D1 — which is why <c>IRcraInfoDataClient</c> can promise that it does not retry and why
    /// <c>SummaryWalk</c> and <c>LookupRefresh</c> contain no retry loops. What was missing was the
    /// configuration: the defaults are 3 attempts, 2s exponential backoff with jitter, a 10s attempt timeout
    /// and a 30s total, which are sensible numbers for a web request a person is waiting on and the wrong
    /// ones for a payload 377 fields deep fetched by an unattended overnight batch. G21's answer belongs
    /// somewhere a human can change it, which is <see cref="RcraInfoThrottleOptions"/>.
    /// </para>
    /// <para>
    /// <b><c>ShouldRetryAfterHeader</c> is the one setting that is not this project's opinion.</b> It makes
    /// the pipeline honour <c>Retry-After</c> on a <c>429</c> or <c>503</c> instead of using the computed
    /// backoff — an instruction from EPA rather than a guess about EPA, and the whole of what "back off
    /// politely" means. It defaults to <see langword="true"/>; it is set anyway, because a default that
    /// silently flipped would be indistinguishable from working and the symptom would arrive as an access
    /// problem months later.
    /// </para>
    /// <para>
    /// <b>The circuit breaker's sampling duration is derived, not chosen.</b> The options type validates
    /// <c>SamplingDuration &gt;= 2 × AttemptTimeout</c> and fails at start-up otherwise, so raising the
    /// attempt timeout to 30s forces at least 60s here. Deriving it means one setting moves and the other
    /// follows; a literal would turn a routine tuning change into a start-up failure whose message names a
    /// value nobody edited.
    /// </para>
    /// <para>
    /// <b>What this configuration costs, stated rather than worked around:</b> Polly's attempts are
    /// invisible to <c>logs.HandlerLoadAttempt</c>. One <c>FetchAsync</c> call is one attempt row, so a
    /// version fetched on the third try records <c>AttemptNumber</c> 1 with whatever the third try returned,
    /// and the two failures before it exist only in Polly's telemetry. That is the deliberate trade for
    /// having retries in one place: the alternative is a caller-driven retry loop that can journal every
    /// attempt and that also duplicates the backoff, the jitter and the <c>Retry-After</c> handling this
    /// pipeline already does correctly. The row that matters — did this version end up loaded — is right
    /// either way. F2 measures the retry rate from the telemetry.
    /// </para>
    /// </remarks>
    private static void ConfigureResilience(HttpStandardResilienceOptions resilience, IServiceProvider provider)
    {
        RcraInfoThrottleOptions throttle = provider
            .GetRequiredService<IOptions<RcraInfoThrottleOptions>>()
            .Value;

        resilience.Retry.MaxRetryAttempts = throttle.MaxRetryAttempts;
        resilience.Retry.Delay = throttle.RetryDelay;
        resilience.Retry.MaxDelay = throttle.MaxRetryDelay;
        resilience.Retry.BackoffType = DelayBackoffType.Exponential;
        resilience.Retry.UseJitter = true;
        resilience.Retry.ShouldRetryAfterHeader = true;

        resilience.AttemptTimeout.Timeout = throttle.AttemptTimeout;
        resilience.TotalRequestTimeout.Timeout = throttle.TotalRequestTimeout;

        // Derived from the attempt timeout for the reason in the remarks: the options type validates
        // SamplingDuration >= 2 * AttemptTimeout at start-up.
        TimeSpan minimumSampling = throttle.AttemptTimeout * 2;

        if (resilience.CircuitBreaker.SamplingDuration < minimumSampling)
        {
            resilience.CircuitBreaker.SamplingDuration = minimumSampling;
        }
    }
}
