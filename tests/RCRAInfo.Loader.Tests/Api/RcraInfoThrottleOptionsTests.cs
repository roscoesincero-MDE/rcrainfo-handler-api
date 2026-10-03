using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RCRAInfo.Core.Credentials;
using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// G21's answer, and the pipeline it configures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two requests per second is an assumption, not a published limit.</b> EPA documents no rate limit for
/// the RCRAInfo REST API — that is what G21 asks — so the number is a decision, and these tests are what make
/// it a decision that can be changed in one place. Half the file is about the settings being <i>wired</i>:
/// <c>AddStandardResilienceHandler</c> has been on this client since §D1 and was running on package defaults
/// the whole time, which is indistinguishable from working right up until somebody edits
/// <c>appsettings.json</c> and nothing changes.
/// </para>
/// <para>
/// The other half is the ordering. The pacing handler is registered <i>after</i> the resilience handler and
/// is therefore inside it, so Polly's retries are paced too — the opposite arrangement paces logical requests
/// while a retry storm slips through unpaced, which is backwards, because a retry storm is the moment EPA has
/// already said it is unhappy.
/// </para>
/// </remarks>
public class RcraInfoThrottleOptionsTests
{
    private const string ApiId = "MDTESTAPIID00001";
    private const string ApiKey = "MDTESTAPIKEY-0123456789";
    private const string Base = "https://rcranodepreprod.epa.gov/rcra-api/rest";

