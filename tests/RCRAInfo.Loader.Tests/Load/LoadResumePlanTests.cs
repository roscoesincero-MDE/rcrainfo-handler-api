using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The subtraction at the centre of D2.7, driven directly because it is pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sets go in and three come out, and the third one is the reason the resume set carries
/// unfinished versions at all.</b> <c>ToFetch</c> and <c>ToSkip</c> are what the run acts on;
/// <c>Vanished</c> is a finding it cannot act on — versions the previous run knew about that this run's
/// summaries walk did not name. EPA does not publish which date field <c>/hd/sources/summaries</c>
/// filters on (G25), and this comparison is the only place in the application where the two sets are held
/// at once, so it is the only place that discrepancy can be seen.
/// </para>
/// <para>
/// Kept apart from <see cref="LoadResumeTests"/> deliberately: the arithmetic is where the mistakes would
/// be, and it needs neither a database nor a clock nor a logger to exercise.
/// </para>
/// </remarks>
public class LoadResumePlanTests
{
    private static readonly HandlerVersion One = new("MDD000000001", "N", 1);
    private static readonly HandlerVersion Two = new("MDD000000002", "N", 1);
    private static readonly HandlerVersion Three = new("MDD000000003", "N", 1);

    [Fact]
    public void WithNoResumePointEverythingIsFetchedAndNothingIsSkipped()
    {
        // The first-run path, and the path four separate "no candidate" answers from script 525 all land
        // on. It has to be the safe one, because it is also what a misconfigured threshold produces.
        LoadResumePlan plan = LoadResumePlan.FetchAll([One, Two, Three]);

        Assert.Equal([One, Two, Three], plan.ToFetch);
        Assert.Empty(plan.ToSkip);
        Assert.Empty(plan.Vanished);
        Assert.Equal(0, plan.SkipRate);
        Assert.False(plan.Point.HasResumePoint);
    }

    [Fact]
    public void AVersionThePreviousRunFinishedIsSkippedAndTheRestAreFetched()
    {
        LoadResumePoint point = Point(completed: [One], unfinished: [Two]);

        LoadResumePlan plan = point.Plan([One, Two, Three]);

        Assert.Equal([One], plan.ToSkip);

        // Two is in the resume set and is still fetched, because 'Failed' is not 'Succeeded'. That is the
        // whole point of resuming from a status rather than from a page number.
        Assert.Equal([Two, Three], plan.ToFetch);
    }

    [Fact]
    public void TheWalkOrderIsKeptSoTheFetchLoopIsReproducible()
    {
        // Not cosmetic: the fetch order decides what a run that is killed again has got through, so a
        // reproducible order is what makes a second resume test assertable at all.
        LoadResumePoint point = Point(completed: [Two]);

        LoadResumePlan plan = point.Plan([Three, Two, One]);

        Assert.Equal([Three, One], plan.ToFetch);
    }

    [Fact]
    public void ARepeatedVersionIsCountedOnceBecauseScriptFiveTwentyThrowsOnADuplicate()
    {
        // SummaryWalkReport.Versions is already de-duplicated, so this is belt and braces -- but the
        // consequence of a repeat is not a slow load: script 520 refuses a payload naming one key twice,
        // which fails the whole flush and loses the journal for every other handler in it.
        LoadResumePlan plan = LoadResumePlan.FetchAll([One, One, Two, One]);

        Assert.Equal([One, Two], plan.ToFetch);
        Assert.Equal(2, plan.WalkedCount);
    }

    [Fact]
    public void AVersionThePreviousRunKnewAboutAndTheWalkDidNotNameIsReportedAsVanished()
    {
        // The G25 signal. The abandoned run did not advance the watermark, so this run asked EPA for the
        // same range -- which means the walk should have named at least what that run did. A version
        // missing from it means the summaries feed has stopped reporting a record it reported before.
        LoadResumePoint point = Point(completed: [One], unfinished: [Two]);

        LoadResumePlan plan = point.Plan([One]);

        Assert.Equal([Two], plan.Vanished);
    }

    [Fact]
    public void VanishedCoversBothSetsBecauseTheOldRunsOutcomeSaysNothingAboutEpasFeed()
    {
        // Whether the previous run succeeded on a version has no bearing on whether EPA still reports it.
        // Filtering Vanished to the completed set would hide exactly half of the evidence.
        LoadResumePoint point = Point(completed: [One], unfinished: [Two]);

        LoadResumePlan plan = point.Plan([Three]);

        Assert.Equal([One, Two], plan.Vanished);
        Assert.Equal([Three], plan.ToFetch);
    }

    [Fact]
    public void VanishedIsInKeyOrderRatherThanInWhicheverOrderTheSetsIterate()
    {
        // A HashSet's order is not contractual. Two runs of the same load would otherwise report the same
        // finding in a different order, which is enough to make it look like a different finding.
        LoadResumePoint point = Point(completed: [Three, One], unfinished: [Two]);

        LoadResumePlan plan = point.Plan([]);

        Assert.Equal([One, Two, Three], plan.Vanished);
    }

    [Fact]
    public void TheSkipRateIsThePercentageOfTheWalkThisRunDoesNotHaveToRequest()
    {
        // The number that says whether resume is working at all. A resumed run reporting 0% while holding
        // a resume point is the defect this exists to make visible.
        LoadResumePoint point = Point(completed: [One, Two]);

        LoadResumePlan plan = point.Plan([One, Two, Three]);

        Assert.Equal(2, plan.ToSkip.Count);
        Assert.Equal(3, plan.WalkedCount);
        Assert.Equal(200.0 / 3, plan.SkipRate, 6);
    }

    [Fact]
    public void AnEmptyWalkIsNotADivisionByZero()
    {
        Assert.Equal(0, LoadResumePlan.FetchAll([]).SkipRate);
    }

    [Fact]
    public void TheStringFormCarriesCountsAndARunNumberAndNoHandlerIdentifier()
    {
        // Written to the run log. AR8's rule for logs.ExecutionLog.KeyParameters is applied to this
        // application's own log as well, and the versions themselves are in logs.HandlerLoadStatus where
        // the run number this line carries will find them.
        LoadResumePoint point = Point(completed: [One], unfinished: [Two]);

        string line = point.Plan([One, Three]).ToString();

        Assert.Contains("resume from run 100", line, StringComparison.Ordinal);
        Assert.Contains("1 to fetch", line, StringComparison.Ordinal);
        Assert.Contains("1 to skip", line, StringComparison.Ordinal);
        Assert.Contains("1 vanished", line, StringComparison.Ordinal);
        Assert.DoesNotContain("MDD0000", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAResumePointTheStringFormSaysSoRatherThanPrintingAnEmptyRun()
    {
        Assert.StartsWith("no resume point", LoadResumePlan.FetchAll([One]).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANullWalkIsARefusalRatherThanAnEmptyPlan()
    {
        // An empty plan would mean "fetch nothing", which is a successful-looking run that loaded nothing.
        Assert.Throws<ArgumentNullException>(() => LoadResumePoint.None.Plan(null!));
    }

    private static LoadResumePoint Point(
        HandlerVersion[]? completed = null,
        HandlerVersion[]? unfinished = null) =>
        new(
            100,
            "Full",
            "Abandoned",
            new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 8, 30),
            new DateOnly(2026, 9, 5),
            new HashSet<HandlerVersion>(completed ?? []),
            new HashSet<HandlerVersion>(unfinished ?? []));
}
