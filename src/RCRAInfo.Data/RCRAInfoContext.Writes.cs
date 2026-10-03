using Microsoft.Data.SqlClient;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

namespace RCRAInfo.Data;

/// <content>
/// The write path: nine procedures, six of which take a JSON payload.
/// </content>
/// <remarks>
/// <para>
/// Every payload is a single <c>NVARCHAR (MAX)</c> parameter carrying a JSON array (G32). There is no
/// table-valued parameter anywhere in this solution, no <c>SqlDbType.Structured</c>, no
/// <c>IEnumerable&lt;SqlDataRecord&gt;</c> and no user-defined table type in the script folder — a
/// guardrail fails the build if one appears. One shape for every set-based call, and the procedure
/// shreds it with <c>OPENJSON</c>.
/// </para>
/// <para>
/// Each of these methods serialises through <see cref="PayloadJson"/> and asserts the result is an
/// array before sending it. That is not redundant with the procedures' <c>ISJSON</c> guard: the
/// procedure's check is the backstop, and its refusal names no element, so a payload that fails there
/// tells the operator far less than one that fails here. The loader owns well-formedness.
/// </para>
/// <para>
/// The six payload calls and the two OUT-parameter batch calls run under
/// <see cref="RCRAInfoDataOptions.BatchCommandTimeoutSeconds"/>, not the read timeout. The 30-second
/// default that EF Core would otherwise use will not survive a real batch, and the failure presents as
/// a database fault rather than as the configuration problem it is.
/// </para>
/// </remarks>
public sealed partial class RCRAInfoContext
{
    /// <summary>Merges a batch of handler versions. Script 400.</summary>
    /// <param name="envelopes">
    /// The retrieved handlers, each wrapping EPA's own JSON object unchanged.
    /// </param>
    /// <param name="loadRunId">The run this batch belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened to each element, and the batch's count and digest.</returns>
    /// <remarks>
    /// <para>
    /// The one call in this class whose payload is not built from a hand-written shape.
    /// <see cref="HandlerEnvelope.Handler"/> is a <c>JsonElement</c> and is written through verbatim,
    /// so the bytes reaching <c>OPENJSON</c> are the bytes EPA sent. Script 400 shreds 212 paths out of
    /// them, generated from the same pinned swagger specification that generated
    /// <c>dbo.HandlerSource</c>; a hand-written mapping would be a second source of truth for the one
    /// mapping this project cannot afford to have two of, and a field this project has never heard of
    /// would be dropped instead of landing in its column.
    /// </para>
    /// <para>
    /// Unlike the other writes this one returns rows rather than output parameters, so it goes through
    /// <see cref="QueryAsync{T}"/>. That is checked rather than assumed —
    /// <c>build/check_result_shapes.py</c> fails if the set of procedures returning a result set
    /// changes.
    /// </para>
    /// </remarks>
    public async Task<MergeBatchResult> MergeHandlerSourceBatchAsync (
        IReadOnlyCollection<HandlerEnvelope> envelopes,
        int loadRunId,
        CancellationToken cancellationToken = default)
    {
        var batch = PayloadJson.Serialize (envelopes, this.options.MaxPayloadElements);
        PayloadJson.AssertJsonArray (batch.Json);

        SqlParameter[] parameters =
        [
            Payload ("Payload", batch.Json),
            Int ("LoadRunId", loadRunId),
        ];

        var outcomes = await this.QueryAsync<MergeOutcomeRow> (
            "dbo.uspMergeHandlerSourceBatch",
            parameters,
            this.options.BatchCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return new MergeBatchResult (outcomes, batch);
    }

    /// <summary>Corrects the <c>CurrentRecord</c> flag across a set of versions. Script 521.</summary>
    /// <param name="loadRunId">The run this reconciliation belongs to.</param>
    /// <param name="summaries">
    /// The versions as EPA's summary listing gave them, each carrying the flag EPA asserts.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many flags were corrected, how many disagreements were seen, and the digest.</returns>
    /// <remarks>
    /// <see cref="HandlerVersionElement.CurrentRecord"/> is a non-nullable <see cref="bool"/> on
    /// purpose. A missing or unparseable value read as "not current" would demote a version EPA calls
    /// current, and the row would look deliberate afterwards.
    /// </remarks>
    public async Task<ReconcileResult> ReconcileCurrentRecordAsync (
        int loadRunId,
        IReadOnlyCollection<HandlerVersionElement> summaries,
        CancellationToken cancellationToken = default)
    {
        var batch = PayloadJson.Serialize (summaries, this.options.MaxPayloadElements);
        PayloadJson.AssertJsonArray (batch.Json);

        var rowsAffected = OutInt ("RowsAffected");
        var observations = OutInt ("Observations");

        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Payload ("Summaries", batch.Json),
            rowsAffected,
            observations,
        ];

        await this.ExecuteAsync (
            "dbo.uspReconcileCurrentRecord",
            parameters,
            this.options.BatchCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return new ReconcileResult (ReadInt (rowsAffected), ReadInt (observations), batch);
    }

    /// <summary>Refreshes one of the 24 mirrored EPA code lists. Script 523.</summary>
    /// <param name="loadRunId">The run this refresh belongs to.</param>
    /// <param name="lookupName">
    /// Which list. Validated by the procedure against its own dispatch, which
    /// <c>build/check_lookup_coverage.py</c> holds against <c>sys.columns</c>; not validated here, for
    /// the reason <see cref="LoadRunRequest.RunMode"/> gives.
    /// </param>
    /// <param name="mode">How to treat codes absent from the payload.</param>
    /// <param name="elements">The codes as EPA published them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows written, rows retired, child rows written, and the digest.</returns>
    /// <remarks>
    /// The lookups must be refreshed <b>before</b> the handler data in every run (G15). Nothing in this
    /// method enforces that — ordering belongs to the console app's run sequencing — but a handler row
    /// merged against a code list that has not yet seen a new code is the failure this ordering exists
    /// to prevent.
    /// </remarks>
    public async Task<LookupRefreshResult> RefreshLookupSetAsync (
        int loadRunId,
        string lookupName,
        string mode,
        IReadOnlyCollection<LookupElement> elements,
        CancellationToken cancellationToken = default)
    {
        var batch = PayloadJson.Serialize (elements, this.options.MaxPayloadElements);
        PayloadJson.AssertJsonArray (batch.Json);

        var rowsAffected = OutInt ("RowsAffected");
        var retiredRows = OutInt ("RetiredRows");
        var childRows = OutInt ("ChildRows");

        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Text ("LookupName", lookupName, 50),
            Text ("Mode", mode, 20),
            Payload ("Elements", batch.Json),
            rowsAffected,
            retiredRows,
            childRows,
        ];

        await this.ExecuteAsync (
            "dbo.uspRefreshLookupSet",
            parameters,
            this.options.BatchCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return new LookupRefreshResult (
            ReadInt (rowsAffected), ReadInt (retiredRows), ReadInt (childRows), batch);
    }

    /// <summary>Soft-deletes a set of handler versions and their descendants. Script 522.</summary>
    /// <param name="loadRunId">The run this deletion belongs to.</param>
    /// <param name="keys">The natural keys of the versions to mark deleted.</param>
    /// <param name="reason">Why. Recorded on every row the call touches.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows marked, descendant rows marked, and the digest.</returns>
    /// <remarks>
    /// <para>
    /// Nothing is removed. This sets <c>IsDeleted = 1</c>, and the same call <b>revives</b>
    /// soft-deleted descendants of a version that EPA has published again — a child left deleted under
    /// a revived parent would read as absent from a record that is present.
    /// </para>
    /// <para>
    /// <paramref name="reason"/> <i>is</i> written to the log, which is exactly why it must carry
    /// nothing else: no handler name, no contact, no API detail. It is the operator's justification and
    /// the log row is the only place it can live.
    /// </para>
    /// </remarks>
    public async Task<SoftDeleteResult> SoftDeleteHandlerSourceSetAsync (
        int loadRunId,
        IReadOnlyCollection<HandlerKeyElement> keys,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var batch = PayloadJson.Serialize (keys, this.options.MaxPayloadElements);
        PayloadJson.AssertJsonArray (batch.Json);

        var rowsAffected = OutInt ("RowsAffected");
        var childRows = OutInt ("ChildRows");

        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Payload ("Elements", batch.Json),
            Text ("Reason", reason, 200),
            rowsAffected,
            childRows,
        ];

        await this.ExecuteAsync (
            "dbo.uspSoftDeleteHandlerSourceSet",
            parameters,
            this.options.BatchCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return new SoftDeleteResult (ReadInt (rowsAffected), ReadInt (childRows), batch);
    }

    /// <summary>Records per-handler load status for a batch. Script 520.</summary>
    /// <param name="loadRunId">The run these attempts belong to.</param>
    /// <param name="mode">Which of the procedure's four modes to apply.</param>
    /// <param name="elements">One element per handler attempt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many status rows were written, and the digest.</returns>
    /// <remarks>
    /// <para>
    /// The procedure <i>sets</i> <c>AttemptCount</c> from the payload rather than incrementing it, and
    /// that is what makes this call safe to retry. An incrementing counter would count the retry as an
    /// attempt EPA never saw, so a transient connection fault would inflate the very number an operator
    /// uses to decide whether EPA is failing.
    /// </para>
    /// <para>
    /// <c>@Elements</c> is excluded from the log by name, and so is <c>apiErrorMessage</c> within it.
    /// </para>
    /// </remarks>
    public async Task<UpsertStatusResult> UpsertHandlerLoadStatusSetAsync (
        int loadRunId,
        string mode,
        IReadOnlyCollection<HandlerLoadStatusElement> elements,
        CancellationToken cancellationToken = default)
    {
        var batch = PayloadJson.Serialize (elements, this.options.MaxPayloadElements);
        PayloadJson.AssertJsonArray (batch.Json);

        var rowsAffected = OutInt ("RowsAffected");

        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Text ("Mode", mode, 20),
            Payload ("Elements", batch.Json),
            rowsAffected,
        ];

        await this.ExecuteAsync (
            "logs.uspUpsertHandlerLoadStatusSet",
            parameters,
            this.options.BatchCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return new UpsertStatusResult (ReadInt (rowsAffected), batch);
    }

    /// <summary>Appends the per-request attempt log for a batch of calls. Script 524.</summary>
    /// <param name="loadRunId">The run these attempts belong to.</param>
    /// <param name="elements">One element per HTTP call made.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Rows appended, elements orphaned, values withheld, and the batch's count and digest.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Callers name the handler version, not the status row —
    /// <see cref="HandlerLoadAttemptElement"/> explains why. This is the finer-grained companion to
    /// <see cref="UpsertHandlerLoadStatusSetAsync"/>: that one keeps the current state of each handler,
    /// this one keeps every call that led there, including the ones that were retried away. The status
    /// row says a handler eventually succeeded; only these rows say it took four attempts and two 429s
    /// to get there.
    /// </para>
    /// <para>
    /// <b>Safe to retry, and safe to re-send.</b> The write is an <c>INSERT … WHERE NOT EXISTS</c> on the
    /// table's natural key, never an update, so a flush that committed and was then reported as failed
    /// writes nothing the second time instead of colliding with the unique index.
    /// </para>
    /// <para>
    /// <b>Two of the three counts returned are defect reports, not statistics.</b>
    /// <see cref="AttemptRecordResult.RowsOrphaned"/> and
    /// <see cref="AttemptRecordResult.ValuesWithheld"/> should both be zero in a correct run, and neither
    /// causes the call to fail — the procedure writes what it can and reports what it could not, because
    /// refusing a flush would lose the diagnostic rows for every other handler in the same buffer. The
    /// caller is expected to surface a non-zero count rather than discard it.
    /// </para>
    /// <para>
    /// <c>@Elements</c> is excluded from the log by name, and so are <c>requestPath</c>,
    /// <c>apiErrorMessage</c> and <c>failureMessage</c> within it.
    /// </para>
    /// </remarks>
    public async Task<AttemptRecordResult> RecordHandlerLoadAttemptSetAsync (
        int loadRunId,
        IReadOnlyCollection<HandlerLoadAttemptElement> elements,
        CancellationToken cancellationToken = default)
    {
        var batch = PayloadJson.Serialize (elements, this.options.MaxPayloadElements);
        PayloadJson.AssertJsonArray (batch.Json);

        var rowsAffected = OutInt ("RowsAffected");
        var rowsOrphaned = OutInt ("RowsOrphaned");
        var valuesWithheld = OutInt ("ValuesWithheld");

        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Payload ("Elements", batch.Json),
            rowsAffected,
            rowsOrphaned,
            valuesWithheld,
        ];

        await this.ExecuteAsync (
            "logs.uspRecordHandlerLoadAttemptSet",
            parameters,
            this.options.BatchCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return new AttemptRecordResult (
            ReadInt (rowsAffected), ReadInt (rowsOrphaned), ReadInt (valuesWithheld), batch);
    }

    /// <summary>Opens a load run. Script 510.</summary>
    /// <param name="request">What the run is and where it came from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new run's identifier.</returns>
    /// <remarks>
    /// Refuses if a run is already live, unless <see cref="LoadRunRequest.AllowConcurrent"/> says
    /// otherwise. The refusal arrives as a <see cref="SqlException"/> with number
    /// <see cref="SqlErrorNumbers.ProcedureRefusal"/> and is not retryable —
    /// <see cref="SqlErrorNumbers.IsRetryable"/> returns false for it, which matters because a retried
    /// start would either start the second run the refusal existed to prevent or spin until the first
    /// finished.
    /// </remarks>
    public async Task<int> StartLoadRunAsync (
        LoadRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (request);

        var loadRunId = OutInt ("LoadRunId");

        SqlParameter[] parameters =
        [
            Text ("RunMode", request.RunMode, 20),
            Text ("ActivityLocation", request.ActivityLocation, 2),
            Date ("RequestedFromDate", request.RequestedFromDate),
            Date ("RequestedToDate", request.RequestedToDate),
            Date ("WatermarkBeforeDate", request.WatermarkBeforeDate),
            Int ("OverlapDaysApplied", request.OverlapDaysApplied),
            Int ("ResumedFromLoadRunId", request.ResumedFromLoadRunId),
            Text ("MachineName", request.MachineName, 128),
            Int ("ProcessId", request.ProcessId),
            Text ("ApplicationVersion", request.ApplicationVersion, 50),
            Int ("AbandonAfterMinutes", request.AbandonAfterMinutes),
            Bit ("AllowConcurrent", request.AllowConcurrent),
            loadRunId,
        ];

        await this.ExecuteAsync (
            "logs.uspStartLoadRun",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return ReadInt (loadRunId);
    }

    /// <summary>Closes a load run and records its counters. Script 511.</summary>
    /// <param name="loadRunId">The run to close.</param>
    /// <param name="status">The terminal status.</param>
    /// <param name="failureMessage">Why it failed, if it did. Null for a successful run.</param>
    /// <param name="counters">What the run did.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// <b><paramref name="failureMessage"/> must never contain the API key or any credential.</b>
    /// Scripts 500 and 502 record that this column is returned to a web page. A
    /// <see cref="SqlException"/> message is safe to put here; an <c>HttpRequestException</c> message
    /// is not always, because a request URI can carry a query string, and a caller that passes
    /// <c>exception.ToString ()</c> from anywhere in the HTTP stack is one stack frame away from
    /// writing a header into a table the monitoring app displays. Pass a message this project composed.
    /// </para>
    /// <para>
    /// Called on the failure path as well as the success path, which is why it takes a status rather
    /// than inferring one: a run abandoned half-way still needs its counters recorded, and a run left
    /// <c>Running</c> forever is indistinguishable from one still going.
    /// </para>
    /// </remarks>
    public async Task CompleteLoadRunAsync (
        int loadRunId,
        string status,
        string? failureMessage,
        LoadRunCounters counters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (counters);

        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Text ("Status", status, 20),
            Text ("FailureMessage", failureMessage, 4000),
            Int ("LookupListsRefreshed", counters.LookupListsRefreshed),
            Int ("SourceRecordsEnumerated", counters.SourceRecordsEnumerated),
            Int ("SourceRecordsFetched", counters.SourceRecordsFetched),
            Int ("SourceRecordsInserted", counters.SourceRecordsInserted),
            Int ("SourceRecordsUpdated", counters.SourceRecordsUpdated),
            Int ("SourceRecordsUnchanged", counters.SourceRecordsUnchanged),
            Int ("SourceRecordsSoftDeleted", counters.SourceRecordsSoftDeleted),
            Int ("SourceRecordsSkipped", counters.SourceRecordsSkipped),
            Int ("SourceRecordsFailed", counters.SourceRecordsFailed),
            Int ("HttpRequestCount", counters.HttpRequestCount),
            Int ("HttpRetryCount", counters.HttpRetryCount),
        ];

        await this.ExecuteAsync (
            "logs.uspCompleteLoadRun",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);
    }

    /// <summary>Moves or clears a feed's incremental-load watermark. Script 513.</summary>
    /// <param name="feedName">The feed.</param>
    /// <param name="activityLocation">The state.</param>
    /// <param name="watermarkDate">
    /// The new watermark. Ignored when <paramref name="clearWatermark"/> is true.
    /// </param>
    /// <param name="loadRunId">The run that earned the move.</param>
    /// <param name="clearWatermark">
    /// Clear the watermark instead of setting it, forcing the next run to be a full load.
    /// </param>
    /// <param name="overlapDays">How many days the next run should re-fetch. Null leaves it as is.</param>
    /// <param name="isEnabled">Whether the feed is loaded at all. Null leaves it as is.</param>
    /// <param name="notes">Operator free text.</param>
    /// <param name="allowRewind">
    /// Permit moving the watermark backwards. False by default, and the refusal is the point: a
    /// watermark that silently moved back would re-fetch a window already loaded, which is harmless,
    /// but one that moved back by accident and was then moved forward again would skip the window
    /// between — which is not.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <paramref name="notes"/> is excluded from the log by name; only the derived flag saying whether
    /// notes were supplied is recorded. Operator free text is text of unknown provenance, and the log
    /// is readable by the monitoring web app.
    /// </remarks>
    public async Task SetLoadWatermarkAsync (
        string feedName,
        string activityLocation,
        DateOnly? watermarkDate,
        int loadRunId,
        bool clearWatermark = false,
        int? overlapDays = null,
        bool? isEnabled = null,
        string? notes = null,
        bool allowRewind = false,
        CancellationToken cancellationToken = default)
    {
        SqlParameter[] parameters =
        [
            Text ("FeedName", feedName, 50),
            Text ("ActivityLocation", activityLocation, 2),
            Date ("WatermarkDate", watermarkDate),
            Bit ("ClearWatermark", clearWatermark),
            Int ("LoadRunId", loadRunId),
            Int ("OverlapDays", overlapDays),
            Bit ("IsEnabled", isEnabled),
            Text ("Notes", notes, 4000),
            Bit ("AllowRewind", allowRewind),
        ];

        await this.ExecuteAsync (
            "config.uspSetLoadWatermark",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);
    }
}
