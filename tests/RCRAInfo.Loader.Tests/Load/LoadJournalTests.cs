using Microsoft.Extensions.Logging;

using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;
using RCRAInfo.Loader.Tests.Api;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The buffered journal: when it flushes, in what order, and the two things it deliberately refuses to
/// write.
/// </summary>
/// <remarks>
/// <para>
/// These tests are not about the two procedures — the DA5 integration suite covers those against a real
/// database. They are about the four decisions that live only in the loader and that no procedure can
/// defend itself against: the flush <b>order</b>, the per-payload <b>duplicate key</b> rule that script
/// 520 throws over, the <b>one-of-three</b> detail rule its <c>Fail</c> mode throws over, and that a
/// buffer is <b>never</b> silently dropped on exit.
/// </para>
/// <para>
/// Each of the four is a defect that would surface as missing or wrong rows in the one pair of tables an
/// operator uses to find out what a 2am load actually did — which is to say, in the place where nobody
/// would think to distrust them.
/// </para>
/// </remarks>
public class LoadJournalTests
{
    private const int RunId = 4242;

    private static readonly DateTimeOffset Start = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheFlushOrderIsEnumerateAttemptFailSkipThenTheAttemptRows()
    {
        // The single most consequential behaviour in this class. Script 524 resolves each attempt element
        // against a status row of the same run and counts the ones it cannot as @RowsOrphaned; script 520's
        // Attempt, Fail and Skip modes all UPDATE the row Enumerate creates. Any other order either reports
        // a defect for a working load or leaves a failed version reading InProgress.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.EnumerateAsync([Journalled.Pending(sequence: 1), Journalled.Pending(sequence: 2)]);
        await journal.RecordAttemptAsync(Journalled.Result(ApiFetchOutcome.ServiceFailure, 500), Journalled.Version(sequence: 1), 1);
        await journal.ConcludeAsync(Journalled.Result(ApiFetchOutcome.ServiceFailure, 500), Journalled.Version(sequence: 1));
        await journal.ConcludeAsync(Journalled.Result(ApiFetchOutcome.Cancelled), Journalled.Version(sequence: 2));

        Assert.Empty(writer.Calls);

        LoadJournalFlush flush = await journal.FlushAsync();

        Assert.Equal(
            ["520:Enumerate(2)", "520:Attempt(1)", "520:Fail(1)", "520:Skip(1)", "524(1)"],
            writer.Calls.Select(call => call.ToString()));

        Assert.Equal(2, flush.Enumerated);
        Assert.Equal(1, flush.Attempted);
        Assert.Equal(1, flush.Failed);
        Assert.Equal(1, flush.Skipped);
        Assert.Equal(1, flush.AttemptRows);
        Assert.Equal(5, flush.Calls);
        Assert.False(flush.HasDefects);
        Assert.Equal(0, journal.PendingRows);
    }

    [Fact]
    public async Task AModeWithNothingBufferedMakesNoCallAtAll()
    {
        // Not "sends an empty array and lets the procedure no-op". Four modes plus the attempt log means
        // four wasted round-trips per flush, and a flush happens every hundred rows across several hundred
        // thousand handlers.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.EnumerateAsync([Journalled.Pending()]);

        LoadJournalFlush flush = await journal.FlushAsync();

        Assert.Equal(["520:Enumerate(1)"], writer.Calls.Select(call => call.ToString()));
        Assert.Equal(1, flush.Calls);
    }

