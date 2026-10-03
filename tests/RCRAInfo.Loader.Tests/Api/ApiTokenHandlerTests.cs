using System.Net;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The bearer header, and the one retry plan §D1 requires: on a <c>401</c> from a data endpoint, renew
/// the token and try again — once.
/// </summary>
public class ApiTokenHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheTokenIsPresentedAsABearerHeader()
    {
        StubHandler transport = StubHandler.Always(HttpStatusCode.OK, "{}");
        ScriptedTokenProvider tokens = new(Token("first"));

        using HttpResponseMessage response = await SendAsync(transport, tokens);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The scheme comes from EPA's wiki and not from the pinned spec, which declares that a token is
        // required and leaves securityDefinitions null. If a live call ever refuses a token known good, this
        // is the first line to doubt.
        Assert.Equal("Bearer first", Assert.Single(transport.Authorizations));
        Assert.Equal(1, tokens.Gets);
        Assert.Equal(0, tokens.Refreshes);
    }

    [Fact]
    public async Task A401IsRetriedOnceWithARenewedToken()
    {
        StubHandler transport = new((_, ordinal) => StubHandler.Respond(
            ordinal == 0 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            "{}"));

        ScriptedTokenProvider tokens = new(Token("stale"), Token("fresh"));

        using HttpResponseMessage response = await SendAsync(transport, tokens);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(["Bearer stale", "Bearer fresh"], transport.Authorizations);

        // The rejected token is handed to the provider, which is how it tells "renew this" from "someone
        // already renewed it" without a second, pointless auth call.
        Assert.Equal("stale", tokens.LastRejected?.Value);
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task ASecond401IsNotRetriedAgain()
    {
        StubHandler transport = StubHandler.Always(HttpStatusCode.Unauthorized, "{}");
        ScriptedTokenProvider tokens = new(Token("stale"), Token("fresh"));

        using HttpResponseMessage response = await SendAsync(transport, tokens);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Two attempts, not a loop. A renew-and-retry cycle against an endpoint that keeps refusing is how an
        // API account gets throttled, and the second refusal is evidence the token was never the problem.
        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task A401IsReturnedUntouchedWhenTheProviderDeclinesToRenew()
    {
        // The rate floor: the token was issued moments ago, so the provider hands the same one back. Sending
        // it again would produce the same 401 and cost a round-trip to prove it.
        StubHandler transport = StubHandler.Always(HttpStatusCode.Unauthorized, "{}");
        ScriptedTokenProvider tokens = new(Token("just-issued")) { RefreshDeclines = true };

        using HttpResponseMessage response = await SendAsync(transport, tokens);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task ARequestWithABodyIsNotRetried()
    {
        // Every RCRAInfo call this application makes is a GET, so this costs nothing today. Without it, the
        // first POST added later would be retried with a consumed content stream and fail as a service error.
        StubHandler transport = StubHandler.Always(HttpStatusCode.Unauthorized, "{}");
        ScriptedTokenProvider tokens = new(Token("stale"), Token("fresh"));

        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/hd/sources/summaries")
        {
            Content = new StringContent("{}"),
        };

        using HttpResponseMessage response = await SendAsync(transport, tokens, request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(0, tokens.Refreshes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NothingButA401IsRetried(HttpStatusCode status)
    {
        StubHandler transport = StubHandler.Always(status, "{}");
        ScriptedTokenProvider tokens = new(Token("first"));

        using HttpResponseMessage response = await SendAsync(transport, tokens);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, transport.Calls);

        // A 403 in particular: renewing the token cannot widen an account's scope, and trying teaches an
        // operator to read a permissions problem as a flaky one.
        Assert.Equal(0, tokens.Refreshes);
    }

    private static ApiToken Token(string value) => new(value, Now, Now.AddMinutes(20));

    private static async Task<HttpResponseMessage> SendAsync(
        StubHandler transport,
        ScriptedTokenProvider tokens,
        HttpRequestMessage? request = null)
    {
        using ApiTokenHandler handler = new(tokens) { InnerHandler = transport };
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://rcranodepreprod.epa.gov/rcra-api/rest/"),
        };

        return await client.SendAsync(
            request ?? new HttpRequestMessage(HttpMethod.Get, "api/v1/hd/sources/summaries"));
    }
}
