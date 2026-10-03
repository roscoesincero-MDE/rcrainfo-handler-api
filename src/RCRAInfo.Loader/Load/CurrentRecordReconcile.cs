using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Data.Payloads;
using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Asks <c>/hd/sources/summaries?handlerId=…</c> for one handler's complete version list and hands it to
/// <c>dbo.uspReconcileCurrentRecord</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The class is organised around script 521's one hard contract, and every batching decision below follows
/// from it: <c>@Summaries</c> must be the COMPLETE list for every <c>(HandlerId, SourceType)</c> pair it
/// mentions.</b> The procedure's whole purpose is to demote versions EPA no longer calls current, so a version
/// of a mentioned pair that the list does not name is set to <c>CurrentRecord = 0</c> and recorded as
/// <c>VersionNotInSourceSummary</c>. That makes a partial list actively destructive rather than merely
/// incomplete, and it rules out three things a batching loop would otherwise do by default:
/// </para>
/// <list type="number">
/// <item>
/// <b>The walk's own summaries are never reused.</b> They are the versions EPA changed <i>in a date window</i>
/// — a delta, not a lineage. Feeding them here would demote nearly every version in the mirror.
/// </item>
/// <item>
/// <b>A handler's versions are never split across two calls.</b> The batcher fills up to the writer's element
/// limit and then closes the batch <i>before</i> adding the next handler, rather than at the limit.
/// </item>
/// <item>
/// <b>A handler with more versions than one call may carry is refused whole.</b> Not reachable today — the
/// deepest lineage observed holds 33 versions against a limit of 500 — and guarded anyway, because the failure
/// it would cause is silent flag corruption rather than an error.
/// </item>
/// </list>
/// <para>
/// <b>A failed handler does not stop the stage and does not hold the watermark</b>, which is the opposite of
/// the summaries walk's rule. See <see cref="ReconcileReport"/> for the argument: an unwalked date window is a
/// permanent, undiscoverable hole, whereas a stale <c>CurrentRecord</c> flag is one query away from being found
/// and one targeted run away from being fixed.
/// </para>
/// <para>
/// <b>There is no retry here</b>, for <see cref="SummaryWalk"/>'s reason: retries belong to the resilience
/// pipeline on the <c>rcrainfo-data</c> client, and a second loop here would multiply the attempt count by
/// EPA's own tolerance without anything in configuration saying so.
/// </para>
/// </remarks>
/// <param name="client">The data client. Classifies; does not throw for a failed call.</param>
/// <param name="writer">The writer, for script 521 and for its element limit.</param>
/// <param name="options">The run's scope — the activity location, and the request chunk size.</param>
/// <param name="throttle">
/// Read only for <see cref="RcraInfoThrottleOptions.MaxConcurrentRequests"/>, for
/// <see cref="LoadRun"/>'s reason: the rate is the pacer's, and parallelism here exists only so the configured
/// rate can actually be reached.
/// </param>
/// <param name="logger">Counts, statuses, durations and run identifiers only. No handler identifier (AR8).</param>
public sealed class CurrentRecordReconcile(
    IRcraInfoDataClient client,
    ILoadRunWriter writer,
    IOptions<LoadRunOptions> options,
    IOptions<RcraInfoThrottleOptions> throttle,
    ILogger<CurrentRecordReconcile> logger) : ICurrentRecordReconcile
{
    private readonly LoadRunOptions runOptions = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<ReconcileReport> ReconcileAsync(
        int loadRunId,
        IReadOnlyCollection<string> handlerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handlerIds);

        string location = RequireUsableOptions();

        // Ordinally de-duplicated and trimmed here rather than trusted from the caller. The scheduled run's
        // handler list comes from a walk of many windows and a handler that changed in three of them is one
        // lineage, not three -- and each duplicate would otherwise cost a request and submit the same list.
        string[] handlers =
        [
            .. handlerIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        if (handlers.Length == 0)
        {
            return ReconcileReport.Nothing;
        }

        CurrentRecordReconcileLog.ReconcileStarting(logger, loadRunId, handlers.Length, handlers.Length);

        List<HandlerReconcileReport> reports = new(handlers.Length);
        Batcher batcher = new(writer.MaxElementsPerCall);

        int chunkSize = Math.Max(1, Math.Min(runOptions.FetchBatchSize, handlers.Length));
        int degree = Math.Max(1, throttle.Value.MaxConcurrentRequests);

        ApiFetchOutcome? fatal = null;
        bool cancelled = false;
        int calls = 0;

        for (int offset = 0; offset < handlers.Length; offset += chunkSize)
        {
            string[] chunk = handlers[offset..Math.Min(offset + chunkSize, handlers.Length)];

            if (fatal is not null || cancelled)
            {
                // Reported rather than omitted, for SummaryWalk's reason: "we never asked" and "we asked and
                // the lineage agreed" are the two answers that must never be confused.
                reports.AddRange(chunk.Select(HandlerReconcileReport.NotReached));

                continue;
            }

            ApiFetchResult[] results = new ApiFetchResult[chunk.Length];

            // No CancellationToken on ParallelOptions, for FetchAsync's reason: it would throw before
            // scheduling and discard results already paid for. The token goes to the fetch, which reports.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunk.Length),
                new ParallelOptions { MaxDegreeOfParallelism = degree },
                async (index, _) =>
                {
                    results[index] = await client.FetchAsync(
                        RcraInfoDataRequest.SummariesForHandler(chunk[index]),
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

            for (int index = 0; index < chunk.Length; index++)
            {
                ApiFetchResult result = results[index];

                // Counted per logical call, because LoadRun.Counters derives the retry count by subtracting
                // logical calls from the pacer's reservations -- an uncounted call reads as a retry.
                calls++;

                HandlerReconcileReport report = Read(
                    loadRunId, chunk[index], result, location, out HandlerVersionElement[] elements);

                reports.Add(report);

                if (report.IsReconciled)
                {
                    await batcher
                        .AddAsync(loadRunId, elements, writer, logger, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (result.Outcome == ApiFetchOutcome.Cancelled)
                {
                    cancelled = true;
                }
                else if (result.Outcome.IsFatalToTheRun())
                {
                    fatal ??= result.Outcome;
                }
            }

            if (fatal is { } outcome)
            {
                CurrentRecordReconcileLog.ReconcileStoppedFatally(
                    logger,
                    loadRunId,
                    outcome,
                    reports.Count(r => r.IsReconciled),
                    handlers.Length - reports.Count);
            }
        }

        // The last partial batch. Not conditional on the stage having finished cleanly: the lineages already
        // read are complete lists and asserting them is real progress, exactly as a failed window does not
        // discard the windows that landed.
        await batcher.FlushAsync(loadRunId, writer, logger, CancellationToken.None).ConfigureAwait(false);

        return Finish(loadRunId, reports, batcher, calls, fatal, cancelled);
    }

    /// <inheritdoc />
    public async Task<ReconcileReport> ReconcileKnownAsync(
        int loadRunId,
        string handlerId,
        IReadOnlyList<HandlerSourceSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        ArgumentNullException.ThrowIfNull(summaries);

        string location = RequireUsableOptions();
        string id = handlerId.Trim();

        CurrentRecordReconcileLog.ReconcileStarting(logger, loadRunId, 1, 0);

        HandlerReconcileReport report = Classify(
            loadRunId, id, summaries, location, null, null, 0, out HandlerVersionElement[] elements);

        Batcher batcher = new(writer.MaxElementsPerCall);

        if (report.IsReconciled)
        {
            await batcher
                .AddAsync(loadRunId, elements, writer, logger, cancellationToken)
                .ConfigureAwait(false);

            await batcher.FlushAsync(loadRunId, writer, logger, cancellationToken).ConfigureAwait(false);
        }

        // Calls = 0, and it is the whole point of this overload: the caller already spent the request that
        // produced this list, and counting it twice would show up in logs.LoadRun as a retry.
        return Finish(loadRunId, [report], batcher, 0, null, false);
    }

    private ReconcileReport Finish(
        int loadRunId,
        IReadOnlyList<HandlerReconcileReport> reports,
        Batcher batcher,
        int calls,
        ApiFetchOutcome? fatal,
        bool cancelled)
    {
        ReconcileReport report = new(reports, batcher.Counts, calls, batcher.WriteCalls, fatal, cancelled);

        CurrentRecordReconcileLog.ReconcileFinished(
            logger,
            loadRunId,
            report.ReconciledCount,
            report.ConsideredCount,
            report.WriteCalls,
            report.Counts.RowsAffected,
            report.Counts.Observations,
            report.Complete);

        return report;
    }

    /// <summary>Turns one summaries answer into a handler's verdict.</summary>
    /// <remarks>
    /// <b><c>NotFound</c> is the line to read twice, and it is the third distinct meaning this project gives
    /// that one status.</b> On <c>/hd/sources/{handlerId}/{sourceType}/{sequence}</c> it is AR7's soft-delete
    /// signal; on the dated summaries form it is an empty window; here it means EPA holds no summaries for a
    /// handler whose version this run has just merged. That is a contradiction worth surfacing and it is
    /// <i>not</i> a deletion — nothing is soft deleted, because the AR7 signal is a <c>404</c> for a version
    /// this loader named and this call names a handler.
    /// </remarks>
    private HandlerReconcileReport Read(
        int loadRunId,
        string handlerId,
        ApiFetchResult result,
        string location,
        out HandlerVersionElement[] elements)
    {
        elements = [];

        if (result.Outcome == ApiFetchOutcome.NotFound)
        {
            return NotReconciled(
                loadRunId,
                handlerId,
                HandlerReconcileStatus.NoVersions,
                result,
                "EPA answered 404 for the handler's summaries, so it named no version list to assert the "
                + "lineage against. NOTHING WAS SOFT DELETED: the AR7 soft delete is a 404 for a version this "
                + "loader named, and this call names a handler.");
        }

        if (result.Outcome != ApiFetchOutcome.Succeeded)
        {
            return NotReconciled(
                loadRunId,
                handlerId,
                HandlerReconcileStatus.FetchFailed,
                result,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the call came back {0} with no usable body (HTTP {1}).",
                    result.Outcome,
                    result.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none"));
        }

        SummaryPayloadRead read = SummaryPayload.Read(result.Payload);

        if (!read.IsReadable)
        {
            return NotReconciled(
                loadRunId, handlerId, HandlerReconcileStatus.PayloadRejected, result, read.Problem!);
        }

        return Classify(
            loadRunId,
            handlerId,
            read.Summaries,
            location,
            result.Outcome,
            result.HttpStatusCode,
            result.DurationMs,
            out elements);
    }

    /// <summary>
    /// The four checks a readable list must pass before it may be submitted, and the projection if it does.
    /// </summary>
    private HandlerReconcileReport Classify(
        int loadRunId,
        string handlerId,
        IReadOnlyList<HandlerSourceSummary> summaries,
        string location,
        ApiFetchOutcome? outcome,
        int? httpStatusCode,
        int durationMs,
        out HandlerVersionElement[] elements)
    {
        elements = [];

        if (SummaryWalk.FindOutOfScope(summaries, location) is string outOfScope)
        {
            // The same method the walk uses and the same refusal-whole reasoning. It runs even when the
            // caller has already run it (ReconcileKnownAsync), because a contract note is not a check.
            return NotReconciled(
                loadRunId, handlerId, HandlerReconcileStatus.OutOfScope, outcome, httpStatusCode, durationMs,
                outOfScope);
        }

        if (FindForeignHandler(summaries, handlerId) is string foreign)
        {
            return NotReconciled(
                loadRunId, handlerId, HandlerReconcileStatus.ForeignHandler, outcome, httpStatusCode,
                durationMs, foreign);
        }

        if (summaries.Count == 0)
        {
            return NotReconciled(
                loadRunId, handlerId, HandlerReconcileStatus.NoVersions, outcome, httpStatusCode, durationMs,
                "EPA returned a readable, in-scope answer naming no versions at all, so there is no list to "
                + "assert the lineage against. An empty @Summaries mentions no (HandlerId, SourceType) pair "
                + "and script 521 documents it as a no-op, so submitting it would report success for a "
                + "lineage nothing examined.");
        }

        if (summaries.Count > writer.MaxElementsPerCall)
        {
            CurrentRecordReconcileLog.HandlerTooManyVersions(
                logger, loadRunId, summaries.Count, writer.MaxElementsPerCall);

            return new HandlerReconcileReport(
                handlerId,
                HandlerReconcileStatus.TooManyVersions,
                summaries.Count,
                summaries.Count(summary => summary.CurrentRecord),
                outcome,
                httpStatusCode,
                durationMs,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the handler holds {0} version(s) and one dbo.uspReconcileCurrentRecord call may carry "
                    + "{1}, so the lineage was refused whole rather than split across two calls. Splitting is "
                    + "not a smaller assertion: script 521 demotes every live version of a mentioned pair "
                    + "that the list does not name.",
                    summaries.Count,
                    writer.MaxElementsPerCall));
        }

        elements =
        [
            .. summaries.Select(summary => new HandlerVersionElement
            {
                // EPA's own values rather than the requested identifier. FindForeignHandler above has already
                // established that they agree, and substituting the requested one here would make that
                // refusal unfalsifiable -- the same reasoning ToStatusElement applies to activityLocation.
                HandlerId = summary.HandlerId!,
                SourceType = summary.SourceType!,
                Sequence = summary.Sequence,
                CurrentRecord = summary.CurrentRecord,

                // [R43] Passed through unvalidated and possibly null, which HandlerVersionElement
                // explains: script 521 uses it only to order two versions EPA has BOTH called current,
                // and falls back to sequence without it. Sequence alone chose the last of MDE's
                // duplicate submissions over the real update on MDD985416569.
                ReceivedDate = summary.ReceivedDate,
            }),
        ];

        int currentCount = summaries.Count(summary => summary.CurrentRecord);

        CurrentRecordReconcileLog.HandlerRead(
            logger, loadRunId, summaries.Count, currentCount, durationMs);

        return new HandlerReconcileReport(
            handlerId,
            HandlerReconcileStatus.Reconciled,
            summaries.Count,
            currentCount,
            outcome,
            httpStatusCode,
            durationMs,
            null);
    }

    /// <summary>
    /// The subject check: every version in the answer must belong to the handler that was asked about.
    /// </summary>
    /// <remarks>
    /// <b>A different guard from the activity-location one, protecting against a different loss.</b>
    /// <see cref="SummaryWalk.FindOutOfScope"/> stops another state's regulated entities being mirrored. This
    /// stops a <i>third</i> handler's lineage being asserted from a list that was never its complete one —
    /// which would demote every version of it that the answer happened not to include. Both refuse the answer
    /// whole, and for the same underlying reason: a response that did not honour the parameter it was given
    /// cannot be trusted to have honoured the rest.
    /// </remarks>
    private static string? FindForeignHandler(IReadOnlyList<HandlerSourceSummary> summaries, string handlerId)
    {
        for (int index = 0; index < summaries.Count; index++)
        {
            string? id = summaries[index].HandlerId?.Trim();

            if (!string.IsNullOrEmpty(id)
                && !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase))
            {
                // Neither identifier is reproduced. Both are permitted in a report and neither is needed to
                // act on this: the fact is that the answer named more than one handler, and the report row
                // already carries the one that was asked about.
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "element {0} of {1} names a different handler than the one the request asked about, so "
                    + "the answer was refused whole. Asserting a lineage from a list that was never its "
                    + "complete one would demote every version of it the list omits (script 521).",
                    index,
                    summaries.Count);
            }
        }

        return null;
    }

    private HandlerReconcileReport NotReconciled(
        int loadRunId,
        string handlerId,
        HandlerReconcileStatus status,
        ApiFetchResult result,
        string problem) =>
        NotReconciled(
            loadRunId, handlerId, status, result.Outcome, result.HttpStatusCode, result.DurationMs, problem);

    private HandlerReconcileReport NotReconciled(
        int loadRunId,
        string handlerId,
        HandlerReconcileStatus status,
        ApiFetchOutcome? outcome,
        int? httpStatusCode,
        int durationMs,
        string problem)
    {
        CurrentRecordReconcileLog.HandlerNotReconciled(logger, loadRunId, status, problem);

        return new HandlerReconcileReport(
            handlerId, status, 0, 0, outcome, httpStatusCode, durationMs, problem);
    }

    private string RequireUsableOptions()
    {
        IReadOnlyList<string> problems = runOptions.Validate();

        if (problems.Count > 0)
        {
            // Thrown rather than reported, for SummaryWalk's reason: unusable configuration is not an answer
            // from EPA. Here it is sharper still -- the activity location is the ONLY scope this endpoint's
            // handlerId form has, because the request cannot carry the filter at all.
            throw new InvalidOperationException(
                "The RCRAInfoLoad configuration is not usable, so no CurrentRecord reconciliation was "
                + "attempted: " + string.Join(" ", problems));
        }

        return runOptions.NormalizedActivityLocation();
    }

    /// <summary>
    /// Accumulates whole lineages up to the writer's element limit and never splits one across two calls.
    /// </summary>
    /// <remarks>
    /// <b>The batch closes before the element that would overflow it, not at the limit.</b> That one line is
    /// the difference between this stage working and it corrupting flags: script 521 sets every live version of
    /// a mentioned <c>(HandlerId, SourceType)</c> pair that the list does not name to <c>CurrentRecord = 0</c>,
    /// so a lineage arriving half in one call and half in the next would have each half demote the other.
    /// </remarks>
    /// <param name="limit">The writer's element limit.</param>
    private sealed class Batcher(int limit)
    {
        private readonly List<HandlerVersionElement> pending = [];
        private readonly int limit = Math.Max(1, limit);
        private int pendingHandlers;

        /// <summary>What every call to the procedure reported, summed.</summary>
        public ReconcileCounts Counts { get; private set; } = ReconcileCounts.Empty;

        /// <summary>How many calls the batching produced.</summary>
        public int WriteCalls { get; private set; }

        /// <summary>Adds one complete lineage, writing first if it would not fit.</summary>
        public async Task AddAsync(
            int loadRunId,
            HandlerVersionElement[] elements,
            ILoadRunWriter writer,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (elements.Length == 0)
            {
                return;
            }

            if (pending.Count > 0 && pending.Count + elements.Length > limit)
            {
                await FlushAsync(loadRunId, writer, logger, cancellationToken).ConfigureAwait(false);
            }

            pending.AddRange(elements);
            pendingHandlers++;
        }

        /// <summary>Writes whatever is accumulated, if anything.</summary>
        public async Task FlushAsync(
            int loadRunId,
            ILoadRunWriter writer,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (pending.Count == 0)
            {
                return;
            }

            HandlerVersionElement[] batch = [.. pending];
            int handlers = pendingHandlers;

            pending.Clear();
            pendingHandlers = 0;
            WriteCalls++;

            ReconcileCounts counts = await writer
                .ReconcileAsync(loadRunId, batch, cancellationToken)
                .ConfigureAwait(false);

            Counts = Counts.Add(counts);

            CurrentRecordReconcileLog.BatchReconciled(
                logger,
                loadRunId,
                WriteCalls,
                handlers,
                batch.Length,
                counts.RowsAffected,
                counts.Observations);
        }
    }
}
