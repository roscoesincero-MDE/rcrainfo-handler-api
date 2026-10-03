using System.Globalization;
using System.Text.Json;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// The orchestrator: one scheduled retrieval, in the order the other stages have to run in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Almost nothing here is new work, and that is the design.</b> Every stage was built and tested on its
/// own — the lookup refresh ([R32]), the summaries walk ([R34]), the resume read ([R34]), the buffered
/// journal ([R30]) — and this class contributes the two things none of them can hold: the <i>order</i> they
/// run in, and the decision about what each one's failure means to the run as a whole. Those two are where
/// the run-level defects live, so they are the two things worth testing here and the two things this class
/// keeps.
/// </para>
/// <para>
/// <b>The order is not a preference; three of the six steps are load-bearing.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>The resume read comes before the run is opened</b>, because <c>logs.uspStartLoadRun</c> takes
/// <c>@ResumedFromLoadRunId</c> as an <i>input</i> — the answer has to exist before the row that records
/// it. That is also why both procedures are given the same <see cref="LoadRunOptions.AbandonAfterMinutes"/>
/// (see that property).
/// </item>
/// <item>
/// <b>The lookups are refreshed before any handler data is fetched (G15)</b>, so that a handler payload
/// naming a code EPA added this cycle meets a mirror that already has it.
/// </item>
/// <item>
/// <b>Every version the walk named is enumerated before anything is attempted or skipped</b>, because
/// script 524 resolves each attempt row against a status row <i>of the same run</i>, and script 520's
/// <c>Skip</c> mode UPDATEs and never INSERTs. The journal's own flush order
/// (<c>Enumerate → Attempt → Fail → Skip → 524</c>) enforces this within one flush; the orchestrator's
/// obligation is not to skip a version it never enumerated.
/// </item>
/// </list>
/// <para>
/// <b>Every exit path flushes and closes the run, and neither of those uses the caller's cancellation
/// token.</b> A token that cancels the fetching and also cancels the record of the cancellation loses the
/// buffered status rows — from the one table that exists to make loss visible — and leaves
/// <c>logs.LoadRun</c> at <c>Running</c>, which is indistinguishable from a run still going until the
/// abandonment sweep twelve hours later. So the fetch takes the token and every write takes
/// <see cref="CancellationToken.None"/>, bounded by the command timeout instead.
/// </para>
/// <para>
/// <b>The watermark moves to where the walk got without a gap, and no further.</b> It moves to
/// <see cref="SummaryWalkReport.ContiguouslyWalkedThrough"/> — the end of the last window in an unbroken run
/// of walked windows from the start of the range — and only when no version the plan named was left
/// unfetched or failed. When every window landed, that date <i>is</i> the end of the requested range, so a
/// clean run behaves as it always did.
/// </para>
/// <para>
/// <b>[R41] The code lists deliberately do NOT gate the watermark, and they used to.</b> The rule was a
/// conjunction of three conditions, the third being that every list refreshed — and EPA answers <c>200</c>
/// with <c>[]</c> for two state-scoped lists, which this loader refuses (a <c>Full</c> send of an empty array
/// retires the list). <b>Retrying produces the identical answer</b>, so unlike the other two conditions that
/// one could never clear itself: the bookmark was pinned permanently and every scheduled run re-walked the
/// whole range for as long as EPA kept answering that way. Two things make dropping it safe rather than
/// merely convenient. First, the condition was already unreachable in the case it was written for — a code
/// list that fails <i>fatally</i> stops the run before the walk, so <see cref="SummaryWalkReport"/> is null
/// and the watermark is never considered; the only case it governed was the self-perpetuating one. Second,
/// a stale code list is not a missing date: it renders a label wrongly and cannot corrupt a handler record,
/// and holding the handler bookmark does nothing to make the list refresh. The shortfall is still reported —
/// the run reports <see cref="LoadRunOutcome.PartiallySucceeded"/>, <see cref="LoadRunLog.LookupsIncomplete"/>
/// names the counts, and every list's status is in <c>logs.LookupRefresh</c> — so nothing became invisible;
/// only the bookmark stopped being held hostage.
/// </para>
/// <para>
/// <b>Both halves of that rule answer the same question, and it is not "did the run work".</b> The
/// watermark is the only thing that decides whether a date is ever asked for again, so the test is whether
/// a date can be given up. A window that did not land cannot: nothing later would ever reveal the hole,
/// because <c>/hd/sources/summaries</c> returns no envelope. A version that failed cannot either — the next
/// scheduled run walks a <i>later</i> range, so it never names that version, and the resume read reports it
/// as vanished rather than fetching it. But a version that failed cannot be attributed to a window from the
/// walk report, so one failure holds the bookmark entirely rather than trimming it; the next run re-walks
/// the same range, the resume read skips the successes, and the one failure is retried.
/// </para>
/// <para>
/// <b>Stopping at the first gap rather than refusing to move at all is what makes a long catch-up
/// finish.</b> Nothing guarantees the scheduled task ran, so a missed week, a month of a disabled action
/// and a first load covering the whole notification era all arrive as one wide range — roughly 2,400
/// windows at the default width for the last of those. Requiring all 2,400 before any progress is durable
/// means one transient <c>500</c> in hour six costs the whole night, every night, and the load never
/// converges. Banking the unbroken prefix costs a re-walk of the windows after the gap, at one request
/// each, whose versions the merge then reports <c>Unchanged</c>.
/// </para>
/// <para>
/// <b>There is a second entry point, and it deliberately does three fewer things.</b>
/// <see cref="RunTargetedAsync"/> fetches one handler asked for by hand — the latest record, or its entire
/// history. It reads no watermark, moves none, refreshes no code list and resumes from nothing, and it opens
/// its run as <c>RunMode = 'Targeted'</c> so that scripts 510 and 525 can tell it apart from the load they
/// protect. What it shares is everything after "which versions": the journal, <see cref="FetchAsync"/>, the
/// merge, the soft delete and the closing write are the same code, because a diagnostic that wrote handler
/// rows by a different path would be a second merge to keep correct.
/// </para>
/// <para>
/// <b><c>other-ids</c> is not fetched, and that is a named gap rather than a decision.</b> See
/// <see cref="ILoadRunWriter"/>: <c>dbo.HandlerOtherIdentifier</c> has a table and a client and no write
/// path, and fetching a payload with nowhere to put it would spend the requests — roughly doubling the
/// initial load's request count (G14, [R8]) — and report success.
/// </para>
/// </remarks>
/// <param name="writer">The run lifecycle and the two data writes.</param>
/// <param name="lookups">The lookup refresh stage (G15).</param>
/// <param name="walk">The date-windowed summaries walk.</param>
/// <param name="resume">The resume read and the plan.</param>
/// <param name="reconcile">
/// The <c>CurrentRecord</c> reconciliation stage (§D4). Runs after the merge, because the fact it asserts is a
/// property of a handler's whole lineage rather than of any one version, and the lineage is only settled once
/// this run's versions are in.
/// </param>
/// <param name="journals">Makes the journal, which needs the run identifier this class obtains.</param>
/// <param name="client">The data client, for the per-version fetch.</param>
/// <param name="pacer">
/// The rate limiter, read <b>only</b> for its reservation count. It is the one place that knows how many
/// HTTP requests were actually issued, because the resilience pipeline retries below this class and a
/// retry is invisible from here.
/// </param>
/// <param name="options">What the run is scoped to.</param>
/// <param name="throttle">
/// The throttle settings, read only for <see cref="RcraInfoThrottleOptions.MaxConcurrentRequests"/>. The
/// fetch loop's degree of parallelism has exactly one correct value and the transport owns it; a second
/// setting here would be two numbers that have to agree.
/// </param>
/// <param name="logger">Counts, dates, run identifiers and stage names only (AR8).</param>
public sealed class LoadRun(
    ILoadRunWriter writer,
    ILookupRefresh lookups,
    ISummaryWalk walk,
    ILoadResume resume,
    ICurrentRecordReconcile reconcile,
    ILoadJournalFactory journals,
    IRcraInfoDataClient client,
    RequestPacer pacer,
    IOptions<LoadRunOptions> options,
    IOptions<RcraInfoThrottleOptions> throttle,
    ILogger<LoadRun> logger) : ILoadRun
{
    /// <summary>
    /// The only feed name Phase 1 uses, seeded as <c>('HandlerSource', 'MD')</c> by script 340.
    /// </summary>
    /// <remarks>
    /// A constant rather than a setting. <c>config.LoadWatermark.FeedName</c> exists so a second watermarked
    /// feed would not require a grain change (script 340's own remarks); it is not something an operator
    /// chooses, and a configurable value would let a typo silently create a second watermark row that
    /// starts every run from the beginning of time.
    /// </remarks>
    public const string HandlerSourceFeed = "HandlerSource";

    /// <summary>
    /// The <c>logs.LoadRun.RunMode</c> a single-handler run records — <c>CK_logs_LoadRun_RunMode</c>'s fourth
    /// value.
    /// </summary>
    /// <remarks>
    /// <b>A mode rather than a flag column, because two other procedures have to be able to tell.</b> Script
    /// 525 must not offer a targeted run as something to resume from — as the newest row it would disqualify
    /// resume outright and re-fetch an abandoned population run's several hundred thousand recorded successes
    /// — and script 510 must not let an interrupted one refuse the scheduled load for the next twelve hours.
    /// Both of those are predicates in T-SQL, so the distinction has to be a value in a column those
    /// procedures already read.
    /// </remarks>
    public const string TargetedRunMode = "Targeted";

    /// <summary>The three terminal values <c>CK_logs_LoadRun_Status</c> admits for a finished run.</summary>
    private const string Succeeded = "Succeeded";
    private const string PartiallySucceeded = "PartiallySucceeded";
    private const string Failed = "Failed";

    private readonly LoadRunOptions runOptions = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<LoadRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        // Validated here as well as by ValidateOnStart, because this class is also reachable from a test
        // and from any future host that forgets to call it -- and an unusable ActivityLocation is the one
        // defect that produces a successful national load rather than an error (plan [R28]).
        IReadOnlyList<string> problems = runOptions.Validate();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The {LoadRunOptions.SectionName} configuration section is not usable, so no load was "
                + $"attempted: {string.Join(" ", problems)}");
        }

        string location = runOptions.NormalizedActivityLocation();

        LoadWatermark? watermark = await writer
            .ReadWatermarkAsync(HandlerSourceFeed, location, cancellationToken)
            .ConfigureAwait(false);

        if (Refuse(watermark, location) is { } refusal)
        {
            return refusal;
        }

        string runMode = watermark!.RecommendedRunMode!;

        // Not RecommendedFromDate!.Value: on a full load there is no recommendation, and Refuse above has
        // already established that the floor supplies one.
        DateOnly fromDate = FullLoadFromDate(watermark)!.Value;
        DateOnly toDate = watermark.RecommendedToDate!.Value;

        LoadRunLog.RunPlanned(
            logger,
            runMode,
            location,
            fromDate,
            toDate,
            runOptions.WindowDays,
            watermark.RecommendedOverlapDaysApplied ?? 0);

        // THE ORDERING HAZARD, and the whole reason this call is here rather than three lines down.
        // logs.uspStartLoadRun takes @ResumedFromLoadRunId as an input, so the resume answer must exist
        // before the new run's row does -- and the abandonment sweep lives in that same procedure, so at
        // this instant a run killed by a reboot is still marked 'Running'. See LoadRunOptions.
        LoadResumePoint point = await resume
            .ReadAsync(loadRunId: null, cancellationToken)
            .ConfigureAwait(false);

        int loadRunId;

        try
        {
            loadRunId = await writer.StartRunAsync(
                new LoadRunRequest
                {
                    RunMode = runMode,
                    ActivityLocation = location,
                    RequestedFromDate = fromDate,
                    RequestedToDate = toDate,
                    WatermarkBeforeDate = watermark.WatermarkDate,
                    OverlapDaysApplied = watermark.RecommendedOverlapDaysApplied,
                    ResumedFromLoadRunId = point.ResumedFromLoadRunId,
                    ApplicationVersion = ApplicationVersion(),
                    AbandonAfterMinutes = runOptions.AbandonAfterMinutes,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException error) when (SqlErrorNumbers.IsProcedureRefusal(error))
        {
            // Not retried and not reported as a load failure. The refusal means the previous run is still
            // working, which is the state script 510 exists to protect -- and SqlErrorNumbers.IsRetryable
            // returns false for it precisely so no resilience layer turns a refusal into a second run.
            LoadRunLog.AlreadyRunning(logger, location, runOptions.AbandonAfterMinutes);

            return LoadRunResult.NotStarted(
                LoadRunOutcome.AlreadyRunning,
                $"A load run is already live for {location}, so this one did not open. Nothing was "
                + "fetched.");
        }

        LoadRunLog.RunOpened(logger, loadRunId, point.ResumedFromLoadRunId);

        return await RunOpenedAsync(
            loadRunId,
            runMode,
            location,
            fromDate,
            toDate,
            point,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything after the run row exists — and therefore everything that must close it.</summary>
    private async Task<LoadRunResult> RunOpenedAsync(
        int loadRunId,
        string runMode,
        string location,
        DateOnly fromDate,
        DateOnly toDate,
        LoadResumePoint point,
        CancellationToken cancellationToken)
    {
        long requestsBefore = pacer.Reservations;

        LookupStageReport? lookupReport = null;
        SummaryWalkReport? walkReport = null;
        ReconcileReport? reconcileReport = null;
        LoadJournalFlush journalTotal = LoadJournalFlush.Empty;
        FetchTally tally = new();
        DateOnly? watermarkMovedTo = null;
        string? failureMessage = null;
        bool cancelled = false;

        await using ILoadJournal journal = journals.Create(loadRunId);

        try
        {
            // 1. Lookups first (G15).
            lookupReport = await lookups.RefreshAllAsync(loadRunId, cancellationToken).ConfigureAwait(false);

            LoadRunLog.LookupsRefreshed(
                logger,
                lookupReport.RefreshedCount,
                lookupReport.Reports.Count,
                lookupReport.TotalRetired);

            if (!lookupReport.MayContinue)
            {
                // A fatal outcome on a code list is a condition of the credential or the connection, not of
                // that list, and the fetch loop would meet the same one several hundred thousand times.
                // G15's ordering means it is met once.
                cancelled = lookupReport.WasCancelled;
                failureMessage = LookupFailure(lookupReport);

                if (!cancelled)
                {
                    LoadRunLog.LookupsFatal(logger, lookupReport.FatalOutcome?.ToString() ?? "cancellation");
                }
            }
            else
            {
                if (lookupReport.UnrefreshedCount > 0)
                {
                    LoadRunLog.LookupsIncomplete(
                        logger, lookupReport.UnrefreshedCount, lookupReport.Reports.Count);
                }

                // 2. Walk the change feed.
                walkReport = await walk
                    .WalkAsync(loadRunId, fromDate, toDate, cancellationToken)
                    .ConfigureAwait(false);

                LoadRunLog.WalkComplete(
                    logger,
                    walkReport.WalkedCount,
                    walkReport.Windows.Count,
                    walkReport.Versions.Count,
                    walkReport.SummaryCount);

                if (!walkReport.MayContinue)
                {
                    // Same reasoning as the lookups, and the same asymmetry the walk itself is built around:
                    // a window that failed named no versions at all, so there is nothing to fetch that this
                    // run could have fetched. What it did name is enumerated below either way.
                    cancelled = walkReport.WasCancelled;
                    failureMessage = WalkFailure(walkReport);

                    if (!cancelled)
                    {
                        LoadRunLog.WalkFatal(
                            logger,
                            walkReport.FatalOutcome?.ToString() ?? "cancellation",
                            walkReport.WalkedCount,
                            walkReport.Windows.Count);
                    }
                }

                // 3. Enumerate everything the walk named -- including what will be skipped, because
                // script 520's Skip mode UPDATEs and never INSERTs, and script 524 resolves an attempt
                // against a status row of the same run.
                await journal.EnumerateAsync(
                    walkReport.Versions.Select(ToStatusElement),
                    CancellationToken.None).ConfigureAwait(false);

                // 4. Subtract what a previous run already finished.
                LoadResumePlan plan = resume.Plan(point, walkReport.Versions.Select(s => s.ToVersion()));

                await journal.SkipAsync(plan.ToSkip, CancellationToken.None).ConfigureAwait(false);

                tally.Enumerated = plan.WalkedCount;
                tally.Skipped = plan.ToSkip.Count;

                // 4b. FLUSHED HERE, BEFORE ANYTHING MERGES, AND THE STEP EXISTS BECAUSE F1 FOUND ITS
                // ABSENCE. Steps 3 and 4 only BUFFER: the journal is write-behind and flushes when
                // FlushRowCount or FlushInterval says so. But dbo.uspMergeHandlerSourceBatch writes the
                // SUCCESS half of these very rows from OUTSIDE the journal -- script 400 UPDATEs
                // logs.HandlerLoadStatus and deliberately never INSERTs, because 'handler X was inserted'
                // is only true if it committed. A row still sitting in the buffer when the merge runs is
                // therefore a row the merge CANNOT FIND: the version commits, the run closes Succeeded,
                // and the status row keeps Status = 'InProgress' with a NULL Outcome, CompletedDateUtc and
                // HandlerSourceId for good. That is not cosmetic -- the monitoring web app reads this
                // table, and script 525 treats an InProgress row as work to resume, so the next run
                // re-fetches versions that had already succeeded.
                //
                // Measured on 2026-09-06 during F1, twice, on runs 2139 and 2140: one version apiece, so
                // the buffer never came near FlushRowCount and NOTHING had been written when the merge
                // looked. The ordering the class remarks promise holds WITHIN a flush; it could never hold
                // against a writer that is not the journal.
                journalTotal = journalTotal.Add(
                    await Flush(journal, loadRunId).ConfigureAwait(false));

                // 5. Fetch, merge and soft delete, in batches.
                if (walkReport.MayContinue)
                {
                    journalTotal = journalTotal.Add(await FetchAsync(
                        loadRunId, plan, journal, tally, cancellationToken).ConfigureAwait(false));

                    cancelled = tally.Cancelled;
                    failureMessage ??= tally.FatalOutcome is { } fatal
                        ? $"The fetch loop stopped on {fatal}, leaving {tally.Unfetched} version(s) "
                          + "unfetched. That outcome is fatal to the run rather than to one version."
                        : null;

                    // 5b. RE-ASSERT CurrentRecord PER LINEAGE, and it has to be a separate stage rather than
                    // part of the merge. EPA's currentRecord is a property of a handler's WHOLE version list
                    // delivered as a property of ONE version: adding version 13 says nothing about version 12,
                    // and version 12 is not in this window's feed because version 12 did not change. Script 400
                    // mirrors every version faithfully -- CurrentRecord IS NULL is zero across the whole
                    // mirror -- and faithful per-version mirroring is exactly what leaves the lineage wrong.
                    // Measured on live Maryland data at MDR000501742/N, where sequences 12 and 13 both claim
                    // to be current (Analysis §5.2 item 2, plan §D4).
                    //
                    // THE TRIGGER SET IS EVERY HANDLER THE WALK NAMED, NOT EVERY HANDLER THAT CHANGED, and the
                    // difference is the whole point: the version whose flag is now wrong is a SIBLING of the
                    // one that changed, so a handler whose fetch reported Unchanged can still hold a stale
                    // flag from an earlier run. A changed-only trigger set would be the smaller, faster,
                    // wrong answer.
                    if (tally.FatalOutcome is null && !cancelled && walkReport.Versions.Count > 0)
                    {
                        reconcileReport = await reconcile.ReconcileAsync(
                            loadRunId,
                            [.. walkReport.Versions.Select(summary => summary.HandlerId!)],
                            cancellationToken).ConfigureAwait(false);

                        tally.Calls += reconcileReport.Calls;
                        cancelled = cancelled || reconcileReport.WasCancelled;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Reached only if a stage propagates rather than reporting. Every stage in this pipeline is
            // built to report cancellation as a report, so this is the net under that -- and the run still
            // has to be flushed and closed.
            cancelled = true;
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException or HttpRequestException)
        {
            // Composed, never error.ToString (). The same string goes to logs.LoadRun.FailureMessage, which
            // scripts 500 and 502 return to a web page, and an HttpRequestException message can carry a
            // request URI -- which on this API is where the credential travels.
            failureMessage =
                $"The load stopped on an unhandled {error.GetType().Name}. See logs.ExecutionLog and the "
                + "application log for the detail; it is deliberately not copied into this column.";

            LoadRunLog.RunFailed(logger, loadRunId, failureMessage);
        }

        // THE FLUSH, on every path above. Not conditional and not cancellable: buffered status rows lost at
        // shutdown are invisible data loss in the one table that exists to make loss visible.
        journalTotal = journalTotal.Add(await Flush(journal, loadRunId).ConfigureAwait(false));

        if (journalTotal.HasDefects)
        {
            LoadRunLog.JournalDefects(
                logger, journalTotal.RowsOrphaned, journalTotal.ValuesWithheld, journalTotal.Calls);
        }

        bool everyVersionAccountedFor =
            failureMessage is null && !cancelled && tally.Failed == 0 && tally.Unfetched == 0;

        bool lookupsComplete = lookupReport is { MayContinue: true, UnrefreshedCount: 0 };

        // 6. The watermark. As far as the walk got without a gap, and no further -- see the class remarks.
        // ContiguouslyWalkedThrough equals toDate exactly when every window landed, so a complete run moves
        // the bookmark to the end of the requested range as it always did.
        DateOnly? advanceTo = walkReport is { MayContinue: true } ? walkReport.ContiguouslyWalkedThrough : null;

        // [R41] lookupsComplete is deliberately NOT a term here -- see the class remarks. It still decides the
        // run's OUTCOME below, which is where a stale code list belongs.
        if (advanceTo is { } target && everyVersionAccountedFor)
        {
            watermarkMovedTo = await AdvanceWatermarkAsync(
                loadRunId, location, target, toDate, walkReport!).ConfigureAwait(false);
        }
        else if (walkReport is not null)
        {
            LoadRunLog.WatermarkHeld(
                logger,
                HoldReason(walkReport, advanceTo, everyVersionAccountedFor, tally, cancelled, failureMessage));
        }

        LoadRunCounters counters = Counters(
            lookupReport, tally, pacer.Reservations - requestsBefore, walkReport);

        LoadRunOutcome outcome = Outcome(
            cancelled, failureMessage, everyVersionAccountedFor, lookupsComplete, walkReport, reconcileReport);

        LoadRunResult result = new(
            outcome,
            loadRunId,
            runMode,
            counters,
            lookupReport,
            walkReport,
            reconcileReport,
            journalTotal,
            watermarkMovedTo,

            // The reconcile's own message only if nothing earlier failed, and READ AFTER the watermark branch
            // above rather than merged into failureMessage before it. That ordering is the decision §D4 turns
            // on: an unasserted lineage downgrades the run and must not hold the bookmark, because a stale
            // CurrentRecord flag is findable by query and fixable by a targeted run, where an unwalked date
            // range is neither. See ReconcileReport.
            failureMessage ?? reconcileReport?.FailureMessage);

        await CloseAsync(loadRunId, result).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<LoadRunResult> RunTargetedAsync(
        TargetedLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Both, and in one message. The configuration still has to be usable -- the activity location governs
        // the scope check on the ANSWER below, which is the one thing a handlerId request cannot ask EPA for.
        IReadOnlyList<string> problems = [.. runOptions.Validate(), .. request.Validate()];

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "No targeted load was attempted, because the request or the "
                + $"{LoadRunOptions.SectionName} configuration section is not usable: "
                + string.Join(" ", problems));
        }

        string location = runOptions.NormalizedActivityLocation();

        // NO WATERMARK READ, and its absence is the point rather than an omission. This run covers no date
        // range, so there is nothing for a watermark to recommend and nothing it could later claim to have
        // covered -- and reading one would additionally make a targeted run refuse itself on a disabled feed,
        // which is exactly the moment an operator most needs to ask EPA what it holds.
        int loadRunId;

        try
        {
            loadRunId = await writer.StartRunAsync(
                new LoadRunRequest
                {
                    RunMode = TargetedRunMode,
                    ActivityLocation = location,

                    // The three date columns stay null, which is what tells a later reader this row cannot
                    // have moved the bookmark. Script 510 permits that combination for this mode only.
                    ApplicationVersion = ApplicationVersion(),
                    AbandonAfterMinutes = runOptions.AbandonAfterMinutes,

                    // Set for this mode, and script 510 exempts 'Targeted' from the refusal anyway. Both,
                    // deliberately: the flag is what an older deployment of 510 would honour, and the
                    // exemption is what stops an interrupted targeted run from refusing the nightly load.
                    AllowConcurrent = true,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException error) when (SqlErrorNumbers.IsProcedureRefusal(error))
        {
            // Reachable only against a database where script 510 has not been re-run, since the amended
            // procedure exempts this mode from both halves of the check. Reported rather than thrown, so the
            // operator gets the same exit code and the same sentence a scheduled collision produces.
            LoadRunLog.AlreadyRunning(logger, location, runOptions.AbandonAfterMinutes);

            return LoadRunResult.NotStarted(
                LoadRunOutcome.AlreadyRunning,
                $"logs.uspStartLoadRun refused to open a Targeted run for {location}. That mode is exempt "
                + "from the in-flight refusal in the current script 510, so this database is running an "
                + "earlier version of it -- re-run 300_logs.LoadRun.sql and 510_logs.uspStartLoadRun.sql.");
        }

        LoadRunLog.RunOpened(logger, loadRunId, null);

        // Formatted into a local first, because CA1873 is an error here: an Enum.ToString () inside the
        // logging call runs whether or not anyone is listening. Once per run, so it costs nothing either way.
        string scope = request.Scope.ToString();

        LoadRunLog.TargetedRunPlanned(logger, location, scope, loadRunId);

        return await RunTargetedOpenedAsync(
            loadRunId, location, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything after the targeted run's row exists — and therefore everything that must close it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The enumeration is one call and it is the summaries endpoint's other documented form.</b>
    /// <c>/hd/sources/summaries?handlerId=…</c> names no dates and no activity location — the spec presents the
    /// two forms as alternatives, and a request satisfying both invites EPA to choose one — so the state check
    /// has nowhere to happen except on the answer. It happens through
    /// <see cref="SummaryWalk.FindOutOfScope"/>, the same method the scheduled walk uses, and it refuses the
    /// answer whole for that method's reason.
    /// </para>
    /// <para>
    /// <b>Nothing to fetch is recorded as a failure, not as a quiet success.</b> A handler that does not
    /// exist, a handler in another state, and a handler EPA flags no current record for all end here — and all
    /// three are answers an operator has to see. Reporting them as <c>Succeeded</c> would return exit code 0
    /// from a run that retrieved nothing, which is the one thing this project's exit codes exist to prevent.
    /// </para>
    /// <para>
    /// <b>The <see cref="TargetedVersionScope.CurrentRecord"/> path never guesses.</b> If the feed flags
    /// nothing as current, the run says so rather than taking the highest sequence: a guessed current record
    /// merges into <c>dbo.HandlerSource</c> indistinguishably from one EPA vouched for, and script 521 would
    /// then reconcile <c>IsCurrentRecord</c> against it.
    /// </para>
    /// </remarks>
    private async Task<LoadRunResult> RunTargetedOpenedAsync(
        int loadRunId,
        string location,
        TargetedLoadRequest request,
        CancellationToken cancellationToken)
    {
        long requestsBefore = pacer.Reservations;

        LoadJournalFlush journalTotal = LoadJournalFlush.Empty;
        FetchTally tally = new();
        ReconcileReport? reconcileReport = null;
        string? failureMessage = null;
        bool cancelled = false;

        await using ILoadJournal journal = journals.Create(loadRunId);

        try
        {
            ApiFetchResult enumerated = await client.FetchAsync(
                RcraInfoDataRequest.SummariesForHandler(request.Normalized()),
                cancellationToken).ConfigureAwait(false);

            // Counted here rather than in FetchAsync, because Counters derives the retry count by subtracting
            // logical calls from the pacer's request count -- and an uncounted call reads as a retry.
            tally.Calls++;

            IReadOnlyList<HandlerSourceSummary> selected = [];
            SummaryPayloadRead? read = null;

            if (enumerated.Outcome == ApiFetchOutcome.Cancelled)
            {
                cancelled = true;
            }
            else if (enumerated.Outcome == ApiFetchOutcome.NotFound)
            {
                // The endpoint documents 404, and on THIS form it means "no summaries for that handlerId".
                // Nothing is soft-deleted: AR7's soft delete is a 404 for a version this loader NAMED, and
                // nothing has been named yet. Withdrawing a handler's records because a hand-typed identifier
                // came back empty would delete real rows over a typo.
                failureMessage =
                    $"EPA has no handler source summaries for handler '{request.Normalized()}'. Nothing was "
                    + "fetched and NOTHING WAS SOFT DELETED -- a 404 here means the identifier matched no "
                    + "record, which a mistyped one also does, and the AR7 soft delete applies only to a "
                    + "version this loader had already enumerated.";
            }
            else if (enumerated.Outcome != ApiFetchOutcome.Succeeded)
            {
                failureMessage =
                    $"The targeted enumeration came back {enumerated.Outcome} with no usable body (HTTP "
                    + $"{enumerated.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}), so "
                    + "no version was named and nothing was fetched.";
            }
            else
            {
                read = SummaryPayload.Read(enumerated.Payload);

                if (!read.IsReadable)
                {
                    failureMessage =
                        "The targeted enumeration returned a body that could not be read, so no version was "
                        + $"named: {read.Problem}";
                }
                else if (SummaryWalk.FindOutOfScope(read.Summaries, location) is string outOfScope)
                {
                    // The answer named another state. Refused whole, for SummaryWalk.FindOutOfScope's reason
                    // -- and here the request could carry no activityLocation at all, so this check is the
                    // only thing standing between a hand-typed identifier and another state's records.
                    failureMessage =
                        "The targeted enumeration named a handler outside the activity location this "
                        + $"installation loads, so it was refused whole: {outOfScope}";
                }
                else
                {
                    selected = Select(read.Summaries, request.Scope);

                    LoadRunLog.TargetedVersionsSelected(
                        logger, selected.Count, read.Summaries.Count, read.CurrentRecordCount);

                    if (selected.Count == 0)
                    {
                        failureMessage = NothingSelected(request, read);

                        LoadRunLog.TargetedNothingToFetch(logger, failureMessage);
                    }
                }
            }

            if (selected.Count > 0)
            {
                // Enumerated before anything is attempted, for the reason the class remarks give: script 524
                // resolves an attempt against a status row of the same run.
                await journal.EnumerateAsync(
                    selected.Select(ToStatusElement), CancellationToken.None).ConfigureAwait(false);

                // AND FLUSHED IMMEDIATELY, because EnumerateAsync only BUFFERS. See step 4b of
                // RunOpenedAsync for the whole reasoning: script 400 UPDATEs these same status rows from
                // outside the journal and never INSERTs, so a buffered row is a row the merge cannot find
                // and the version ends a Succeeded run reading InProgress. A targeted run is the WORST
                // case, not an edge one -- one or two versions never come near FlushRowCount, so without
                // this nothing whatsoever has been written when the merge runs. Both F1 passes proved it.
                journalTotal = journalTotal.Add(
                    await Flush(journal, loadRunId).ConfigureAwait(false));

                // FetchAll and not resume.Plan: an operator asking for a handler now wants it now, so nothing
                // is skipped on the strength of an older run having succeeded on it. The cost is one request
                // per version and a merge that reports Unchanged.
                LoadResumePlan plan = LoadResumePlan.FetchAll(selected.Select(s => s.ToVersion()));

                tally.Enumerated = plan.WalkedCount;

                journalTotal = journalTotal.Add(await FetchAsync(
                    loadRunId, plan, journal, tally, cancellationToken).ConfigureAwait(false));

                cancelled = tally.Cancelled;
                failureMessage ??= tally.FatalOutcome is { } fatal
                    ? $"The fetch loop stopped on {fatal}, leaving {tally.Unfetched} version(s) unfetched."
                    : null;

                // RECONCILED FROM THE LIST ALREADY IN HAND, AND WITH NO SECOND REQUEST. read.Summaries is the
                // answer to /hd/sources/summaries?handlerId=... -- which is precisely the complete lineage
                // script 521 requires -- so the targeted path is the one place the reconcile costs nothing.
                //
                // read.Summaries AND NOT `selected`, which is the mistake this comment exists to prevent:
                // --current-record selects one version, and submitting only that one would tell script 521 that
                // the lineage consists of one version, so every OTHER version of it would be demoted to
                // CurrentRecord = 0 and reported as VersionNotInSourceSummary.
                //
                // This is also the repair path for a lineage that is already wrong: a targeted run on one
                // handler re-asserts its whole lineage, whatever scope was asked for, without touching a
                // watermark or a date range.
                if (read is not null && !cancelled && tally.FatalOutcome is null)
                {
                    reconcileReport = await reconcile.ReconcileKnownAsync(
                        loadRunId,
                        request.Normalized(),
                        read.Summaries,
                        cancellationToken).ConfigureAwait(false);

                    cancelled = cancelled || reconcileReport.WasCancelled;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException or HttpRequestException)
        {
            // Composed, never error.ToString (), for RunOpenedAsync's reason: an HttpRequestException message
            // can carry a request URI, and on this API that is where the credential travels.
            failureMessage =
                $"The targeted load stopped on an unhandled {error.GetType().Name}. See logs.ExecutionLog and "
                + "the application log for the detail; it is deliberately not copied into this column.";

            LoadRunLog.RunFailed(logger, loadRunId, failureMessage);
        }

        journalTotal = journalTotal.Add(await Flush(journal, loadRunId).ConfigureAwait(false));

        if (journalTotal.HasDefects)
        {
            LoadRunLog.JournalDefects(
                logger, journalTotal.RowsOrphaned, journalTotal.ValuesWithheld, journalTotal.Calls);
        }

        // NO WATERMARK BRANCH AT ALL, and there is deliberately not even a held-watermark log line. A held
        // watermark is a fact about a run that asked for days and did not get them; this run asked for none,
        // so reporting one either way would invite the reading that a targeted run could have moved it.
        LoadRunCounters counters = Counters(null, tally, pacer.Reservations - requestsBefore, null);

        LoadRunResult result = new(
            TargetedOutcome(cancelled, failureMessage, tally, reconcileReport),
            loadRunId,
            TargetedRunMode,
            counters,
            null,
            null,
            reconcileReport,
            journalTotal,
            null,
            failureMessage ?? reconcileReport?.FailureMessage);

        await CloseAsync(loadRunId, result).ConfigureAwait(false);

        return result;
    }

    /// <summary>The versions a targeted run will fetch, from the scope the operator asked for.</summary>
    private static IReadOnlyList<HandlerSourceSummary> Select(
        IReadOnlyList<HandlerSourceSummary> summaries,
        TargetedVersionScope scope) =>
        scope == TargetedVersionScope.EveryVersion
            ? summaries
            : [.. summaries.Where(summary => summary.CurrentRecord)];

    /// <summary>Why a readable, in-scope answer still yielded nothing to fetch.</summary>
    /// <remarks>
    /// Two distinct causes with two distinct operator actions, which is why they are not one message. An empty
    /// feed means the identifier is wrong or the handler is not in this state's data; a feed with rows but no
    /// flagged current record means EPA holds history for the handler and marks none of it current, and
    /// <c>--every-version</c> will return it.
    /// </remarks>
    private static string NothingSelected(TargetedLoadRequest request, SummaryPayloadRead read) =>
        read.Summaries.Count == 0
            ? $"EPA returned no handler source summaries for handler '{request.Normalized()}', so there was "
              + "nothing to fetch. Either the identifier does not exist, or it belongs to a handler outside "
              + "the activity location this installation loads."
            : $"EPA returned {read.Summaries.Count} summary row(s) for handler '{request.Normalized()}' and "
              + "flagged none of them as its current record, so the CurrentRecord scope selected nothing. The "
              + "highest sequence is deliberately NOT assumed to be current: a guessed current record is "
              + "indistinguishable in dbo.HandlerSource from one EPA vouched for. Ask for the entire history "
              + "instead, which fetches all of them.";

    /// <summary>The verdict for a targeted run, which has no walk and no lookups to weigh.</summary>
    /// <remarks>
    /// <b>Failed rather than Succeeded whenever nothing landed</b>, including the cases that are not this
    /// loader's fault. Exit code 0 from a run that retrieved nothing is the failure mode plan §4.2 names, and
    /// a targeted run is the one shape where "there was nothing to get" is a perfectly ordinary answer — so it
    /// is the one shape where that could happen by accident.
    /// </remarks>
    private static LoadRunOutcome TargetedOutcome(
        bool cancelled,
        string? failureMessage,
        FetchTally tally,
        ReconcileReport? reconcileReport)
    {
        if (cancelled)
        {
            return LoadRunOutcome.Cancelled;
        }

        // Nothing landed. Note that no condition on failureMessage guards this: the case it exists for is a
        // request that came back 200 with an empty array and no failure at all, and reporting THAT as success
        // is the one thing a targeted run must not do.
        if (tally.Fetched == 0 && tally.SoftDeleted == 0)
        {
            return LoadRunOutcome.Failed;
        }

        // The reconcile counts here as well, and on this path it is nearly free to get right: the lineage came
        // from the enumeration this run already paid for, so the only way it goes unasserted is a divergence
        // worth reporting -- an answer naming another state, another handler, or no version at all.
        return failureMessage is not null
            || tally.Failed > 0
            || tally.Unfetched > 0
            || reconcileReport is { Complete: false }
            ? LoadRunOutcome.PartiallySucceeded
            : LoadRunOutcome.Succeeded;
    }

    /// <summary>
    /// Fetches <see cref="LoadResumePlan.ToFetch"/> in batches, merging and soft deleting each batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Batched rather than streamed, and the batch is the checkpoint.</b> One
    /// <c>dbo.uspMergeHandlerSourceBatch</c> call per batch keeps the write set-based, bounds how many
    /// downloaded payloads are held in memory at once, and bounds what a killed run re-fetches — which is
    /// safe, because the merge reports an unchanged version as <c>Unchanged</c> and writes nothing.
    /// </para>
    /// <para>
    /// <b>Within a batch the fetches run at <see cref="RcraInfoThrottleOptions.MaxConcurrentRequests"/>,
    /// which is not a second rate limit.</b> The rate is the pacer's, in a <c>DelegatingHandler</c> below
    /// this class. Parallelism here exists only so the rate can actually be reached: a sequential loop
    /// achieves one request per round trip, so at a round trip longer than the pacing interval it would
    /// leave the configured budget unspent and make the loop, rather than the setting, the real limit.
    /// </para>
    /// <para>
    /// <b>The cancellation token reaches the fetch and nothing else.</b> The client reports cancellation as
    /// <see cref="ApiFetchOutcome.Cancelled"/> rather than throwing, so every version in the batch still
    /// gets a result and a status row. The journal writes, the merge and the soft delete all take
    /// <see cref="CancellationToken.None"/>: a batch whose payloads have already been paid for is finished
    /// rather than discarded, and it is bounded by <see cref="LoadRunOptions.FetchBatchSize"/>.
    /// </para>
    /// <para>
    /// <b>The journal is flushed above the writer, and that ordering is the batch's one real constraint.</b>
    /// <c>dbo.uspMergeHandlerSourceBatch</c> concludes these same <c>logs.HandlerLoadStatus</c> rows from
    /// outside the journal — by <c>UPDATE</c>, never <c>INSERT</c> — so a buffered row is a row it cannot
    /// find, and <c>logs.uspUpsertHandlerLoadStatusSet</c> then refuses a late <c>Attempt</c> write against
    /// the row the merge has already marked <c>Succeeded</c>. F1 measured both halves of that on live runs;
    /// the flush is the first statement after the conclude loop for that reason and must stay there.
    /// </para>
    /// </remarks>
    private async Task<LoadJournalFlush> FetchAsync(
        int loadRunId,
        LoadResumePlan plan,
        ILoadJournal journal,
        FetchTally tally,
        CancellationToken cancellationToken)
    {
        LoadJournalFlush flushed = LoadJournalFlush.Empty;

        // The ceiling is the writer's, applied here rather than validated against a copy of it.
        int batchSize = Math.Min(runOptions.FetchBatchSize, writer.MaxElementsPerCall);
        int batchCount = (plan.ToFetch.Count + batchSize - 1) / batchSize;
        int degree = Math.Max(1, throttle.Value.MaxConcurrentRequests);

        for (int batchNumber = 1; batchNumber <= batchCount; batchNumber++)
        {
            HandlerVersion[] batch = plan.ToFetch
                .Skip((batchNumber - 1) * batchSize)
                .Take(batchSize)
                .ToArray();

            ApiFetchResult[] results = new ApiFetchResult[batch.Length];

            // No CancellationToken on ParallelOptions, deliberately: it would throw before scheduling and
            // discard the results already in hand. The token goes to the fetch, which reports cancellation.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, batch.Length),
                new ParallelOptions { MaxDegreeOfParallelism = degree },
                async (index, _) =>
                {
                    HandlerVersion version = batch[index];

                    ApiFetchResult result = await client.FetchAsync(
                        RcraInfoDataRequest.Source(version.HandlerId, version.SourceType, version.Sequence),
                        cancellationToken).ConfigureAwait(false);

                    results[index] = result;

                    // Attempt number 1 on every call, and it is not a placeholder: the resilience pipeline
                    // retries below this class, so one logical fetch is one attempt row no matter how many
                    // HTTP requests it took. The request count that does include retries comes from the
                    // pacer, and logs.LoadRun records the difference.
                    await journal.RecordAttemptAsync(result, version, 1, CancellationToken.None)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);

            List<HandlerEnvelope> envelopes = new(batch.Length);
            List<HandlerKeyElement> gone = [];

            for (int index = 0; index < batch.Length; index++)
            {
                ApiFetchResult result = results[index];

                tally.Calls++;

                if (result.Outcome == ApiFetchOutcome.Cancelled)
                {
                    tally.Cancelled = true;
                }
                else if (result.Outcome.IsFatalToTheRun())
                {
                    tally.FatalOutcome ??= result.Outcome;
                }

                ApiFetchResult concluded = result;

                if (result.HasPayload)
                {
                    if (TryReadEnvelope(result, out HandlerEnvelope envelope, out string? problem))
                    {
                        envelopes.Add(envelope);
                        tally.Fetched++;
                    }
                    else
                    {
                        // A 200 whose body this loader cannot use is not a success, and recording it as one
                        // would leave a version marked Succeeded that no row in dbo.HandlerSource
                        // corresponds to -- and the resume read would then skip it forever. Reclassified as
                        // Unexpected, which is this project's word for "the spec and the service have
                        // diverged", and deliberately NOT fatal: one unreadable payload is not evidence
                        // about the next one. The problem names a JSON value kind, never any part of the
                        // body (AR8).
                        LoadRunLog.PayloadUnreadable(logger, index + 1, batchNumber, problem!);

                        concluded = result with
                        {
                            Outcome = ApiFetchOutcome.Unexpected,
                            Payload = null,
                            FailureMessage = problem,
                        };

                        tally.Failed++;
                    }
                }
                else if (result.IsGone)
                {
                    // AR7. The one place a 404 becomes a deletion, and it is a 404 from /hd/sources, which
                    // documents that status -- /hd/other-ids does not, which is why the client classifies
                    // against the endpoint's own documented set ([R29]).
                    gone.Add(batch[index].ToKeyElement());
                }
                else
                {
                    tally.Failed++;
                }

                await journal.ConcludeAsync(concluded, batch[index], CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // FLUSHED BEFORE THE WRITER, NOT AFTER, AND THE DIFFERENCE IS NOT COSMETIC. This flush used to
            // sit below the two writer calls, and F1 measured what that costs. The invariant is the one step
            // 4b of RunOpenedAsync states: dbo.uspMergeHandlerSourceBatch writes the success half of these
            // very status rows from OUTSIDE the journal, so any row still in the buffer when the writer runs
            // is a row the writer cannot find. Below the merge it fails TWICE over:
            //
            //   * the Pending row is missing, so the merge's UPDATE matches nothing and the version ends a
            //     Succeeded run reading Status = 'InProgress' with a NULL Outcome for good; and
            //   * the buffered Attempt row then arrives at a status row the merge has ALREADY marked
            //     Succeeded, and script 520 excludes Succeeded rows from Attempt mode by design -- it counts
            //     the refusal as skippedSucceeded and leaves AttemptCount at 0. That is the column an
            //     operator reads to judge whether a handler is stuck, and it would read zero attempts for a
            //     version that was fetched once and merged.
            //
            // Above the merge, the same rows are already in the table when it looks, in mode order, and
            // both columns come out right. Measured on runs 2139-2142 during F1: 2139 and 2140 with the
            // flush below the writer (Status wrong), 2141 with the status rows flushed but not the attempts
            // (AttemptCount wrong), 2142 with this ordering (both right).
            flushed = flushed.Add(await journal.FlushAsync(CancellationToken.None).ConfigureAwait(false));

            MergeCounts merged = MergeCounts.Empty;

            if (envelopes.Count > 0)
            {
                merged = await writer.MergeAsync(loadRunId, envelopes, CancellationToken.None)
                    .ConfigureAwait(false);

                tally.Merged = tally.Merged.Add(merged);
            }

            int softDeleted = 0;

            if (gone.Count > 0)
            {
                softDeleted = await writer.SoftDeleteAsync(
                    loadRunId,
                    gone,
                    $"EPA answered 404 for this version during load run {loadRunId} (AR7).",
                    CancellationToken.None).ConfigureAwait(false);

                tally.SoftDeleted += softDeleted;
            }

            // Deliberately no second flush here. Nothing buffers between the flush above and this point --
            // ConcludeAsync returns false for a success, so the journal is empty by the time the writer runs
            // -- and a flush below the writer is precisely the ordering F1 proved wrong. One flush per batch,
            // above the writer, is the whole rule.
            LoadRunLog.BatchMerged(
                logger,
                batchNumber,
                batchCount,
                envelopes.Count,
                merged.Inserted,
                merged.Updated,
                merged.Unchanged,
                softDeleted,
                tally.Failed);

            if (tally.FatalOutcome is null && !tally.Cancelled)
            {
                continue;
            }

            tally.Unfetched = Math.Max(0, plan.ToFetch.Count - (batchNumber * batchSize));

            if (tally.FatalOutcome is { } outcome)
            {
                LoadRunLog.FetchFatal(logger, batchNumber, batchCount, outcome.ToString(), tally.Unfetched);
            }

            break;
        }

        return flushed;
    }

    /// <summary>
    /// Turns a <c>200</c> body into a merge envelope, or reports why it is not one.
    /// </summary>
    /// <remarks>
    /// <b>The spec says one object, so an array is a divergence rather than a shape to accommodate.</b>
    /// <c>GET /api/v1/hd/sources/{handlerId}/{sourceType}/{sequence}</c> is documented as returning a single
    /// <c>HandlerSource</c>. Silently taking element zero of an array would discard every other element
    /// while reporting the version as loaded — so an array is refused, and the refusal names the value kind
    /// and the element count and nothing else.
    /// </remarks>
    private static bool TryReadEnvelope(
        ApiFetchResult result,
        out HandlerEnvelope envelope,
        out string? problem)
    {
        envelope = null!;
        problem = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Payload!);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem =
                    $"EPA answered 200 with a JSON {document.RootElement.ValueKind} where the spec "
                    + "documents a single HandlerSource object. This version was not merged.";

                return false;
            }

            envelope = new HandlerEnvelope
            {
                // EPA's completion time as this loader observed it, not SYSUTCDATETIME (): the merge stores
                // it as when the payload was retrieved, and a value taken at merge time would drift by
                // however long the batch took.
                RetrievedDateUtc = result.CompletedDateUtc,

                // Cloned because the JsonDocument is disposed on the way out of this method.
                Handler = document.RootElement.Clone(),
            };

            return true;
        }
        catch (JsonException error)
        {
            // The line and position are safe -- they are offsets, not content. error.Message is not
            // reproduced, because System.Text.Json includes the offending token in it.
            problem =
                $"EPA answered 200 with a body that is not valid JSON, at line "
                + $"{error.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"} position "
                + $"{error.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "?"}. This "
                + "version was not merged.";

            return false;
        }
    }

    /// <summary>Refuses to open a run, for the three reasons that are not failures of a load.</summary>
    /// <param name="watermark">What script 512 returned, if anything.</param>
    /// <param name="location">The configured activity location.</param>
    /// <returns>The refusal, or <see langword="null"/> to go ahead.</returns>
    private LoadRunResult? Refuse(LoadWatermark? watermark, string location)
    {
        // Resolved before the shape is judged, because on a FULL load the absent from-date is not a broken
        // row -- it is script 512 saying "ask for everything", and "everything" is a date this loader has to
        // supply. See FullLoadFromDate.
        DateOnly? fromDate = FullLoadFromDate(watermark);
        DateOnly? toDate = watermark?.RecommendedToDate;

        string? problem = watermark switch
        {
            null => $"config.uspGetLoadWatermark returned no row for {HandlerSourceFeed}/{location}.",
            { RecommendedRunMode: null or "" } =>
                "the watermark row recommends no run mode, so there is nothing to ask for.",
            { RecommendedToDate: null } =>
                "the watermark row recommends no end date, so there is nothing to ask for.",

            // THE INITIAL LOAD, and the one refusal here that is a missing setting rather than a broken row.
            // Script 512 returns no from-date for as long as the watermark has never advanced, which is the
            // state script 340 seeds -- so this is the path every first run takes, and before
            // InitialLoadFromDate existed it was refused as "nothing to ask for" and the loader could not
            // perform an initial load at all.
            { RecommendedFromDate: null } when fromDate is null =>
                "the watermark has never advanced, so this is a full load -- and a full load needs a first "
                + $"day, which {LoadRunOptions.SectionName}:"
                + $"{nameof(LoadRunOptions.InitialLoadFromDate)} does not supply. There is deliberately no "
                + "default: it decides how far back the mirror reaches, and getting that wrong is invisible "
                + "-- the walk reports every window it asked for as walked, and it never asked. Set it to "
                + $"{LoadRunOptions.RecommendedInitialLoadFromDate} unless MDE has decided otherwise (G38).",

            // Its own arm rather than sharing the inverted-range one below, so the message names the setting
            // the operator can fix instead of blaming a watermark row that is correct.
            { RecommendedFromDate: null } when toDate < fromDate =>
                $"{LoadRunOptions.SectionName}:{nameof(LoadRunOptions.InitialLoadFromDate)} is "
                + $"{fromDate:yyyy-MM-dd}, which is after the end of the range script 512 recommends "
                + $"({toDate:yyyy-MM-dd}). A full load would ask for an inverted range, and EPA answers that "
                + "with 200 and an empty array -- which reads as a quiet week.",

            _ when toDate < fromDate =>
                "the watermark row recommends a range that ends before it starts. EPA answers an inverted "
                + "range with 200 and an empty array, which reads as a quiet week.",
            _ => null,
        };

        if (problem is not null)
        {
            LoadRunLog.NotConfigured(logger, HandlerSourceFeed, location, problem);

            return LoadRunResult.NotStarted(
                LoadRunOutcome.NotConfigured,
                $"No run was opened for {HandlerSourceFeed}/{location}: {problem}");
        }

        if (!watermark!.IsEnabled)
        {
            // Checked after the shape, so an operator who disabled a feed AND has a broken row hears about
            // the one they chose. No run row is opened: a Running row for a run that does nothing is
            // exactly the row that refuses the next one.
            LoadRunLog.FeedDisabled(logger, HandlerSourceFeed, location);

            return LoadRunResult.NotStarted(
                LoadRunOutcome.FeedDisabled,
                $"{HandlerSourceFeed}/{location} is disabled in config.LoadWatermark, so nothing was "
                + "fetched. Set IsEnabled to 1 through config.uspSetLoadWatermark to resume loading.");
        }

        return null;
    }

    /// <summary>
    /// The first day to ask for: what script 512 recommends, or the configured floor when it recommends
    /// nothing because the watermark has never advanced.
    /// </summary>
    /// <param name="watermark">What script 512 returned, if anything.</param>
    /// <returns>The from-date, or <see langword="null"/> when this is a full load and none is configured.</returns>
    /// <remarks>
    /// <b>The coalesce is one way round and it matters which.</b> Script 512's recommendation wins whenever
    /// it has one, so the floor cannot pull an established mirror backwards and cannot be used to re-load
    /// history — that is <c>config.uspSetLoadWatermark</c>'s job, and it is the object that records who
    /// rewound the bookmark and when. The floor applies only where there is nothing to override.
    /// </remarks>
    private DateOnly? FullLoadFromDate(LoadWatermark? watermark) =>
        watermark?.RecommendedFromDate ?? runOptions.InitialLoadFromDate;

    /// <summary>Moves the watermark, and treats a failure to move it as not a failure of the run.</summary>
    /// <param name="loadRunId">The run being credited with the move.</param>
    /// <param name="location">The activity location.</param>
    /// <param name="target">Where the bookmark is going — <c>ContiguouslyWalkedThrough</c>.</param>
    /// <param name="requestedToDate">Where the run asked to get to, for the shortfall in the log line.</param>
    /// <param name="walkReport">The walk, for the window counts a partial move should name.</param>
    /// <returns>The date the bookmark reached, or <see langword="null"/> if the move failed.</returns>
    private async Task<DateOnly?> AdvanceWatermarkAsync(
        int loadRunId,
        string location,
        DateOnly target,
        DateOnly requestedToDate,
        SummaryWalkReport walkReport)
    {
        try
        {
            await writer.AdvanceWatermarkAsync(
                HandlerSourceFeed, location, target, loadRunId, CancellationToken.None)
                .ConfigureAwait(false);

            // Two messages rather than one with a conditional clause, because the partial move is the one an
            // operator has to notice: the run reports PartiallySucceeded, the bookmark moved anyway, and both
            // of those are correct. A single Information line saying "advanced" would bury it.
            if (target < requestedToDate)
            {
                LoadRunLog.WatermarkAdvancedPartially(
                    logger,
                    HandlerSourceFeed,
                    location,
                    target,
                    requestedToDate,
                    loadRunId,
                    walkReport.UnwalkedCount,
                    walkReport.Windows.Count);
            }
            else
            {
                LoadRunLog.WatermarkAdvanced(logger, HandlerSourceFeed, location, target, loadRunId);
            }

            return target;
        }
        catch (SqlException error)
        {
            // Everything this run fetched is already committed; only the bookmark did not move. The next
            // run therefore re-asks for a range it already holds and merges it as Unchanged, which costs
            // requests and loses nothing -- so downgrading the run's status here would misreport a
            // recoverable miss as a data failure. Script 513's rewind refusal is the other way this lands.
            LoadRunLog.WatermarkMoveFailed(logger, loadRunId, error);

            return null;
        }
    }

    /// <summary>Flushes the journal, and reports rather than throws if the flush itself fails.</summary>
    private async Task<LoadJournalFlush> Flush(ILoadJournal journal, int loadRunId)
    {
        try
        {
            return await journal.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException error)
        {
            // The run must still be closed. A throw here would leave logs.LoadRun at 'Running' on top of
            // having lost the status rows, which is two failures where one is enough.
            LoadRunLog.RunCouldNotBeClosed(logger, loadRunId, runOptions.AbandonAfterMinutes, error);

            return LoadJournalFlush.Empty;
        }
    }

    /// <summary>Closes the run row, and reports rather than throws if that fails.</summary>
    private async Task CloseAsync(int loadRunId, LoadRunResult result)
    {
        string status = result.Outcome switch
        {
            LoadRunOutcome.Succeeded => Succeeded,
            LoadRunOutcome.PartiallySucceeded => PartiallySucceeded,

            // CK_logs_LoadRun_Status has no 'Cancelled', and adding one would be a CHECK change for a
            // distinction the run row does not need: 'Abandoned' means a later run found this one dead,
            // which is not what happened. So a cancelled run records whether any of it worked, and
            // LoadRunOutcome.Cancelled keeps the distinction where the exit code needs it.
            LoadRunOutcome.Cancelled => result.WroteData ? PartiallySucceeded : Failed,
            _ => Failed,
        };

        try
        {
            await writer.CompleteRunAsync(
                loadRunId, status, result.FailureMessage, result.Counters, CancellationToken.None)
                .ConfigureAwait(false);

            if (result.Outcome == LoadRunOutcome.Cancelled)
            {
                LoadRunLog.RunCancelled(logger, loadRunId);
            }

            // Formatted into a local first, because result.ToString () composes thirteen counters and
            // CA1873 is an error here. Guarded as well, so the run's one summary line costs nothing when
            // Information is switched off.
            if (logger.IsEnabled(LogLevel.Information))
            {
                string summary = result.ToString();

                LoadRunLog.RunClosed(logger, loadRunId, status, summary);
            }
        }
        catch (SqlException error)
        {
            // Critical, and not rethrown. The data this run wrote is committed and unaffected; what is lost
            // is the run's own counters, and the row stays at 'Running' until the next run sweeps it. An
            // exception escaping here would additionally deny the caller its exit code.
            LoadRunLog.RunCouldNotBeClosed(logger, loadRunId, runOptions.AbandonAfterMinutes, error);
        }
    }

    /// <summary>The verdict, from the five things that can be short of complete.</summary>
    /// <remarks>
    /// <b>Two of the five govern the run's verdict without governing the watermark</b>, and they are the two
    /// worth understanding, because "this run is not clean" and "this range cannot be given up" are different
    /// questions. <paramref name="reconcileReport"/> is one: a lineage whose <c>CurrentRecord</c> was not
    /// asserted makes the run <c>PartiallySucceeded</c> — there is something for an operator to look at — but
    /// the bookmark still moves, for the reason <see cref="ReconcileReport"/> gives.
    /// <b>[R41] <paramref name="lookupsComplete"/> is now the other</b>, and this is the only place it is still
    /// weighed. A code list EPA has changed but we did not read renders a label wrongly and cannot make a date
    /// unreachable, so it belongs to the verdict and not to the bookmark; the class remarks give the failure
    /// that taught it, which is that the one case the watermark term governed was a case retrying could never
    /// resolve.
    /// </remarks>
    private static LoadRunOutcome Outcome(
        bool cancelled,
        string? failureMessage,
        bool everyVersionAccountedFor,
        bool lookupsComplete,
        SummaryWalkReport? walkReport,
        ReconcileReport? reconcileReport)
    {
        if (cancelled)
        {
            return LoadRunOutcome.Cancelled;
        }

        if (walkReport is null)
        {
            // The walk never ran, so no handler data was even enumerated. Nothing partial about it.
            return LoadRunOutcome.Failed;
        }

        // Null means the stage did not run, which happens only when the walk named no version at all -- and a
        // run with nothing to reconcile is complete rather than incomplete.
        bool reconcileComplete = reconcileReport is null || reconcileReport.Complete;

        if (everyVersionAccountedFor && lookupsComplete && reconcileComplete && walkReport.UnwalkedCount == 0)
        {
            return LoadRunOutcome.Succeeded;
        }

        // PartiallySucceeded rather than Failed whenever the run got as far as walking, because it is the
        // status that says "look in logs.HandlerLoadStatus for which" -- and something always did land.
        return failureMessage is not null && walkReport.WalkedCount == 0
            ? LoadRunOutcome.Failed
            : LoadRunOutcome.PartiallySucceeded;
    }

    /// <summary>
    /// Names every reason the watermark is being held, for the one log message whose entire value to the
    /// reader is <em>why</em>.
    /// </summary>
    /// <remarks>
    /// The hold rule is a conjunction of independent conditions, so reporting one of them is reporting a
    /// coincidence. Runs 2621 and 2622 held because code lists did not refresh and were told
    /// "0 of 12 window(s) went uncovered" and "0 of 77 window(s) went uncovered" -- true, and about the wrong
    /// thing. Every failed condition is listed, not the first, because two can hold at once and the operator
    /// fixing one needs to know about the other before deciding the next run will advance.
    /// <para>
    /// <b>[R41] The code lists are no longer among those conditions</b>, so this method no longer names them
    /// and no longer takes the lookup report. That is the durable fix for what those two runs reported: the
    /// message was wrong because it omitted the cause, and the cause has been removed from the rule. A run
    /// held only by a stale code list now advances, and <see cref="LoadRunLog.LookupsIncomplete"/> is where
    /// the shortfall is read.
    /// </para>
    /// <para>
    /// Counts and run state only: no handler identifier, no URI, no exception message (AR8).
    /// </para>
    /// </remarks>
    private static string HoldReason(
        SummaryWalkReport walkReport,
        DateOnly? advanceTo,
        bool everyVersionAccountedFor,
        FetchTally tally,
        bool cancelled,
        string? failureMessage)
    {
        List<string> reasons = [];

        if (walkReport.UnwalkedCount > 0)
        {
            reasons.Add($"{walkReport.UnwalkedCount} of {walkReport.Windows.Count} window(s) went uncovered");
        }

        // MayContinue false with no uncovered window means the walk itself stopped fatally or was cancelled,
        // which is a distinct thing from a window that answered badly.
        if (advanceTo is null && walkReport.UnwalkedCount == 0)
        {
            reasons.Add("the summaries walk did not finish, so there is no covered range to bank");
        }

        if (!everyVersionAccountedFor && !cancelled && failureMessage is null)
        {
            reasons.Add($"{tally.Failed} version(s) failed and {tally.Unfetched} were never fetched");
        }

        if (cancelled)
        {
            reasons.Add("the run was cancelled");
        }

        if (failureMessage is not null)
        {
            reasons.Add("the run stopped on an unhandled error");
        }

        // Never an empty string: this method is only called on the hold path, so "no reason" would mean the
        // rule and this explanation of it have drifted apart, and saying so is more useful than saying
        // nothing.
        return reasons.Count == 0
            ? "the advance conditions were not met, and no single reason was identified -- which is itself a "
              + "defect worth reporting, because the hold rule and this message are supposed to agree"
            : string.Join("; ", reasons);
    }

    /// <summary>Assembles what <c>logs.uspCompleteLoadRun</c> records.</summary>
    private static LoadRunCounters Counters(
        LookupStageReport? lookupReport,
        FetchTally tally,
        long httpRequests,
        SummaryWalkReport? walkReport)
    {
        // Requests INCLUDING retries, from the pacer, which leases once per HTTP send -- retries included,
        // because the pacing handler is registered inside the resilience handler. Only the data client is
        // paced, so the auth call is not counted here and is not mistaken for a retry below.
        int requests = (int)Math.Min(httpRequests, int.MaxValue);

        // Logical calls this loader made: the lookup lists it attempted, the windows it attempted, and the
        // versions it fetched. The difference is the retry count. Derived rather than measured, because a
        // retry happens below this class and is invisible from here; clamped at zero because the derivation
        // is only as good as the three counts, and a negative retry count in logs.LoadRun would read as a
        // display bug rather than as an accounting one.
        int logical =
            (lookupReport?.Reports.Count(report => report.Status != LookupRefreshStatus.NotAttempted) ?? 0)
            + (walkReport?.Windows.Count(window => window.Status != SummaryWindowStatus.NotAttempted) ?? 0)
            + tally.Calls;

        return new LoadRunCounters
        {
            LookupListsRefreshed = lookupReport?.RefreshedCount ?? 0,
            SourceRecordsEnumerated = tally.Enumerated,
            SourceRecordsFetched = tally.Fetched,
            SourceRecordsInserted = tally.Merged.Inserted,
            SourceRecordsUpdated = tally.Merged.Updated,
            SourceRecordsUnchanged = tally.Merged.Unchanged,
            SourceRecordsSoftDeleted = tally.SoftDeleted,
            SourceRecordsSkipped = tally.Skipped,
            SourceRecordsFailed = tally.Failed,
            HttpRequestCount = requests,
            HttpRetryCount = Math.Max(0, requests - logical),
        };
    }

    private static HandlerLoadStatusElement ToStatusElement(HandlerSourceSummary summary) =>
        new()
        {
            HandlerId = summary.HandlerId!,

            // EPA's own value rather than the configured one. The walk already refuses a window whose
            // ANSWER names another state (OutOfScope), so this agrees with configuration by then -- and
            // substituting the configured value here would make that refusal unfalsifiable.
            ActivityLocation = summary.ActivityLocation,
            SourceType = summary.SourceType!,
            Sequence = summary.Sequence,
        };

    private static string LookupFailure(LookupStageReport report) =>
        report.WasCancelled
            ? "The lookup refresh stage was cancelled, so no handler data was fetched."
            : $"The lookup refresh stage stopped on {report.FatalOutcome}, so no handler data was fetched. "
              + "G15 refreshes the code lists first, which is why this was met once rather than once per "
              + "handler.";

    private static string WalkFailure(SummaryWalkReport report) =>
        report.WasCancelled
            ? $"The summaries walk was cancelled after covering {report.WalkedCount} of "
              + $"{report.Windows.Count} window(s)."
            : $"The summaries walk stopped on {report.FatalOutcome} after covering {report.WalkedCount} of "
              + $"{report.Windows.Count} window(s), so no version was fetched in this run.";

    /// <summary>
    /// This assembly's informational version, for <c>logs.LoadRun.ApplicationVersion</c>.
    /// </summary>
    /// <remarks>
    /// Recorded because the first question about a run that behaved unlike its neighbours is which build
    /// made it, and a scheduled task leaves no other trace of that.
    /// </remarks>
    private static string? ApplicationVersion() =>
        typeof(LoadRun).Assembly.GetName().Version?.ToString();

    /// <summary>
    /// The fetch loop's running counts. A mutable class rather than a returned record because the loop
    /// updates it from two methods and the alternative is eleven out parameters.
    /// </summary>
    private sealed class FetchTally
    {
        public int Enumerated { get; set; }

        public int Skipped { get; set; }

        public int Fetched { get; set; }

        public int SoftDeleted { get; set; }

        public int Failed { get; set; }

        public int Calls { get; set; }

        public int Unfetched { get; set; }

        public bool Cancelled { get; set; }

        public ApiFetchOutcome? FatalOutcome { get; set; }

        public MergeCounts Merged { get; set; }
    }
}
