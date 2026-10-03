using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Core.Credentials;
using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The measured leak, and the registration that closes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was measured.</b> With stock <c>IHttpClientFactory</c> logging, one request produces eight log
/// entries, two of them at <b>Information</b>: <c>Start processing HTTP request GET {uri}</c> and
/// <c>Sending HTTP request GET {uri}</c> — the full URI. For the auth call that URI contains the API ID
/// and the API Key, because EPA's auth endpoint takes both as path segments. A third hazard sits at
/// <c>Trace</c>, where the same logging writes request headers, which for a data call is
/// <c>Authorization: Bearer …</c>.
/// </para>
/// <para>
/// The tests below are the reason the fix is trusted rather than assumed: the first proves that logging
/// really would carry the credential, and the second and third prove that the registration does not. A
/// control that has never been seen to be needed is indistinguishable from one that does nothing.
/// </para>
/// </remarks>
public class ApiRegistrationLoggingTests
{
    private const string ApiId = "MDTESTAPIID00001";
    private const string ApiKey = "MDTESTAPIKEY-0123456789";
    private const string Base = "https://rcranodepreprod.epa.gov/rcra-api/rest";

    [Fact]
    public async Task StockLoggingWouldPutTheApiKeyInTheLog()
    {
        // Deliberately NOT AddRcraInfoApi: this is the default that had to be turned off, and it is here so
        // that the two tests below are known to be testing something.
        Capture capture = new();
        ServiceCollection services = new();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddHttpClient("stock", client => client.BaseAddress = new Uri(Base + "/"))
            .ConfigurePrimaryHttpMessageHandler(() => StubHandler.Always(HttpStatusCode.OK, "{}"));

        using ServiceProvider provider = services.BuildServiceProvider();

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("stock");
        using HttpResponseMessage response = await client.GetAsync($"api/v1/auth/{ApiId}/{ApiKey}");

        Assert.Contains(capture.Lines, line => line.Contains(ApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAuthClientLogsNothingAtAll()
    {
        Capture capture = new();
        using ServiceProvider provider = Build(capture, HttpStatusCode.OK, "{\"token\":\"secret-token\"}");

        IRcraInfoAuthClient client = provider.GetRequiredService<IRcraInfoAuthClient>();

        ApiAuthResult result = await client.AuthenticateAsync(ApiId, ApiKey);

        Assert.True(result.Succeeded);

        // Not "no line contains the key" but "no line at all". The distinction matters: a filter that redacts
        // known-secret shapes has to be right about every shape, while a client with no logger attached has
        // nothing to be right about.
        Assert.Empty(capture.Lines);
    }

    [Fact]
    public async Task TheDataClientLogsNeitherTheBearerTokenNorItsRequest()
    {
        Capture capture = new();
        using ServiceProvider provider = Build(capture, HttpStatusCode.OK, "{\"token\":\"secret-token\"}");

        using HttpClient client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(RcraInfoApiClients.Data);

        using HttpResponseMessage response = await client.GetAsync("api/v1/hd/sources/summaries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Polly's resilience telemetry is still attached to this client and still logs -- measured to report
        // the pipeline, the attempt and the status code, never the URI. So the assertion is about the secret
        // rather than about silence.
        Assert.DoesNotContain(capture.Lines, line => line.Contains("secret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Lines, line => line.Contains(ApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDataClientIsResolvableThroughItsInterfaceAndClassifiesThroughTheRealPipeline()
    {
        // The registration is a typed client rather than a bare named one, so that the only HttpClient
        // carrying a bearer token is reachable through one interface. This resolves it the way the loader
        // will -- through the container, with the token handler and the resilience handler attached -- which
        // is the part a hand-built HttpClient in the client's own tests cannot cover.
        Capture capture = new();
        using ServiceProvider provider = Build(
            capture,
            HttpStatusCode.OK,
            """{"token":"secret-token","expiration":"2026-09-06T02:20:00Z"}""",
            dataBody: """[{"handlerId":"MDD000000001"}]""");

        IRcraInfoDataClient client = provider.GetRequiredService<IRcraInfoDataClient>();

        ApiFetchResult result = await client.FetchAsync(
            RcraInfoDataRequest.OtherIds("MDD000000001"));

        Assert.Equal(ApiFetchOutcome.Succeeded, result.Outcome);
        Assert.Equal("""[{"handlerId":"MDD000000001"}]""", result.Payload);

        // The bearer token the pipeline attached does not reach a log. Same assertion as the named-client
        // test above and worth repeating here, because a typed registration is a different code path in
        // IHttpClientFactory and RemoveAllLoggers has to apply to it too.
        Assert.DoesNotContain(capture.Lines, line => line.Contains("secret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Lines, line => line.Contains(ApiKey, StringComparison.Ordinal));

        // And the query string does not reach a log either, through any of the handlers in the pipeline.
        Assert.DoesNotContain(capture.Lines, line => line.Contains("handlerId=", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOptionsAreValidatedAtStartupAndNotOnFirstUse()
    {
        ServiceCollection services = new();

        services.AddRcraInfoApi(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RCRAInfoApi:BaseAddress"] = "not-a-uri",
                })
                .Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        // A scheduled task that fails on its first HTTP call at 2am has already burned the window it was
        // given; this fails while the console is still the thing reading the message.
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RcraInfoApiOptions>>().Value);
    }

    /// <param name="dataBody">
    /// The body the <c>rcrainfo-data</c> client answers with, when it differs from the auth client's. The
    /// two are separable because a test that drives the data client through the real pipeline needs the
    /// auth call in front of it to hand back a token, not a data payload.
    /// </param>
    private static ServiceProvider Build(
        Capture capture,
        HttpStatusCode status,
        string body,
        string? dataBody = null)
    {
        ServiceCollection services = new();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));

        // Registered by the host after the AR4 bootstrap has decrypted and validated them, which is the
        // ordering AddRcraInfoApi documents: the bootstrap has to be allowed to fail into an exit code before
        // there is a host at all.
        services.AddSingleton(new ApplicationCredentials("sql-password", ApiId, ApiKey));

        services.AddRcraInfoApi(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RCRAInfoApi:BaseAddress"] = Base,
                })
                .Build());

        services.AddHttpClient(RcraInfoApiClients.Auth)
            .ConfigurePrimaryHttpMessageHandler(() => StubHandler.Always(status, body));

        services.AddHttpClient(RcraInfoApiClients.Data)
            .ConfigurePrimaryHttpMessageHandler(() => StubHandler.Always(status, dataBody ?? body));

        return services.BuildServiceProvider();
    }

    private sealed class Capture : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // The formatted message AND the state: the stock logging puts the URI in a structured field,
                // and a sink that only reads the message would miss a leak that any real structured sink
                // records.
                lock (lines)
                {
                    lines.Add($"[{logLevel}] {category}: {formatter(state, exception)} :: {state}");
                }
            }
        }
    }
}
