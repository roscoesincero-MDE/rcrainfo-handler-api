using System.Globalization;

using Microsoft.Extensions.Options;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The stage that decides what the run will fetch. Every assertion here is about one asymmetry, and it is the
/// mirror image of the lookup refresh's: <b>a failed window is the dangerous case.</b>
/// </summary>
/// <remarks>
/// <para>
/// A failed lookup changed nothing. A failed <i>window</i> loses the versions it would have named, attaches
/// no error to any handler, and — if the watermark advances anyway — is never asked about again.
/// <c>/hd/sources/summaries</c> has no envelope and no paging, so nothing downstream can notice the gap
/// afterwards. So the tests below are mostly about <see cref="SummaryWalkReport.MayAdvanceWatermark"/>: it is
/// the one boolean standing between a transient HTTP failure and a permanent hole in the mirror.
/// </para>
/// <para>
/// The sharpest single case is <see cref="ADocumentedNotFoundIsAnEmptyWindowAndNeverADeletion"/>. The same
/// <c>ApiFetchOutcome.NotFound</c> that means "EPA withdrew this version" on the source endpoint means "no
/// versions in these dates" here, and reading it the first way would soft-delete handlers on the strength of
/// a quiet week.
/// </para>
/// </remarks>
public class SummaryWalkTests
{
    private const int RunId = 91;

    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 1, 28);

    [Fact]
    public async Task TheWholeRangeIsCoveredByWindowsAndEveryOneIsAsked()
    {
        (SummaryWalk walk, StubDataClient client, _) = Build();

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(4, report.Windows.Count);
        Assert.Equal(4, report.WalkedCount);
        Assert.Equal(0, report.UnwalkedCount);
        Assert.Equal(4, client.RequestedUris.Count);
        Assert.True(report.MayContinue);
        Assert.True(report.MayAdvanceWatermark);

        // Contiguous and inclusive, in date order, with no gap between the last day of one window and the
        // first of the next -- a gap here is a range nobody ever asks EPA about.
        Assert.Equal(From, report.Windows[0].Window.StartDate);
        Assert.Equal(To, report.Windows[^1].Window.EndDate);
    }

    [Fact]
    public async Task EveryRequestNamesTheConfiguredActivityLocationAndTheWindowsOwnDates()
    {
        (SummaryWalk walk, StubDataClient client, _) = Build();

        await walk.WalkAsync(RunId, From, To);

        Assert.All(
            client.RequestedUris,
            uri => Assert.Contains("activityLocation=MD", uri, StringComparison.Ordinal));

        Assert.Contains(
            client.RequestedUris,
            uri => uri.Contains("startDate=2026-01-01", StringComparison.Ordinal)
                && uri.Contains("endDate=2026-01-07", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADocumentedNotFoundIsAnEmptyWindowAndNeverADeletion()
    {
        // The single most consequential test in the file. ApiFetchOutcome.NotFound is the AR7 soft-delete
        // signal on /hd/sources/{handlerId}/{sourceType}/{sequence}. The summaries endpoint documents 404 as
        // well, and there it means "no handler sources in these dates" -- which is what a quiet week looks
        // like. Reading it the first way would soft-delete handlers on the strength of a date range.
        (SummaryWalk walk, _, _) = Build(
            request => Journalled.LookupFailure(request, ApiFetchOutcome.NotFound, 404));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.All(report.Windows, window => Assert.Equal(SummaryWindowStatus.Walked, window.Status));
        Assert.Empty(report.Versions);

        // Walked, so the watermark advances: the run genuinely learned that these dates are empty.
        Assert.True(report.MayAdvanceWatermark);
    }

    [Fact]
    public async Task AFailedWindowDoesNotStopTheWalkButDoesStopTheWatermark()
    {
        // The two halves of the trade. Fifty windows' versions are real progress and one transient failure
        // should not cost them -- but the failed window's dates must stay in scope for the next run, and the
        // watermark is the only thing that decides that.
        (SummaryWalk walk, StubDataClient client, _) = Build(
            request => Second(request)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.ServiceFailure, 503)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(4, client.RequestedUris.Count);
        Assert.Equal(3, report.WalkedCount);
        Assert.Equal(1, report.UnwalkedCount);

        SummaryWindowReport failed = Assert.Single(report.UnwalkedWindows);
        Assert.Equal(SummaryWindowStatus.FetchFailed, failed.Status);
        Assert.Equal(ApiFetchOutcome.ServiceFailure, failed.FetchOutcome);
        Assert.Equal(503, failed.HttpStatusCode);

        Assert.True(report.MayContinue);
        Assert.False(report.MayAdvanceWatermark);
    }

    [Fact]
    public async Task AFatalOutcomeLeavesTheRemainingWindowsReportedAsNotAttempted()
    {
        // Not omitted. "We never asked" and "we asked and got nothing" are the two answers that must never be
        // confused, and an omitted window looks like neither -- it looks like a shorter range.
        (SummaryWalk walk, StubDataClient client, _) = Build(
            request => Second(request)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.AccessDenied, 403)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        // Two calls: the first window, and the one that failed fatally. An outcome that will fail identically
        // on every remaining window is not worth asking three more times.
        Assert.Equal(2, client.RequestedUris.Count);
        Assert.Equal(4, report.Windows.Count);
        Assert.Equal(ApiFetchOutcome.AccessDenied, report.FatalOutcome);
        Assert.False(report.MayContinue);
        Assert.False(report.MayAdvanceWatermark);

        Assert.Equal(
            [SummaryWindowStatus.Walked, SummaryWindowStatus.FetchFailed, SummaryWindowStatus.NotAttempted,
             SummaryWindowStatus.NotAttempted],
            report.Windows.Select(window => window.Status));
    }

    [Fact]
    public async Task ARetryableOutcomeIsNotFatalSoTheRestOfTheRangeIsStillAsked()
    {
        // Throttled is what a 429 becomes, and it is the outcome most likely to arrive mid-load. The
        // resilience pipeline has already retried it by the time it reaches here, so the walk carries on and
        // the failed window simply blocks the watermark.
        (SummaryWalk walk, StubDataClient client, _) = Build(
            request => Journalled.LookupFailure(request, ApiFetchOutcome.Throttled, 429));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(4, client.RequestedUris.Count);
        Assert.Equal(0, report.WalkedCount);
        Assert.Null(report.FatalOutcome);
        Assert.True(report.MayContinue);
        Assert.False(report.MayAdvanceWatermark);
    }

    [Fact]
    public async Task AnOutOfStateSummaryRefusesTheWholeWindowRatherThanBeingFilteredOut()
    {
        // A request that carried activityLocation=MD and came back with a Delaware handler does not indicate
        // one stray row -- it indicates the filter did not apply, and the Maryland-LOOKING rows in the same
        // body are then no more trustworthy. Filtering would turn "the request I sent is not the request that
        // was answered" into a slightly shorter, entirely plausible window.
        (SummaryWalk walk, _, _) = Build(
            request => Journalled.LookupResult(
                request,
                """
                [{"handlerId":"MDD000000001","activityLocation":"MD","sourceType":"N","sequence":1},
                 {"handlerId":"DED000000001","activityLocation":"DE","sourceType":"N","sequence":1}]
                """));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(0, report.WalkedCount);
        Assert.False(report.MayAdvanceWatermark);
        Assert.True(report.MayContinue);
        Assert.Empty(report.Versions);

        SummaryWindowReport first = report.Windows[0];
        Assert.Equal(SummaryWindowStatus.OutOfScope, first.Status);

        // The jurisdiction is reportable -- a two-letter state is not a regulated entity -- and it is the one
        // value that makes this actionable. Neither handler is named.
        Assert.Contains("'DE'", first.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("DED000000001", first.Problem!, StringComparison.Ordinal);
        Assert.DoesNotContain("MDD000000001", first.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadablePayloadIsAWindowRejectionAndNotARunFailure()
    {
        (SummaryWalk walk, StubDataClient client, _) = Build(
            request => Second(request)
                ? Journalled.LookupResult(request, """{"results":[],"hasMore":true}""")
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(4, client.RequestedUris.Count);
        Assert.Equal(SummaryWindowStatus.PayloadRejected, report.Windows[1].Status);
        Assert.Equal(3, report.WalkedCount);
        Assert.True(report.MayContinue);
        Assert.False(report.MayAdvanceWatermark);
    }

    [Fact]
    public async Task VersionsAreDeDuplicatedAcrossWindowsAndTheDuplicatesAreCounted()
    {
        // Windows do not overlap, so a repeat should be impossible. It is counted rather than silently
        // absorbed because a repeat is the SYMPTOM of G25: EPA filtering on a date field this loader has
        // assumed. A HashSet alone would swallow the evidence.
        (SummaryWalk walk, _, RecordingLogger<SummaryWalk> logger) = Build(
            request => Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(4, report.WalkedCount);
        Assert.Single(report.Versions);

        // Four windows each naming the same version: four summaries read, one distinct version kept, three
        // counted as duplicates.
        Assert.Equal(4, report.SummaryCount);
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("3 duplicate(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedPropertiesAreCollectedAcrossEveryWindowAndDeDuplicated()
    {
        (SummaryWalk walk, _, RecordingLogger<SummaryWalk> logger) = Build(
            request => Journalled.LookupResult(
                request,
                """
                [{"handlerId":"MDD000000001","activityLocation":"MD","sourceType":"N","sequence":1,
                  "nonNotifier": true}]
                """));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.True(report.MayAdvanceWatermark);
        Assert.Equal(["nonNotifier"], report.UnexpectedProperties);
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("Re-pin spec/rcrainfo/swagger.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledRunStopsWalkingAndDoesNotAdvanceTheWatermark()
    {
        using CancellationTokenSource source = new();

        (SummaryWalk walk, StubDataClient client, _) = Build(
            request =>
            {
                source.Cancel();

                return Journalled.LookupResult(request, OneSummary());
            });

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To, source.Token);

        // Cancellation is a report, not an exception: the versions the walk already collected are real, and
        // the caller still has to flush the journal on the way out.
        Assert.Single(client.RequestedUris);
        Assert.True(report.WasCancelled);
        Assert.False(report.MayContinue);
        Assert.False(report.MayAdvanceWatermark);
        Assert.Equal(3, report.Windows.Count(window => window.Status == SummaryWindowStatus.NotAttempted));
    }

    [Fact]
    public async Task NothingLoggedNamesAHandlerOrAUri()
    {
        // AR8 applies to this application's own log, not only to logs.ExecutionLog: the same people read it
        // and paste it into the same tickets. The walk holds several hundred thousand handler identifiers and
        // names none of them, and it logs the two dates rather than the relative URI -- a template that took
        // a URI is the template a future endpoint's credentialed URI gets logged through.
        (SummaryWalk walk, _, RecordingLogger<SummaryWalk> logger) = Build(
            request => Journalled.LookupResult(request, OneSummary()));

        await walk.WalkAsync(RunId, From, To);

        Assert.NotEmpty(logger.Entries);
        Assert.All(
            logger.Entries,
            entry =>
            {
                Assert.DoesNotContain("MDD000000001", entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("api/v1", entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("activityLocation=", entry.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task AnInvertedRangeThrowsBeforeAnythingIsAsked()
    {
        // The dangerous alternative is a successful empty report: EPA answers an inverted range with 200 and
        // an empty array, so every window would look quiet and the watermark would advance over all of it.
        (SummaryWalk walk, StubDataClient client, _) = Build();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => walk.WalkAsync(RunId, To, From));

        Assert.Contains("watermark", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.RequestedUris);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MDX")]
    [InlineData("M")]
    public async Task UnusableOptionsThrowBeforeAnythingIsAsked(string activityLocation)
    {
        // Thrown rather than reported, for the reason LookupRefresh gives: unusable configuration is not an
        // answer from EPA, and an empty report here is indistinguishable from a range in which nothing
        // changed -- which is the report that advances a watermark.
        (SummaryWalk walk, StubDataClient client, _) = Build(activityLocation: activityLocation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => walk.WalkAsync(RunId, From, To));

        Assert.Empty(client.RequestedUris);
    }

    [Fact]
    public async Task TheWindowWidthComesFromConfigurationAndDecidesTheCallCount()
    {
        // The only lever on response size, because the endpoint has no paging. Worth asserting that it is
        // actually wired: a WindowDays nobody reads would leave every run asking for one window of whatever
        // range it was given, which is the shape [R28] rules out.
        (SummaryWalk walk, StubDataClient client, _) = Build(windowDays: 1);

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, new DateOnly(2026, 1, 3));

        Assert.Equal(3, client.RequestedUris.Count);
        Assert.All(report.Windows, window => Assert.Equal(1, window.Window.DayCount));
    }

    /// <summary>
    /// A clean walk banks the whole range, so the partial rule changes nothing about the ordinary case.
    /// </summary>
    /// <remarks>
    /// <b>Worth asserting explicitly rather than inferring it from <c>MayAdvanceWatermark</c>.</b> The
    /// orchestrator moves the watermark to <see cref="SummaryWalkReport.ContiguouslyWalkedThrough"/> now, not to
    /// the requested end date — so if this property ever stopped equalling the end date on a complete walk,
    /// every scheduled run would quietly leave the bookmark short and re-walk the same days forever.
    /// </remarks>
    [Fact]
    public async Task ACompleteWalkBanksTheWholeRequestedRange()
    {
        (SummaryWalk walk, _, _) = Build();

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.True(report.MayAdvanceWatermark);
        Assert.Equal(To, report.ContiguouslyWalkedThrough);
    }

    /// <summary>
    /// A missed window banks the days before it and nothing after, even though later windows did land.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes a decades-long catch-up converge.</b> Forty-six years at the default width is
    /// roughly 2,400 windows; requiring all 2,400 before any progress is durable means one transient
    /// <c>500</c> in hour six costs the whole night, every night.
    /// </para>
    /// <para>
    /// <b>And this is why it is safe.</b> The windows after the failure are discarded from the bookmark even
    /// though they were walked, so the next run asks for the failed window's dates <i>and</i> everything
    /// after them. Re-walking costs one request each and their versions merge as <c>Unchanged</c>; banking
    /// past the gap would leave a hole that <c>/hd/sources/summaries</c> has no envelope to reveal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMissedWindowBanksTheDaysBeforeItAndDiscardsTheOnesAfter()
    {
        (SummaryWalk walk, _, _) = Build(
            request => Second(request)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.ServiceFailure, 503)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.False(report.MayAdvanceWatermark);
        Assert.Equal(new DateOnly(2026, 1, 7), report.ContiguouslyWalkedThrough);

        // The third and fourth windows landed and are deliberately not banked -- the prefix stops at the gap.
        Assert.Equal(3, report.WalkedCount);
    }

    /// <summary>A first window that did not land banks nothing at all.</summary>
    /// <remarks>
    /// The one case that must be <see langword="null"/> rather than a date: there is no walked prefix, so any
    /// value here would move the bookmark over days nobody asked EPA about.
    /// </remarks>
    [Fact]
    public async Task AMissedFirstWindowBanksNothing()
    {
        (SummaryWalk walk, _, _) = Build(
            request => request.RelativeUri.Contains("startDate=2026-01-01", StringComparison.Ordinal)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.ServiceFailure, 503)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(3, report.WalkedCount);
        Assert.Null(report.ContiguouslyWalkedThrough);
    }

    /// <summary>A missed last window banks everything before it, which is the common shape of a long run.</summary>
    /// <remarks>
    /// A catch-up that dies late is the ordinary failure: the credential expires, the service degrades under
    /// the sustained load, or the maintenance window arrives. Banking the prefix turns a night that achieved
    /// nothing into a night that achieved all but one window.
    /// </remarks>
    [Fact]
    public async Task AMissedLastWindowBanksEverythingBeforeIt()
    {
        (SummaryWalk walk, _, _) = Build(
            request => request.RelativeUri.Contains("startDate=2026-01-22", StringComparison.Ordinal)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.ServiceFailure, 503)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.False(report.MayAdvanceWatermark);
        Assert.Equal(new DateOnly(2026, 1, 21), report.ContiguouslyWalkedThrough);
    }

    /// <summary>
    /// A genuinely quiet window does not stop the prefix, and that distinction is the property's whole basis.
    /// </summary>
    /// <remarks>
    /// <b>"Nothing changed" and "we do not know" have to be different answers here.</b> EPA's documented
    /// <c>404</c> and a <c>200</c> with an empty array both mean the dates are empty, and both are
    /// <c>Walked</c> — so a quiet fortnight in a decades-long catch-up does not halt the bookmark. If it did,
    /// the initial load would stop advancing at the first quiet week of 1980 and never reach the present.
    /// </remarks>
    [Fact]
    public async Task AQuietWindowDoesNotStopThePrefix()
    {
        (SummaryWalk walk, _, _) = Build(
            request => Second(request)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.NotFound, 404)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.Equal(4, report.WalkedCount);
        Assert.Equal(To, report.ContiguouslyWalkedThrough);
    }

    /// <summary>
    /// A fatal outcome leaves a prefix that the orchestrator never reads, and the property is not permission.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as a pair, because the property on its own would be misread.</b> A <c>403</c> stops the walk
    /// with three windows never attempted, and the first window's date is still a perfectly good prefix — but
    /// <c>LoadRun</c> requires <see cref="SummaryWalkReport.MayContinue"/> before it looks, and an
    /// <c>AccessDenied</c> run has not even reached the fetch loop. Banking on a credential failure would move
    /// the bookmark on a night the loader accomplished nothing.
    /// </remarks>
    [Fact]
    public async Task AFatalOutcomeLeavesAPrefixTheOrchestratorMayNotUse()
    {
        (SummaryWalk walk, _, _) = Build(
            request => Second(request)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.AccessDenied, 403)
                : Journalled.LookupResult(request, OneSummary()));

        SummaryWalkReport report = await walk.WalkAsync(RunId, From, To);

        Assert.False(report.MayContinue);
        Assert.Equal(new DateOnly(2026, 1, 7), report.ContiguouslyWalkedThrough);
    }

    /// <summary>Whether this is the second window of the four-window range the tests use.</summary>
    private static bool Second(RcraInfoDataRequest request) =>
        request.RelativeUri.Contains("startDate=2026-01-08", StringComparison.Ordinal);

    private static string OneSummary(string handlerId = "MDD000000001", int sequence = 1) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """[{{"handlerId":"{0}","activityLocation":"MD","sourceType":"N","sequence":{1},"currentRecord":true}}]""",
            handlerId,
            sequence);

    private static (SummaryWalk Walk, StubDataClient Client, RecordingLogger<SummaryWalk> Logger) Build(
        Func<RcraInfoDataRequest, ApiFetchResult>? answer = null,
        string activityLocation = "MD",
        int windowDays = 7)
    {
        StubDataClient client = new(
            answer ?? (request => Journalled.LookupResult(request, OneSummary())));
        RecordingLogger<SummaryWalk> logger = new();

        SummaryWalk walk = new(
            client,
            Options.Create(
                new LoadRunOptions { ActivityLocation = activityLocation, WindowDays = windowDays }),
            logger);

        return (walk, client, logger);
    }
}
