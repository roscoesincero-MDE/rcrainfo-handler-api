using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The options carry three decisions that would otherwise be invisible: no default base address, no
/// plaintext HTTP off the machine, and a refresh margin that cannot exceed half a token's life.
/// </summary>
public class RcraInfoApiOptionsTests
{
    private const string Preprod = "https://rcranodepreprod.epa.gov/rcra-api/rest";

    [Fact]
    public void AnAbsentBaseAddressIsRefusedAndTheMessageNamesBothEnvironments()
    {
        RcraInfoApiOptions options = new();

        Assert.False(options.TryGetBaseUri(out Uri? uri, out string? problem));
        Assert.Null(uri);

        // The operator reading this has one job -- paste the right URL -- and the failure they must not make
        // is pasting the other environment's. Both are in the message so neither has to be looked up.
        Assert.Contains("rcranodepreprod.epa.gov", problem!, StringComparison.Ordinal);
        Assert.Contains("rcranode.epa.gov", problem!, StringComparison.Ordinal);
        Assert.Contains("no default", problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATrailingSlashIsAddedBecauseRelativeResolutionDropsTheLastSegment()
    {
        RcraInfoApiOptions options = new() { BaseAddress = Preprod };

        Assert.True(options.TryGetBaseUri(out Uri? uri, out _));
        Assert.EndsWith("/rcra-api/rest/", uri!.AbsoluteUri, StringComparison.Ordinal);

        // The reason the slash matters, asserted rather than described: without it the auth call would go to
        // /rcra-api/api/v1/auth/... and EPA would answer 404 from a base address that reads correctly.
        Assert.Equal(
            "https://rcranodepreprod.epa.gov/rcra-api/rest/api/v1/auth/A/B",
            new Uri(uri, "api/v1/auth/A/B").AbsoluteUri);
    }

    [Fact]
    public void ATrailingSlashAlreadyThereIsNotDoubled()
    {
        RcraInfoApiOptions options = new() { BaseAddress = Preprod + "/" };

        Assert.True(options.TryGetBaseUri(out Uri? uri, out _));
        Assert.Equal(Preprod + "/", uri!.AbsoluteUri);
    }

    [Fact]
    public void PlaintextHttpToEpaIsRefusedBecauseTheKeyIsInThePath()
    {
        RcraInfoApiOptions options = new() { BaseAddress = "http://rcranodepreprod.epa.gov/rcra-api/rest" };

        Assert.False(options.TryGetBaseUri(out _, out string? problem));
        Assert.Contains("clear text", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextHttpToLoopbackIsAllowedBecauseThatIsHowThisIsTested()
    {
        RcraInfoApiOptions options = new() { BaseAddress = "http://127.0.0.1:5000/rcra-api/rest" };

        Assert.True(options.TryGetBaseUri(out _, out string? problem));
        Assert.Null(problem);
    }

    [Fact]
    public void SomethingThatIsNotAUriIsRefused()
    {
        RcraInfoApiOptions options = new() { BaseAddress = "rcranode.epa.gov/rcra-api/rest" };

        Assert.False(options.TryGetBaseUri(out _, out string? problem));
        Assert.Contains("absolute", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReportsEveryProblemAtOnce()
    {
        RcraInfoApiOptions options = new()
        {
            BaseAddress = string.Empty,
            RequestTimeout = TimeSpan.Zero,
            TokenRefreshMargin = TimeSpan.FromSeconds(-1),
            MinimumRefreshInterval = TimeSpan.FromSeconds(-1),
        };

        // All four, not the first: a start-up failure that names one problem per run costs one run per
        // problem, and these runs are scheduled overnight.
        Assert.Equal(4, options.Validate().Count);
    }

    [Fact]
    public void ValidateAcceptsTheDefaultsOnceABaseAddressIsSupplied()
    {
        RcraInfoApiOptions options = new() { BaseAddress = Preprod };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheMarginIsAppliedToATwentyMinuteToken()
    {
        DateTimeOffset issued = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
        RcraInfoApiOptions options = new() { TokenRefreshMargin = TimeSpan.FromMinutes(2) };

        Assert.Equal(
            issued.AddMinutes(18),
            options.RefreshDueAt(issued, issued.AddMinutes(20)));
    }

    [Fact]
    public void TheMarginIsCappedAtHalfTheTokensLife()
    {
        DateTimeOffset issued = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
        RcraInfoApiOptions options = new() { TokenRefreshMargin = TimeSpan.FromMinutes(2) };

        // A one-minute token with a two-minute margin would otherwise be due before it was issued, and every
        // single request would fetch a new one. Half its life instead: at most two auth calls per lifetime,
        // whatever lifetime EPA hands out.
        Assert.Equal(
            issued.AddSeconds(30),
            options.RefreshDueAt(issued, issued.AddMinutes(1)));
    }

    [Fact]
    public void ATokenThatArrivesExpiredIsDueImmediatelyAndNotEarlier()
    {
        DateTimeOffset issued = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
        RcraInfoApiOptions options = new();
        DateTimeOffset expired = issued.AddMinutes(-30);

        // Not "issued minus the margin", which would be a time before the token existed and would make the
        // arithmetic in the provider harder to reason about. Due at its own expiry; the rate floor is what
        // keeps that from becoming a loop.
        Assert.Equal(expired, options.RefreshDueAt(issued, expired));
    }

    [Fact]
    public void ToStringNamesTheEnvironmentAndTheTimings()
    {
        RcraInfoApiOptions options = new() { BaseAddress = Preprod };

        Assert.Contains(Preprod, options.ToString(), StringComparison.Ordinal);
        Assert.Contains("00:02:00", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToStringSaysSoWhenTheBaseAddressIsMissing()
    {
        Assert.Contains("<not configured>", new RcraInfoApiOptions().ToString(), StringComparison.Ordinal);
    }
}
