using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Load;
using RCRAInfo.Loader.Tests.Api;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The resume read: script 525's rows turned into two sets, and the arguments it is given.
/// </summary>
/// <remarks>
/// <para>
/// <b>Half of this file is about the arguments, and that is where the real risk in D2.7 sits.</b>
/// <c>AbandonAfterMinutes</c> has to reach <c>logs.uspGetHandlerLoadResumeSet</c> and
/// <c>logs.uspStartLoadRun</c> as the same number, and neither procedure can check the other. Nothing
/// downstream notices a mismatch either: a resume that quietly did not happen looks exactly like a first
/// run, and one that happened when it should not have looks like a fast run. So the only place the value
/// is observable is in the argument, before the call.
/// </para>
/// <para>
/// The other half is the status split, where exactly one value of eleven means "skip".
/// </para>
/// </remarks>
public class LoadResumeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnEmptyResultIsNoResumePointAndSaysSoInTheLog()
    {
        // Four different answers from script 525 arrive as an empty set -- no previous run, one that
        // succeeded, one still alive, one too old. All four mean "fetch everything", which is safe. It is
        // logged rather than passed over because this is also the shape a misconfigured threshold takes,
        // and it produces no error and a successful run.
        (LoadResume resume, RecordingResumeReader reader, RecordingLogger<LoadResume> logger) = Build();

        reader.Rows = [];

        LoadResumePoint point = await resume.ReadAsync();

        Assert.False(point.HasResumePoint);
        Assert.Same(LoadResumePoint.None, point);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("nothing to resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheConfiguredThresholdAndAgeLimitAreBothPassedThrough()
    {
        // The assertion the whole arrangement rests on. One setting, two procedures, and no way for either
        // to detect that the other was given something else.
        (LoadResume resume, RecordingResumeReader reader, _) = Build(
            abandonAfterMinutes: 90, resumeMaxAgeHours: 6);

        await resume.ReadAsync();

        Assert.Equal(("MD", null, 90, 6), Assert.Single(reader.Calls));
    }

    [Fact]
    public async Task AnAbsentAgeLimitIsPassedAsNullRatherThanAsANumberTheReadInvented()
    {
        // Null means no limit, all the way down to the procedure. Substituting a default here would make
        // "re-drive this initial load by hand" silently stop working.
        (LoadResume resume, RecordingResumeReader reader, _) = Build(resumeMaxAgeHours: null);

        await resume.ReadAsync();

        Assert.Null(Assert.Single(reader.Calls).MaxAgeHours);
    }

    [Fact]
    public async Task AnExplicitRunNumberReachesTheProcedureUnchanged()
    {
        (LoadResume resume, RecordingResumeReader reader, _) = Build();

        await resume.ReadAsync(loadRunId: 42);

        Assert.Equal(42, Assert.Single(reader.Calls).LoadRunId);
    }

    [Fact]
    public async Task TheActivityLocationIsNormalisedBeforeItIsSent()
    {
        // Compared against dbo.HandlerSource.ActivityLocation, which the loader writes from EPA's own
        // payload, where it is upper case.
        (LoadResume resume, RecordingResumeReader reader, _) = Build(activityLocation: " md ");

        await resume.ReadAsync();

        Assert.Equal("MD", Assert.Single(reader.Calls).ActivityLocation);
    }

    [Fact]
    public async Task OnlySucceededCountsAsCompletedAndEveryOtherStatusIsUnfinished()
    {
        // One value of the closed set means "do not fetch this again". CK_logs_HandlerLoadStatus_Status
        // admits five, and treating any of the other four as done would leave a version that never
        // finished looking finished forever.
        (LoadResume resume, RecordingResumeReader reader, _) = Build();

        reader.Rows =
        [
            Resumed.Row("MDD000000001", status: "Succeeded"),
            Resumed.Row("MDD000000002", status: "Pending"),
            Resumed.Row("MDD000000003", status: "InProgress"),
            Resumed.Row("MDD000000004", status: "Failed"),
            Resumed.Row("MDD000000005", status: "Skipped"),
        ];

        LoadResumePoint point = await resume.ReadAsync();

        Assert.Equal([new HandlerVersion("MDD000000001", "N", 1)], point.Completed);
        Assert.Equal(4, point.Unfinished.Count);
        Assert.Equal(5, point.KnownCount);
    }

    [Fact]
    public async Task AStatusThisCodeHasNeverHeardOfIsTreatedAsUnfinished()
    {
        // The safe reading of an unknown status is "not known to be done": re-fetching is idempotent and
        // reports Unchanged, and skipping wrongly leaves a stale row nothing will ask about again. The
        // CHECK constraint is the authority for what the column may hold, not this code.
        (LoadResume resume, RecordingResumeReader reader, _) = Build();

        reader.Rows = [Resumed.Row(status: "Quarantined")];

        LoadResumePoint point = await resume.ReadAsync();

        Assert.Empty(point.Completed);
        Assert.Single(point.Unfinished);
    }

    [Fact]
    public async Task TheRunsFactsAreTakenFromTheRowsThatRepeatThem()
    {
        // Script 525 repeats the plan on every row rather than using a second result set or OUTPUT
        // parameters -- outputs are not populated until the rows have been consumed. So any row carries
        // them, and the read has to pick them up rather than leaving the caller to.
        (LoadResume resume, RecordingResumeReader reader, _) = Build();

        reader.Rows = [Resumed.Row("MDD000000001"), Resumed.Row("MDD000000002")];

        LoadResumePoint point = await resume.ReadAsync();

        Assert.Equal(100, point.ResumedFromLoadRunId);
        Assert.Equal("Full", point.ResumedFromRunMode);
        Assert.Equal("Abandoned", point.ResumedFromStatus);
        Assert.Equal(Resumed.Started, point.ResumedFromStartedDateUtc);
        Assert.Equal(new DateOnly(2026, 8, 30), point.RequestedFromDate);
        Assert.Equal(new DateOnly(2026, 9, 5), point.RequestedToDate);
    }

    [Fact]
    public async Task ResumingFromARunStillMarkedRunningIsAWarningRatherThanARoutineLine()
    {
        // The case the whole @AbandonAfterMinutes arrangement exists for -- and the one where a wrong
        // threshold means two runs writing at once. Normal after a reboot; never routine.
        (LoadResume resume, RecordingResumeReader reader, RecordingLogger<LoadResume> logger) = Build();

        reader.Rows = [Resumed.Row(runStatus: "Running")];

        await resume.ReadAsync();

        (LogLevel Level, string Message, Exception? Error) warning = Assert.Single(
            logger.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("still marked 'Running'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("720", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumingFromAnAbandonedRunDoesNotWarnBecauseThatIsTheOrdinaryCase()
    {
        (LoadResume resume, RecordingResumeReader reader, RecordingLogger<LoadResume> logger) = Build();

        reader.Rows = [Resumed.Row(runStatus: "Abandoned")];

        await resume.ReadAsync();

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TheCandidatesAgeIsReportedSoAStaleResumeIsVisibleAfterTheFact()
    {
        // 22:00 the previous day to 22:00 today is 24 hours, inside the 48-hour default -- which is the
        // arithmetic the default was chosen for: a run killed overnight is resumed the next evening.
        (LoadResume resume, RecordingResumeReader reader, RecordingLogger<LoadResume> logger) = Build();

        reader.Rows = [Resumed.Row()];

        await resume.ReadAsync();

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("24h ago", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnusableConfigurationThrowsRatherThanReportingNothingToResume()
    {
        // An empty resume point is a legitimate answer meaning "fetch everything". A configuration failure
        // returning one would make a resumed run re-fetch the whole population and report success.
        (LoadResume resume, RecordingResumeReader reader, _) = Build(activityLocation: string.Empty);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resume.ReadAsync());

        Assert.Contains("RCRAInfoLoad", error.Message, StringComparison.Ordinal);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task ScriptFiveTwentyFivesRefusalsAreNotSwallowed()
    {
        // A named run that does not exist, or belongs to another activity location, or is still live: three
        // caller defects with no safe reading. Turning them into "nothing to resume" would make resuming a
        // Delaware run into a Maryland one look like a first run.
        (LoadResume resume, RecordingResumeReader reader, _) = Build();

        reader.Throw = new InvalidOperationException("50000: does not belong to activity location 'MD'");

        await Assert.ThrowsAsync<InvalidOperationException>(() => resume.ReadAsync(loadRunId: 7));
    }

    [Fact]
    public async Task PlanningLogsTheSkipRateAndTheVanishedCount()
    {
        // The two numbers an operator reads to decide whether the resume worked. The vanished warning is
        // the reason the resume set carries unfinished versions at all (G25), and this is the only place it
        // is said.
        (LoadResume resume, RecordingResumeReader reader, RecordingLogger<LoadResume> logger) = Build();

        reader.Rows =
        [
            Resumed.Row("MDD000000001", status: "Succeeded"),
            Resumed.Row("MDD000000002", status: "Failed"),
        ];

        LoadResumePoint point = await resume.ReadAsync();

        LoadResumePlan plan = resume.Plan(
            point,
            [new HandlerVersion("MDD000000001", "N", 1), new HandlerVersion("MDD000000003", "N", 1)]);

        Assert.Single(plan.ToSkip);
        Assert.Single(plan.Vanished);

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("50.0%", StringComparison.Ordinal));

        (LogLevel Level, string Message, Exception? Error) vanished = Assert.Single(
            logger.Entries,
            entry => entry.Message.Contains("not named by this run", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Warning, vanished.Level);

        // No handler identifier in the finding, on purpose: the versions are in logs.HandlerLoadStatus and
        // the run number in the message is what finds them (AR8).
        Assert.DoesNotContain("MDD0000", vanished.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningWithoutAResumePointReportsNoVanishedVersions()
    {
        // Vanished is meaningless with nothing to compare against, and a warning naming run "0" would be
        // worse than none.
        (LoadResume resume, _, RecordingLogger<LoadResume> logger) = Build();

        LoadResumePlan plan = resume.Plan(LoadResumePoint.None, [new HandlerVersion("MDD000000001", "N", 1)]);

        Assert.Empty(plan.Vanished);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    private static (LoadResume Resume, RecordingResumeReader Reader, RecordingLogger<LoadResume> Logger) Build(
        string activityLocation = "MD",
        int abandonAfterMinutes = 720,
        int? resumeMaxAgeHours = 48)
    {
        RecordingResumeReader reader = new();
        RecordingLogger<LoadResume> logger = new();

        LoadResume resume = new(
            reader,
            Options.Create(
                new LoadRunOptions
                {
                    ActivityLocation = activityLocation,
                    AbandonAfterMinutes = abandonAfterMinutes,
                    ResumeMaxAgeHours = resumeMaxAgeHours,
                }),
            new TestClock(Now),
            logger);

        return (resume, reader, logger);
    }
}