    [Fact]
    public void TheDefaultsAreTheConservativeAnswerToG21()
    {
        RcraInfoThrottleOptions options = new();

        Assert.Empty(options.Validate());
        Assert.Equal(2, options.MaxRequestsPerSecond);
        Assert.Equal(2, options.MaxConcurrentRequests);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.RequestInterval);
        Assert.Equal(3, options.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.AttemptTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), options.TotalRequestTimeout);
    }

    [Fact]
    public void TheIntervalIsComputedFromTicksSoAFractionalRateSurvives()
    {
        // 0.5 requests per second is what a measured 429 problem calls for. Computed in whole seconds it
        // would round to one second -- twice the rate that was asked for, in the direction that gets noticed.
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            new RcraInfoThrottleOptions { MaxRequestsPerSecond = 0.5 }.RequestInterval);

        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            new RcraInfoThrottleOptions { MaxRequestsPerSecond = 4 }.RequestInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ThereIsNoUnlimitedRate(double rate)
    {
        // A "0 means unlimited" convention makes the most dangerous configuration the one that looks like an
        // absent value, and this is the setting that decides whether an unattended overnight run looks like a
        // load or like an attack.
        IReadOnlyList<string> problems = new RcraInfoThrottleOptions { MaxRequestsPerSecond = rate }.Validate();

        Assert.Contains(problems, problem => problem.Contains("MaxRequestsPerSecond", StringComparison.Ordinal));
    }

    [Fact]
    public void ATotalTimeoutThatIsNotLongerThanOneAttemptIsRefusedWithTheReason()
    {
        // The resilience package validates this itself and fails at start-up. Saying it here says WHY, which
        // the package's message does not: the total timeout silently becomes the real retry limit, and the
        // configured attempts stop happening with nothing naming the setting responsible.
        IReadOnlyList<string> problems = new RcraInfoThrottleOptions
        {
            AttemptTimeout = TimeSpan.FromSeconds(30),
            TotalRequestTimeout = TimeSpan.FromSeconds(30),
        }.Validate();

        Assert.Contains(problems, problem => problem.Contains("retry limit", StringComparison.Ordinal));
    }

    [Fact]
    public void AMaxDelayBelowTheBaseDelayIsRefused()
    {
        IReadOnlyList<string> problems = new RcraInfoThrottleOptions
        {
            RetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromSeconds(5),
        }.Validate();

        Assert.Contains(problems, problem => problem.Contains("MaxRetryDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroRetriesIsLegalAndMeansOneAttempt()
    {
        // Not every deployment wants a ladder. A validator that refused zero would force the retry policy to
        // be turned off somewhere less visible than the setting named after it.
        Assert.Empty(new RcraInfoThrottleOptions { MaxRetryAttempts = 0 }.Validate());
    }

    [Fact]
    public void NoSettingIsASecretSoTheWholeSectionIsSafeToLog()
    {
        // ToString exists to be written to a start-up log. Unlike RcraInfoApiOptions -- whose BaseAddress
        // sits one concatenation away from a credentialed auth URI -- there is nothing in this section that
        // could ever be one, and the assertion is here so that a setting added later has to face the question.
        string line = new RcraInfoThrottleOptions().ToString();

        Assert.Contains("MaxRequestsPerSecond = 2", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Key", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSectionIsNestedUnderTheApiSectionSoOneEnvironmentPrefixConfiguresBoth()
    {
        Assert.Equal("RCRAInfoApi:Throttle", RcraInfoThrottleOptions.SectionName);
    }

    [Fact]
    public void TheThrottleSectionIsBoundAndValidatedAtStartup()
    {
        using ServiceProvider provider = Build(
            HttpStatusCode.OK,
            "{}",
            settings: new Dictionary<string, string?>
            {
                ["RCRAInfoApi:Throttle:MaxRequestsPerSecond"] = "0",
            });

        // Same reason as every other section: a scheduled task that fails on its first HTTP call at 2am has
        // already burned the window it was given.
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RcraInfoThrottleOptions>>().Value);
    }

    [Fact]
    public void AConfiguredRateReachesThePacerRatherThanStoppingAtTheOptionsType()
    {
        using ServiceProvider provider = Build(
            HttpStatusCode.OK,
            "{}",
            settings: new Dictionary<string, string?>
            {
                ["RCRAInfoApi:Throttle:MaxRequestsPerSecond"] = "4",
            });

        RequestPacer pacer = provider.GetRequiredService<RequestPacer>();

        // Both reservations are made at the same instant, and that instant is the pacer's own baseline: this
        // pacer was built by the container on TimeProvider.System, so a literal here would be measured
        // against the real clock and the assertion would be about how far 1970 is from today.
        DateTimeOffset now = pacer.NextPermittedUtc;

        Assert.Equal(TimeSpan.Zero, pacer.Reserve(now));
        Assert.Equal(TimeSpan.FromMilliseconds(250), pacer.Reserve(now));
    }

    [Fact]
    public void ThePacerIsASingletonBecauseTwoPacersAreTwoRateLimits()
    {
        using ServiceProvider provider = Build(HttpStatusCode.OK, "{}");

        Assert.Same(
            provider.GetRequiredService<RequestPacer>(),
            provider.GetRequiredService<RequestPacer>());
    }

    [Fact]
    public async Task TheConfiguredRetryCountReachesTheRealPipeline()
    {
        // The whole point of the file. This pipeline has been running on package defaults since §D1, and a
        // configuration value that never arrives is indistinguishable from one that does -- until somebody
        // edits appsettings.json to slow a load down and nothing changes.
        StubHandler data = StubHandler.Always(HttpStatusCode.ServiceUnavailable, "{}");

        using ServiceProvider provider = Build(
            HttpStatusCode.OK,
            """{"token":"secret-token","expiration":"2026-09-06T02:20:00Z"}""",
            dataHandler: data,
            settings: new Dictionary<string, string?>
            {
                ["RCRAInfoApi:Throttle:MaxRetryAttempts"] = "2",

                // Milliseconds, not the configured seconds: the ladder being exercised is the COUNT. A test
                // that waited out 2s + 4s of real backoff would prove the same thing six seconds later.
                ["RCRAInfoApi:Throttle:RetryDelay"] = "00:00:00.001",
                ["RCRAInfoApi:Throttle:MaxRetryDelay"] = "00:00:00.010",
                ["RCRAInfoApi:Throttle:MaxRequestsPerSecond"] = "1000",
            });

        ApiFetchResult result = await provider
            .GetRequiredService<IRcraInfoDataClient>()
            .FetchAsync(RcraInfoDataRequest.OtherIds("MDD000000001"));

        // One attempt plus two retries. And the outcome is still reported rather than thrown: everything the
        // pipeline gives up on arrives at the client as an ApiFetchResult to be journaled.
        Assert.Equal(3, data.Calls);
        Assert.Equal(ApiFetchOutcome.ServiceFailure, result.Outcome);
    }

    [Fact]
    public async Task EveryRetryIsPacedBecauseTheGateSitsInsideTheResilienceHandler()
    {
        // The ordering decision, asserted. If the pacing handler were outside the resilience handler, one
        // logical request would take one slot and its retries would leave unpaced -- three requests in the
        // same instant, at exactly the moment EPA has said it is unhappy.
        StubHandler data = StubHandler.Always(HttpStatusCode.ServiceUnavailable, "{}");

        using ServiceProvider provider = Build(
            HttpStatusCode.OK,
            """{"token":"secret-token","expiration":"2026-09-06T02:20:00Z"}""",
            dataHandler: data,
            settings: new Dictionary<string, string?>
            {
                ["RCRAInfoApi:Throttle:MaxRetryAttempts"] = "2",
                ["RCRAInfoApi:Throttle:RetryDelay"] = "00:00:00.001",
                ["RCRAInfoApi:Throttle:MaxRetryDelay"] = "00:00:00.010",
                ["RCRAInfoApi:Throttle:MaxRequestsPerSecond"] = "1000",
            });

        RequestPacer pacer = provider.GetRequiredService<RequestPacer>();

        await provider
            .GetRequiredService<IRcraInfoDataClient>()
            .FetchAsync(RcraInfoDataRequest.OtherIds("MDD000000001"));

        // One reservation per HTTP request EPA actually received, retries included.
        Assert.Equal(data.Calls, pacer.Reservations);
        Assert.Equal(3, pacer.Reservations);
    }

    [Fact]
    public async Task TheAuthCallIsNotPacedBecauseItIsOnePerTokenLifetime()
    {
        // Deliberate, and worth pinning: EPA's tokens live 20 minutes, so the auth client's traffic is a
        // rounding error against several hundred thousand data calls, and putting it behind the same gate
        // would mean a token renewal queuing behind data requests to renew the credential they need.
        StubHandler data = StubHandler.Always(HttpStatusCode.OK, "[]");

        using ServiceProvider provider = Build(
            HttpStatusCode.OK,
            """{"token":"secret-token","expiration":"2026-09-06T02:20:00Z"}""",
            dataHandler: data,
            settings: new Dictionary<string, string?>
            {
                ["RCRAInfoApi:Throttle:MaxRequestsPerSecond"] = "1000",
            });

        await provider
            .GetRequiredService<IRcraInfoDataClient>()
            .FetchAsync(RcraInfoDataRequest.OtherIds("MDD000000001"));

        // One data request, one reservation. The auth call that fetched the bearer token took none.
        Assert.Equal(1, provider.GetRequiredService<RequestPacer>().Reservations);
    }

    private static ServiceProvider Build(
        HttpStatusCode status,
        string body,
        StubHandler? dataHandler = null,
        Dictionary<string, string?>? settings = null)
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton(new ApplicationCredentials("sql-password", ApiId, ApiKey));

        Dictionary<string, string?> configured = new(StringComparer.Ordinal)
        {
            ["RCRAInfoApi:BaseAddress"] = Base,
        };

        if (settings is not null)
        {
            foreach ((string key, string? value) in settings)
            {
                configured[key] = value;
            }
        }

        services.AddRcraInfoApi(
            new ConfigurationBuilder().AddInMemoryCollection(configured).Build());

        services.AddHttpClient(RcraInfoApiClients.Auth)
            .ConfigurePrimaryHttpMessageHandler(() => StubHandler.Always(status, body));

        services.AddHttpClient(RcraInfoApiClients.Data)
            .ConfigurePrimaryHttpMessageHandler(
                () => dataHandler ?? StubHandler.Always(status, body));

        return services.BuildServiceProvider();
    }
}