    [Fact]
    public async Task AnEmptyBufferFlushesToNothingAndReportsNothing()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock);

        LoadJournalFlush flush = await journal.FlushAsync();

        Assert.Empty(writer.Calls);
        Assert.Equal(LoadJournalFlush.Empty, flush);
    }

    [Fact]
    public async Task TheRowCountTriggersAFlushWithoutBeingAsked()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 4);

        await journal.EnumerateAsync([Journalled.Pending(sequence: 1), Journalled.Pending(sequence: 2)]);

        Assert.Empty(writer.Calls);
        Assert.Equal(2, journal.PendingRows);

        // Each attempt buffers two rows -- a status row and an attempt row -- so this crosses 4.
        await journal.RecordAttemptAsync(Journalled.Result(ApiFetchOutcome.Succeeded, 200), Journalled.Version(sequence: 1), 1);

        Assert.Equal(["520:Enumerate(2)", "520:Attempt(1)", "524(1)"], writer.Calls.Select(c => c.ToString()));
        Assert.Equal(0, journal.PendingRows);
    }

    [Fact]
    public async Task TheIntervalTriggersAFlushOnTheNextBufferedRow()
    {
        // And on the next buffered row rather than on a timer: see the remarks on LoadJournal. This is the
        // behaviour that measures the trade -- the interval bounds staleness while rows are arriving, which
        // is when an operator watching the monitoring grid cares.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(
            writer, out TestClock clock, rowCount: 1000, interval: TimeSpan.FromSeconds(30));

        await journal.EnumerateAsync([Journalled.Pending(sequence: 1)]);

        Assert.Empty(writer.Calls);

        clock.Advance(TimeSpan.FromSeconds(29));
        await journal.EnumerateAsync([Journalled.Pending(sequence: 2)]);

        Assert.Empty(writer.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await journal.EnumerateAsync([Journalled.Pending(sequence: 3)]);

        Assert.Equal(["520:Enumerate(3)"], writer.Calls.Select(c => c.ToString()));
    }

    [Fact]
    public async Task TheIntervalIsMeasuredFromTheEndOfThePreviousFlush()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(
            writer, out TestClock clock, rowCount: 1000, interval: TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(31));
        await journal.EnumerateAsync([Journalled.Pending(sequence: 1)]);

        Assert.Single(writer.Calls);

        // The clock has not moved since, so this one waits rather than flushing again.
        await journal.EnumerateAsync([Journalled.Pending(sequence: 2)]);

        Assert.Single(writer.Calls);
        Assert.Equal(1, journal.PendingRows);
    }

    [Fact]
    public async Task ABufferWiderThanTheWriterAcceptsIsSplitAndNoCallExceedsTheLimit()
    {
        // Not redundant with FlushRowCount. One EnumerateAsync call carries every version a summaries page
        // named -- a page size, not a flush size -- and above RCRAInfoDataOptions.MaxPayloadElements
        // PayloadJson.Serialize throws, which would lose the whole flush rather than truncate it.
        RecordingJournalWriter writer = new() { MaxElementsPerCall = 3 };
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.EnumerateAsync(Enumerable.Range(1, 7).Select(n => Journalled.Pending(sequence: n)));

        LoadJournalFlush flush = await journal.FlushAsync();

        Assert.Equal(
            ["520:Enumerate(3)", "520:Enumerate(3)", "520:Enumerate(1)"],
            writer.Calls.Select(c => c.ToString()));

        Assert.All(writer.Calls, call => Assert.True(call.Count <= 3));
        Assert.Equal(7, flush.Enumerated);
        Assert.Equal(3, flush.Calls);
    }

    [Fact]
    public async Task TwoAttemptsAtOneVersionBecomeOneStatusRowAndTwoAttemptRows()
    {
        // Script 520 THROWS when one payload names the same (HandlerId, SourceType, Sequence) twice, so a
        // list-shaped buffer would take down the whole flush the first time a handler was retried inside one
        // flush window -- which is the ordinary case, not an edge one. Replacing in place is also the right
        // semantics: the status table holds current state, and attempt 2 supersedes attempt 1.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        HandlerVersion version = Journalled.Version();

        await journal.RecordAttemptAsync(Journalled.Result(ApiFetchOutcome.Throttled, 429), version, 1);
        await journal.RecordAttemptAsync(Journalled.Result(ApiFetchOutcome.Succeeded, 200), version, 2);

        await journal.FlushAsync();

        HandlerLoadStatusElement status = Assert.Single(writer.Status["Attempt"]);

        Assert.Equal(2, status.AttemptNumber);
        Assert.Equal(2, writer.Attempts.Count);
        Assert.Equal([1, 2], writer.Attempts.Select(a => a.AttemptNumber));
    }

    [Fact]
    public async Task NoSingleStatusCallEverNamesOneVersionTwice()
    {
        // The same rule under concurrency, which is how the orchestrator will drive this: bounded parallel
        // fetches, all buffering into one journal. Ten attempts at each of twenty versions, at a threshold
        // low enough that flushes interleave with the buffering.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 10);

        await Task.WhenAll(Enumerable.Range(1, 20).Select(async sequence =>
        {
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                await journal.RecordAttemptAsync(
                    Journalled.Result(ApiFetchOutcome.Throttled, 429),
                    Journalled.Version(sequence: sequence),
                    attempt);
            }
        }));

        await journal.FlushAsync();

        Assert.All(writer.StatusBatches, batch =>
            Assert.Equal(
                batch.Elements.Length,
                batch.Elements.Select(e => (e.HandlerId, e.SourceType, e.Sequence)).Distinct().Count()));

        // And nothing was dropped on the way: every call is still in the attempt log.
        Assert.Equal(200, writer.Attempts.Count);
    }

    [Theory]
    [InlineData(ApiFetchOutcome.Succeeded)]
    [InlineData(ApiFetchOutcome.NotFound)]
    public async Task TheJournalRefusesToClaimSuccessAndSaysSoToTheCaller(ApiFetchOutcome outcome)
    {
        // The finding that produced script 522's step 10. Script 520 has four modes and none is a success:
        // Succeeded comes only from dbo.uspMergeHandlerSourceBatch (a version committed) or
        // dbo.uspSoftDeleteHandlerSourceSet (EPA answered 404, so the version was withdrawn), each inside
        // the transaction that made the claim true. Until 2026-09-06 nothing could write the second of
        // those, so a 404 left its status row reading InProgress forever and the next run re-fetched a
        // record EPA had already withdrawn.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        bool buffered = await journal.ConcludeAsync(
            Journalled.Result(outcome, outcome == ApiFetchOutcome.NotFound ? 404 : 200),
            Journalled.Version());

        Assert.False(buffered);
        Assert.Equal(0, journal.PendingRows);

        await journal.FlushAsync();

        Assert.Empty(writer.Calls);
    }

    [Theory]
    [InlineData(ApiFetchOutcome.BadRequest, "Fail")]
    [InlineData(ApiFetchOutcome.Unauthorized, "Fail")]
    [InlineData(ApiFetchOutcome.AccessDenied, "Fail")]
    [InlineData(ApiFetchOutcome.Throttled, "Fail")]
    [InlineData(ApiFetchOutcome.ServiceFailure, "Fail")]
    [InlineData(ApiFetchOutcome.Unreachable, "Fail")]
    [InlineData(ApiFetchOutcome.TimedOut, "Fail")]
    [InlineData(ApiFetchOutcome.Unexpected, "Fail")]
    [InlineData(ApiFetchOutcome.Cancelled, "Skip")]
    public async Task EveryOtherOutcomeRoutesToTheModeItsStatusNames(ApiFetchOutcome outcome, string mode)
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        Assert.True(await journal.ConcludeAsync(Journalled.Result(outcome), Journalled.Version()));

        await journal.FlushAsync();

        Assert.Equal([mode], writer.Modes);
    }

    [Theory]
    [InlineData(ApiFetchOutcome.Unreachable)]
    [InlineData(ApiFetchOutcome.TimedOut)]
    [InlineData(ApiFetchOutcome.Unexpected)]
    public async Task AFailWithNoDetailAtAllStillSatisfiesScript520(ApiFetchOutcome outcome)
    {
        // A transport failure has no HTTP status and no RCRAInfo error document, so it carries none of the
        // three things script 520's Fail mode requires -- and that procedure THROWS rather than writing a
        // Failed row that says nothing. Unhandled, one connectivity blip would take down the flush and lose
        // the buffered rows for every other handler in it.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.ConcludeAsync(Journalled.Result(outcome), Journalled.Version());
        await journal.FlushAsync();

        HandlerLoadStatusElement element = Assert.Single(writer.Status["Fail"]);

        Assert.Null(element.HttpStatusCode);
        Assert.Equal($"LOADER-{outcome}", element.ApiErrorCode);

        // Prefixed so nothing reads it as one of EPA's codes, and inside the column's 100 characters.
        Assert.True(element.ApiErrorCode!.Length <= 100);
    }

    [Fact]
    public async Task EpasOwnDetailIsNeverReplacedByTheSyntheticCode()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.ConcludeAsync(
            Journalled.Result(ApiFetchOutcome.BadRequest, 400, "E_BAD_REQUEST", "startDate is required"),
            Journalled.Version());

        await journal.FlushAsync();

        HandlerLoadStatusElement element = Assert.Single(writer.Status["Fail"]);

        Assert.Equal(400, element.HttpStatusCode);
        Assert.Equal("E_BAD_REQUEST", element.ApiErrorCode);
        Assert.Equal("startDate is required", element.ApiErrorMessage);
    }

    [Fact]
    public async Task AStatusCodeAloneIsEnoughAndTheSyntheticCodeStaysOut()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.ConcludeAsync(
            Journalled.Result(ApiFetchOutcome.ServiceFailure, 500), Journalled.Version());

        await journal.FlushAsync();

        HandlerLoadStatusElement element = Assert.Single(writer.Status["Fail"]);

        Assert.Equal(500, element.HttpStatusCode);
        Assert.Null(element.ApiErrorCode);
    }

    [Fact]
    public async Task EveryLoggedRequestPathBeginsWithASlash()
    {
        // AR8, and script 524's own rule: it accepts RequestPath only if it starts with a slash and replaces
        // the whole value when it does not, counting it into @ValuesWithheld. HttpClient forbids the leading
        // slash on the value it sends. This asserts the journal carries the third form -- LogPath -- through
        // to the writer, for all three endpoints.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        RcraInfoDataRequest[] requests =
        [
            RcraInfoDataRequest.Source("MDD000000001", "N", 1),
            RcraInfoDataRequest.OtherIds("MDD000000001"),
            RcraInfoDataRequest.Summaries("MD", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)),
        ];

        for (int i = 0; i < requests.Length; i++)
        {
            await journal.RecordAttemptAsync(
                Journalled.Result(ApiFetchOutcome.Succeeded, 200, request: requests[i]),
                Journalled.Version(sequence: i + 1),
                1);
        }

        await journal.FlushAsync();

        Assert.Equal(3, writer.Attempts.Count);
        Assert.All(writer.Attempts, attempt => Assert.StartsWith("/", attempt.RequestPath, StringComparison.Ordinal));

        // And no query string reached the log, from the two endpoints that carry their argument in one.
        Assert.All(writer.Attempts, attempt => Assert.DoesNotContain("?", attempt.RequestPath, StringComparison.Ordinal));
        Assert.All(writer.Attempts, attempt => Assert.DoesNotContain("handlerId=", attempt.RequestPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRunningTotalAccumulatesAcrossFlushes()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.EnumerateAsync([Journalled.Pending(sequence: 1)]);
        await journal.FlushAsync();

        await journal.EnumerateAsync([Journalled.Pending(sequence: 2)]);
        await journal.ConcludeAsync(Journalled.Result(ApiFetchOutcome.Cancelled), Journalled.Version(sequence: 3));
        await journal.FlushAsync();

        Assert.Equal(2, journal.Total.Enumerated);
        Assert.Equal(1, journal.Total.Skipped);
        Assert.Equal(3, journal.Total.StatusRows);
        Assert.Equal(3, journal.Total.TotalRows);
    }

    [Fact]
    public async Task TheDefectCountsAreCarriedUpAndLogged()
    {
        // Script 524 writes what it can and reports what it could not, because refusing the flush would lose
        // the diagnostic rows for every other handler in the buffer. Which makes surfacing the report the
        // only signal there is -- a caller that discards it turns a defect into silence.
        RecordingJournalWriter writer = new() { OrphanPerCall = 2, WithheldPerCall = 1 };
        RecordingLogger<LoadJournal> logger = new();

        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000, logger: logger);

        await journal.RecordAttemptAsync(Journalled.Result(ApiFetchOutcome.Succeeded, 200), Journalled.Version(), 1);

        LoadJournalFlush flush = await journal.FlushAsync();

        Assert.Equal(2, flush.RowsOrphaned);
        Assert.Equal(1, flush.ValuesWithheld);
        Assert.True(flush.HasDefects);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("orphaned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposalWritesWhatIsStillBuffered()
    {
        // "Flush unconditionally before the process exits, on both the success and failure paths -- a
        // buffered status set lost on shutdown is invisible data loss in the one table that exists to make
        // loss visible." (Plan D2.)
        RecordingJournalWriter writer = new();
        RecordingLogger<LoadJournal> logger = new();
        LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000, logger: logger);

        await journal.EnumerateAsync([Journalled.Pending()]);

        Assert.Empty(writer.Calls);

        await journal.DisposeAsync();

        Assert.Equal(["520:Enumerate(1)"], writer.Calls.Select(c => c.ToString()));

        // And it says so, because reaching this path means the orchestrator did not flush on its own.
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DisposalFlushesEvenAfterTheRunWasCancelled()
    {
        // CancellationToken.None, deliberately: the run that most needs its journal written is the one being
        // shut down, and a cancelled run's buffer is the record of which versions still need fetching.
        RecordingJournalWriter writer = new();
        LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        using CancellationTokenSource cancellation = new();

        await journal.EnumerateAsync([Journalled.Pending()], cancellation.Token);
        await cancellation.CancelAsync();

        await journal.DisposeAsync();

        Assert.Equal(["520:Enumerate(1)"], writer.Calls.Select(c => c.ToString()));
    }

    [Fact]
    public async Task AFailingFinalFlushIsLoggedAndNotThrown()
    {
        // Because a throw out of DisposeAsync while the stack is unwinding from another exception REPLACES
        // it -- so the failure that ended the run would be overwritten by the failure to write about it,
        // which is the less useful of the two by a wide margin.
        RecordingJournalWriter writer = new() { ThrowOnce = new InvalidOperationException("520 refused") };
        RecordingLogger<LoadJournal> logger = new();
        LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000, logger: logger);

        await journal.EnumerateAsync([Journalled.Pending()]);

        await journal.DisposeAsync();

        (LogLevel Level, string Message, Exception? Error) entry =
            Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);

        Assert.Contains("lost", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Error);
    }

    [Fact]
    public async Task DisposingTwiceFlushesOnce()
    {
        RecordingJournalWriter writer = new();
        LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.EnumerateAsync([Journalled.Pending()]);

        await journal.DisposeAsync();
        await journal.DisposeAsync();

        Assert.Single(writer.Calls);
    }

    [Fact]
    public async Task DisposingAnEmptyJournalWritesNothingAndSaysNothing()
    {
        RecordingJournalWriter writer = new();
        RecordingLogger<LoadJournal> logger = new();
        LoadJournal journal = Build(writer, out TestClock clock, logger: logger);

        await journal.DisposeAsync();

        Assert.Empty(writer.Calls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task AFlushThatFailsLosesTheRowsItHadDrainedAndTheCallerSeesWhy()
    {
        // The buffers are drained under the lock and then written, so a failed write loses that batch. Stated
        // rather than fixed, because the alternatives are worse: putting the rows back means a deterministic
        // failure -- a malformed element, a revoked GRANT -- retries forever on a buffer that only grows, and
        // then fails the shutdown flush too, so nothing is ever written. What is lost is diagnostic rows for
        // one batch, in a run the orchestrator ends; what the next run re-enumerates as Pending is the same
        // set of versions either way. The exception reaching the caller is the load-bearing part.
        RecordingJournalWriter writer = new() { ThrowOnce = new InvalidOperationException("520 refused") };
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.EnumerateAsync([Journalled.Pending()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.FlushAsync());

        Assert.Equal(0, journal.PendingRows);
    }

    [Fact]
    public async Task AnAttemptNumberBelowOneIsRefusedRatherThanSentOn()
    {
        // Script 520's Attempt mode refuses it too, with a message that explains why it SETs rather than
        // increments. Refusing here means the whole flush is not lost to one bad element.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await journal.RecordAttemptAsync(
                Journalled.Result(ApiFetchOutcome.Succeeded, 200), Journalled.Version(), 0));
    }

    [Fact]
    public async Task SkippingAVersionWritesTheKeyAndNoErrorDetailAtAll()
    {
        // What a resumed run does with the previous run's successes. Script 520's Skip mode takes the
        // natural key and nothing else -- "there is no reason column on this table; a skip's reason belongs
        // to the run" -- so anything more here would be an invention.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.SkipAsync([Journalled.Version("MDD000000001"), Journalled.Version("MDD000000002")]);
        await journal.FlushAsync();

        Assert.Equal(["Skip"], writer.Modes);
        Assert.Equal(2, writer.Status["Skip"].Count);

        HandlerLoadStatusElement element = writer.Status["Skip"][0];

        Assert.Equal("MDD000000001", element.HandlerId);
        Assert.Equal("N", element.SourceType);
        Assert.Equal(1, element.Sequence);

        // The line that matters. Routing this through ConcludeAsync would be the natural shortcut, and the
        // only outcome that reaches Skip mode is Cancelled -- which ToFailureElement stamps as
        // ApiErrorCode = "LOADER-Cancelled". That is a lie about a version nothing went wrong with, written
        // into the column an operator reads to find out what went wrong.
        Assert.Null(element.ApiErrorCode);
        Assert.Null(element.ApiErrorMessage);
        Assert.Null(element.HttpStatusCode);
        Assert.Null(element.ApiErrorId);
        Assert.Null(element.ApiErrorDate);
    }

    [Fact]
    public async Task SkippingTheSameVersionTwiceBuffersOneRow()
    {
        // Script 520 throws when one payload names the same key twice. A resume plan should not produce a
        // repeat -- it de-duplicates the walk -- but the buffer is what makes that a guarantee rather than
        // a hope, and losing a whole flush to a duplicate would lose the journal for every other handler.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.SkipAsync([Journalled.Version(), Journalled.Version()]);
        await journal.FlushAsync();

        Assert.Single(writer.Status["Skip"]);
    }

    [Fact]
    public async Task SkipRowsAreWrittenAfterTheEnumerateRowsThatCreateThem()
    {
        // Skip mode UPDATEs and never INSERTs, so a Skip flushed before the Enumerate row that names its
        // version matches nothing and is silently counted as notFound. The resumed run's grid would then
        // show the population as enumerated-but-unaccounted-for, which is the picture this method exists to
        // prevent.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.SkipAsync([Journalled.Version()]);
        await journal.EnumerateAsync([Journalled.Pending()]);

        await journal.FlushAsync();

        Assert.Equal(["Enumerate", "Skip"], writer.Modes);
    }

    [Fact]
    public async Task SkippedRowsAreCountedInTheRunTotal()
    {
        // AR5's arithmetic: enumerated = fetched + skipped. Without the count, a resumed run's summary says
        // it enumerated several hundred thousand versions and handled a fraction of them.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 1000);

        await journal.SkipAsync([Journalled.Version("MDD000000001"), Journalled.Version("MDD000000002")]);

        LoadJournalFlush flush = await journal.FlushAsync();

        Assert.Equal(2, flush.Skipped);
        Assert.Equal(2, journal.Total.Skipped);
    }

    [Fact]
    public async Task SkippingCountsTowardsTheFlushThresholdLikeEveryOtherBufferedRow()
    {
        // The resume plan hands over the whole skipped set at once, which for an initial load is the bulk of
        // the population. Exempting it from the threshold would buffer all of it in memory.
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock, rowCount: 2);

        await journal.SkipAsync(
            [Journalled.Version("MDD000000001"), Journalled.Version("MDD000000002")]);

        Assert.Equal(0, journal.PendingRows);
        Assert.Equal(["Skip"], writer.Modes);
    }

    [Fact]
    public async Task ANullVersionSequenceIsARefusal()
    {
        RecordingJournalWriter writer = new();
        await using LoadJournal journal = Build(writer, out TestClock clock);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await journal.SkipAsync(null!));
    }

    private static LoadJournal Build(
        RecordingJournalWriter writer,
        out TestClock clock,
        int rowCount = 100,
        TimeSpan? interval = null,
        ILogger<LoadJournal>? logger = null)
    {
        clock = new TestClock(Start);

        LoadJournalOptions options = new()
        {
            FlushRowCount = rowCount,
            FlushInterval = interval ?? TimeSpan.FromHours(1),
        };

        return new LoadJournal(RunId, writer, options, clock, logger ?? new RecordingLogger<LoadJournal>());
    }
}
