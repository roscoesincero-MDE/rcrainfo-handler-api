using Microsoft.Extensions.Options;

using RCRAInfo.Core.Credentials;
using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// Caching, proactive renewal, one renewal at a time, and the floor on the auth-call rate. Every one of
/// these is measured by <see cref="RcraInfoTokenProvider.AuthenticationCount"/>, which exists because a
/// behaviour that cannot be counted cannot be tested.
/// </summary>
public class RcraInfoTokenProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheFirstCallAuthenticatesAndTheSecondDoesNot()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock _) = Build();

        using RcraInfoTokenProvider disposable = provider;

        ApiToken first = await provider.GetTokenAsync();
        ApiToken second = await provider.GetTokenAsync();

        Assert.Same(first, second);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, provider.AuthenticationCount);
    }

    [Fact]
    public async Task TheTokenIsRenewedBeforeItExpiresAndNotAfter()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock clock) = Build();

        using RcraInfoTokenProvider disposable = provider;

        ApiToken first = await provider.GetTokenAsync();

        // 17 minutes into a 20-minute token: inside the token's life, outside the two-minute margin.
        clock.Advance(TimeSpan.FromMinutes(17));
        Assert.Same(first, await provider.GetTokenAsync());
        Assert.Equal(1, client.Calls);

        // 18 minutes: the margin has been reached. Renewed here rather than at 20, because a request that
        // starts at 19:59 and arrives at 20:01 is a 401 nobody needed.
        clock.Advance(TimeSpan.FromMinutes(1));
        ApiToken renewed = await provider.GetTokenAsync();

        Assert.NotSame(first, renewed);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task TwentyConcurrentCallersProduceOneAuthentication()
    {
        // D2 fetches with bounded concurrency, so every worker crosses the expiry boundary within a few
        // milliseconds of the others. Without the gate this is N auth calls at once, aimed by us at the one
        // endpoint whose failure stops the whole load.
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock _) = Build();

        using RcraInfoTokenProvider disposable = provider;

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Gate = gate;

        Task<ApiToken>[] callers = [.. Enumerable.Range(0, 20).Select(_ => provider.GetTokenAsync())];

        // Every caller is now either inside the one authentication or waiting on the semaphore.
        gate.SetResult();

        ApiToken[] tokens = await Task.WhenAll(callers);

        Assert.Equal(1, client.Calls);
        Assert.All(tokens, token => Assert.Same(tokens[0], token));
    }

    [Fact]
    public async Task ARejectedTokenIsAlwaysReplacedEvenIfItWasJustIssued()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock _) = Build();

        using RcraInfoTokenProvider disposable = provider;

        ApiToken first = await provider.GetTokenAsync();
        ApiToken second = await provider.RefreshAsync(first);

        // The 401 path has evidence rather than arithmetic, so the rate floor does not apply to it. If it
        // did, the retry plan §D1 requires would silently become a re-send of the token that just failed.
        Assert.NotSame(first, second);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ACallerThatLostTheRaceIsHandedTheNewTokenInsteadOfAuthenticatingAgain()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock _) = Build();

        using RcraInfoTokenProvider disposable = provider;

        ApiToken first = await provider.GetTokenAsync();
        ApiToken renewed = await provider.RefreshAsync(first);

        // A second worker that hit a 401 on the OLD token arrives after the renewal. Its token is stale, the
        // cached one is not, so it takes the cached one -- one 401 per worker per expiry, not one auth call.
        ApiToken handedBack = await provider.RefreshAsync(first);

        Assert.Same(renewed, handedBack);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ATokenThatArrivesAlreadyExpiredDoesNotCauseAnAuthenticationPerRequest()
    {
        // What a clock disagreement with EPA looks like from here. Without the floor, every request fetches a
        // token, finds it expired, and fetches another -- a denial of service aimed at EPA and authored here.
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock clock) =
            Build(lifetime: TimeSpan.FromMinutes(-30));

        using RcraInfoTokenProvider disposable = provider;

        for (int i = 0; i < 25; i++)
        {
            await provider.GetTokenAsync();
        }

        Assert.Equal(1, client.Calls);

        // Past the five-second floor, one more attempt is allowed. A trickle, not a storm, and the load keeps
        // running on the "expired" token -- which is what the 401 retry path is for.
        clock.Advance(TimeSpan.FromSeconds(6));
        await provider.GetTokenAsync();

        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ARejectedCredentialThrowsAndIsNotRetried()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock _) = Build(
            script: _ => new ApiAuthResult(
                ApiAuthOutcome.InvalidCredentials,
                null,
                "EPA rejected the RCRAInfo API ID and Key (401).",
                401,
                ApiError.InvalidCredentialsCode));

        using RcraInfoTokenProvider disposable = provider;

        RcraInfoAuthException error =
            await Assert.ThrowsAsync<RcraInfoAuthException>(() => provider.GetTokenAsync());

        Assert.Equal(ApiAuthOutcome.InvalidCredentials, error.Outcome);
        Assert.Equal(ApiError.InvalidCredentialsCode, error.ErrorCode);
        Assert.False(error.IsRetryable);

        // One attempt. AR4 recorded this rule for the SQL login after a lockout threshold of three was
        // measured on the domain; an EPA account nobody here administers gets the same treatment.
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task AFailureIsNotCachedSoALaterCallCanSucceed()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient client, TestClock _) = Build(
            script: ordinal => ordinal == 0
                ? new ApiAuthResult(ApiAuthOutcome.ServiceFailure, null, "EPA failed (503).", 503)
                : Issued(Start, TimeSpan.FromMinutes(20)));

        using RcraInfoTokenProvider disposable = provider;

        await Assert.ThrowsAsync<RcraInfoAuthException>(() => provider.GetTokenAsync());

        ApiToken token = await provider.GetTokenAsync();

        Assert.Equal("token-1", token.Value);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public void CredentialsWithNoApiPairAreRefusedAtConstruction()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RcraInfoTokenProvider(
                new ScriptedAuthClient(_ => Issued(Start, TimeSpan.FromMinutes(20))),
                new ApplicationCredentials("sql-password", null, null),
                Options.Create(new RcraInfoApiOptions { BaseAddress = "https://h/rcra-api/rest" }),
                new TestClock(Start)));

        // The monitoring application's credential file, seeded into the loader. Refused at construction so
        // the run fails at start-up naming the template, rather than on the first fetch at 2am.
        Assert.Contains("secrets.Template.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshingAfterDisposalIsRefusedRatherThanHanging()
    {
        (RcraInfoTokenProvider provider, ScriptedAuthClient _, TestClock _) = Build();

        provider.Dispose();
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.RefreshAsync(null));
    }

    private static ApiAuthResult Issued(DateTimeOffset issuedAt, TimeSpan lifetime, int ordinal = 1) =>
        new(ApiAuthOutcome.Succeeded,
            new ApiToken($"token-{ordinal}", issuedAt, issuedAt + lifetime),
            "EPA issued a bearer token.",
            200);

    private static (RcraInfoTokenProvider Provider, ScriptedAuthClient Client, TestClock Clock) Build(
        TimeSpan? lifetime = null,
        Func<int, ApiAuthResult>? script = null)
    {
        TestClock clock = new(Start);
        TimeSpan life = lifetime ?? TimeSpan.FromMinutes(20);

        ScriptedAuthClient client = new(script ?? (ordinal => Issued(clock.Now, life, ordinal + 1)));

        RcraInfoTokenProvider provider = new(
            client,
            new ApplicationCredentials("sql-password", "MDTESTAPIID00001", "MDTESTAPIKEY-0123456789"),
            Options.Create(new RcraInfoApiOptions
            {
                BaseAddress = "https://rcranodepreprod.epa.gov/rcra-api/rest",
            }),
            clock);

        return (provider, client, clock);
    }
}
