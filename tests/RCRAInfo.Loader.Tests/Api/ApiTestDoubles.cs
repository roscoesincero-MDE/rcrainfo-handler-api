using System.Net;
using System.Text;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>A clock the tests move by hand, so token lifetimes cost no wall-clock time.</summary>
/// <remarks>
/// Hand-written rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>'s <c>FakeTimeProvider</c>:
/// the two members below are the whole of what these tests need from a clock, and a package added for
/// two members is a package to keep up to date forever.
/// </remarks>
internal sealed class TestClock : TimeProvider
{
    public TestClock(DateTimeOffset start) => Now = start;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A stubbed transport: answers each request from a script, and records what it was asked.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> respond;
    private int calls;

    public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) =>
        this.respond = respond;

    /// <summary>Every request URI seen, in order. The tests assert on the path this client builds.</summary>
    public List<string> RequestUris { get; } = [];

    /// <summary>Every <c>Authorization</c> header value seen, in order, or "(none)".</summary>
    public List<string> Authorizations { get; } = [];

    public int Calls => calls;

    public static StubHandler Always(HttpStatusCode status, string? body = null) =>
        new((_, _) => Respond(status, body));

    public static StubHandler Throwing(Exception error) =>
        new((_, _) => throw error);

    public static HttpResponseMessage Respond(HttpStatusCode status, string? body) =>
        new(status)
        {
            Content = body is null
                ? new StringContent(string.Empty)
                : new StringContent(body, Encoding.UTF8, "application/json"),
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int ordinal = Interlocked.Increment(ref calls) - 1;

        lock (RequestUris)
        {
            // AbsoluteUri and not ToString(): ToString() un-escapes %20 back to a space, so a test asserting
            // on escaping would read as a failure to escape when the request on the wire is correct.
            RequestUris.Add(request.RequestUri?.AbsoluteUri ?? "(none)");
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "(none)");
        }

        return Task.FromResult(respond(request, ordinal));
    }
}

/// <summary>A scripted auth client: answers from a queue and counts how often it was called.</summary>
internal sealed class ScriptedAuthClient : IRcraInfoAuthClient
{
    private readonly Func<int, ApiAuthResult> script;
    private int calls;

    public ScriptedAuthClient(Func<int, ApiAuthResult> script) => this.script = script;

    public int Calls => calls;

    /// <summary>Released to let a test hold the first authentication open and start a second one.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ApiAuthResult> AuthenticateAsync(
        string apiId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        int ordinal = Interlocked.Increment(ref calls) - 1;

        if (Gate is not null)
        {
            await Gate.Task;
        }

        return script(ordinal);
    }
}

/// <summary>A token provider the handler tests drive directly.</summary>
internal sealed class ScriptedTokenProvider : IApiTokenProvider
{
    private readonly Queue<ApiToken> tokens;

    public ScriptedTokenProvider(params ApiToken[] tokens) => this.tokens = new Queue<ApiToken>(tokens);

    public int Gets { get; private set; }

    public int Refreshes { get; private set; }

    public ApiToken? LastRejected { get; private set; }

    /// <summary>When true, <see cref="RefreshAsync"/> hands back the same token it was given.</summary>
    public bool RefreshDeclines { get; set; }

    public ApiToken Current { get; private set; } = null!;

    public Task<ApiToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        Gets++;
        Current = tokens.Dequeue();

        return Task.FromResult(Current);
    }

    public Task<ApiToken> RefreshAsync(
        ApiToken? rejected,
        CancellationToken cancellationToken = default)
    {
        Refreshes++;
        LastRejected = rejected;

        if (RefreshDeclines)
        {
            return Task.FromResult(rejected!);
        }

        Current = tokens.Dequeue();

        return Task.FromResult(Current);
    }
}
