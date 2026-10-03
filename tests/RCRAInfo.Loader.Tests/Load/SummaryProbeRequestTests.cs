using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The probed window, and the two ways an operator can ask for one that would produce a misleading number.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both refusals here are about a wrong answer rather than an error.</b> An inverted range comes back from
/// EPA as <c>200</c> with an empty array, so it reads as a quiet fortnight; a range of years is one unpaged
/// request that will most likely time out, and a timeout measures nothing while looking like a measurement
/// failed for network reasons. Neither would announce itself.
/// </para>
/// <para>
/// <b>The date format is the third.</b> <c>06/09/2026</c> is two different days depending on who reads it, and
/// the probe's whole output is a statement about which days were asked for.
/// </para>
/// </remarks>
public sealed class SummaryProbeRequestTests
{
    private const string Switch = "--probe-summaries";

    /// <summary>The ordinary case: two ISO dates, in order.</summary>
    [Fact]
    public void TwoIsoDatesInOrderAreAccepted()
    {
        IReadOnlyList<string> problems =
            SummaryProbeRequest.TryParse("2026-09-01", "2026-09-07", Switch, out SummaryProbeRequest? request);

        Assert.Empty(problems);
        Assert.NotNull(request);
        Assert.Equal(new DateOnly(2026, 9, 1), request.FromDate);
        Assert.Equal(new DateOnly(2026, 9, 7), request.ToDate);
        Assert.Equal(7, request.DayCount);
    }

    /// <summary>A single day is a window of one, not of zero — the range is inclusive at both ends.</summary>
    /// <remarks>
    /// The most likely thing anybody types, and the one where an off-by-one would silently halve every
    /// per-day figure derived from the result.
    /// </remarks>
    [Fact]
    public void OneDayIsAWindowOfOneDay()
    {
        IReadOnlyList<string> problems =
            SummaryProbeRequest.TryParse("2026-09-01", "2026-09-01", Switch, out SummaryProbeRequest? request);

        Assert.Empty(problems);
        Assert.Equal(1, request!.DayCount);
    }

    /// <summary>Anything but <c>yyyy-MM-dd</c> is refused, and the refusal names the format.</summary>
    /// <param name="text">The date as typed.</param>
    [Theory]
    [InlineData("09/01/2026")]
    [InlineData("01/09/2026")]
    [InlineData("2026-9-1")]
    [InlineData("20260901")]
    [InlineData("1 Sep 2026")]
    [InlineData("2026-09-01T00:00:00Z")]
    public void AnAmbiguousOrLooseDateIsRefused(string text)
    {
        IReadOnlyList<string> problems =
            SummaryProbeRequest.TryParse(text, "2026-09-07", Switch, out SummaryProbeRequest? request);

        Assert.Null(request);
        Assert.Contains(problems, problem => problem.Contains("yyyy-MM-dd", StringComparison.Ordinal));
    }

    /// <summary>A date that does not exist is refused rather than rolled forward.</summary>
    /// <remarks>
    /// <c>DateTimeStyles.None</c> with an exact format is what refuses it. A parser that rolled 30 February
    /// into 2 March would report on a window nobody asked for and say nothing about having moved it.
    /// </remarks>
    [Fact]
    public void ADateThatDoesNotExistIsRefused()
    {
        IReadOnlyList<string> problems =
            SummaryProbeRequest.TryParse("2026-02-30", "2026-03-07", Switch, out SummaryProbeRequest? request);

        Assert.Null(request);
        Assert.NotEmpty(problems);
    }

    /// <summary>Both dates are required and the refusal says which one is missing.</summary>
    /// <param name="from">The first value, or null.</param>
    /// <param name="to">The second value, or null.</param>
    /// <param name="expected">The word the message has to contain.</param>
    [Theory]
    [InlineData(null, null, "first")]
    [InlineData("2026-09-01", null, "second")]
    [InlineData(null, "2026-09-07", "first")]
    [InlineData("2026-09-01", "   ", "second")]
    public void BothDatesAreRequiredAndTheRefusalNamesWhichIsMissing(
        string? from,
        string? to,
        string expected)
    {
        IReadOnlyList<string> problems =
            SummaryProbeRequest.TryParse(from, to, Switch, out SummaryProbeRequest? request);

        Assert.Null(request);
        Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
        Assert.All(problems, problem => Assert.Contains(Switch, problem, StringComparison.Ordinal));
    }

    /// <summary>An inverted range is refused, and the refusal explains what EPA would have answered.</summary>
    /// <remarks>
    /// The message names the empty array on purpose. Without it the refusal looks like pedantry about
    /// argument order; with it, the operator knows that the alternative was a number that looked fine.
    /// </remarks>
    [Fact]
    public void AnInvertedRangeIsRefusedAndSaysWhyItMatters()
    {
        IReadOnlyList<string> problems =
            SummaryProbeRequest.TryParse("2026-09-07", "2026-09-01", Switch, out SummaryProbeRequest? request);

        Assert.Null(request);
        Assert.Contains(problems, problem => problem.Contains("empty array", StringComparison.Ordinal));
    }

    /// <summary>The widest accepted window is accepted, and one day more is not.</summary>
    /// <remarks>
    /// Asserted as a pair at the boundary, because a cap tested only well inside or well outside it is a cap
    /// whose actual value nothing checks.
    /// </remarks>
    [Fact]
    public void TheWindowCapIsRefusedOnlyOnceItIsExceeded()
    {
        DateOnly from = new(2026, 1, 1);

        SummaryProbeRequest widest = new(from, from.AddDays(SummaryProbeRequest.MaxWindowDays - 1));
        SummaryProbeRequest tooWide = new(from, from.AddDays(SummaryProbeRequest.MaxWindowDays));

        Assert.Equal(SummaryProbeRequest.MaxWindowDays, widest.DayCount);
        Assert.Empty(widest.Validate());

        Assert.NotEmpty(tooWide.Validate());
        Assert.Contains(
            tooWide.Validate(),
            problem => problem.Contains("no offset and no limit", StringComparison.Ordinal));
    }

    /// <summary>The printed form carries both dates in the one accepted format, and nothing else.</summary>
    [Fact]
    public void ThePrintedFormNamesBothDatesInTheAcceptedFormat()
    {
        string printed = new SummaryProbeRequest(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7)).ToString();

        Assert.Contains("2026-09-01", printed, StringComparison.Ordinal);
        Assert.Contains("2026-09-07", printed, StringComparison.Ordinal);
        Assert.Contains("7 day(s)", printed, StringComparison.Ordinal);
    }
}
