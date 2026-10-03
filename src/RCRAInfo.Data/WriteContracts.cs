using RCRAInfo.Data.Results;

namespace RCRAInfo.Data;

/// <summary>
/// What <c>logs.uspStartLoadRun</c> needs to open a run. Script 510.
/// </summary>
/// <remarks>
/// A record for the same reason the read filters are records: twelve positional arguments, half of
/// them nullable and four of them <see cref="int"/>, is a call site that can be silently reordered.
/// </remarks>
public sealed record LoadRunRequest
{
    /// <summary>The run mode, as <c>logs.LoadRun</c>'s <c>CHECK</c> constraint defines it.</summary>
    /// <remarks>
    /// A string, not an enum. The permitted values live in one place — the constraint — and
    /// <c>build/check_closed_set_filters.py</c> holds the procedures against it. A C# enum would be a
    /// second list, and the two would disagree the first time a value was added on one side only.
    /// </remarks>
    public required string RunMode { get; init; }

    /// <summary>The state being loaded. <c>MD</c> is the only value in scope (G2).</summary>
    public required string ActivityLocation { get; init; }

    /// <summary>Start of the window this run was asked for, if it was given one.</summary>
    public DateOnly? RequestedFromDate { get; init; }

    /// <summary>End of the window this run was asked for, if it was given one.</summary>
    public DateOnly? RequestedToDate { get; init; }

    /// <summary>
    /// The watermark as it stood before this run, recorded so that a run which fails part-way can be
    /// reasoned about afterwards without inferring what the watermark used to be.
    /// </summary>
    public DateOnly? WatermarkBeforeDate { get; init; }

    /// <summary>
    /// How many days of overlap were subtracted from the watermark. Recorded rather than recomputed,
    /// because the configured value can change between runs.
    /// </summary>
    public int? OverlapDaysApplied { get; init; }

    /// <summary>The run this one resumes, if it is a resumption.</summary>
    public int? ResumedFromLoadRunId { get; init; }

    /// <summary>The machine the loader is running on.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>The loader's process identifier.</summary>
    /// <remarks>
    /// Together with <see cref="MachineName"/> this is what makes the abandoned-run check meaningful:
    /// a run left <c>Running</c> by a killed process is distinguishable from one still going.
    /// </remarks>
    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>The loader's assembly version.</summary>
    public string? ApplicationVersion { get; init; }

    /// <summary>
    /// After how many minutes a run still marked <c>Running</c> is treated as abandoned. Null takes the
    /// procedure's configured default.
    /// </summary>
    public int? AbandonAfterMinutes { get; init; }

    /// <summary>
    /// Permit a second run to start while one is live. <b>Leave this false.</b>
    /// </summary>
    /// <remarks>
    /// The refusal is not a convenience. Two concurrent runs writing the same handlers is what makes
    /// retry unsafe: <see cref="RCRAInfoDataOptions.MaxRetryCount"/> is only sound because each
    /// procedure is idempotent <i>and</i> only one run is in flight. The flag exists for a controlled
    /// backfill running against a disjoint window, and nothing enforces disjointness but the operator.
    /// <para>
    /// The one caller that sets it as a matter of course is the targeted single-handler run
    /// (<see cref="RunMode"/> <c>'Targeted'</c>), and the argument above does not apply to it: it writes the
    /// versions of one handler, walks no date window and cannot advance the watermark, so it neither doubles a
    /// population's requests nor competes for the bookmark. Script 510 exempts that mode from the refusal on
    /// both sides anyway; the flag is what an older deployment of the procedure would honour.
    /// </para>
    /// </remarks>
    public bool AllowConcurrent { get; init; }
}

/// <summary>
/// The counters <c>logs.uspCompleteLoadRun</c> records when a run closes. Script 511.
/// </summary>
/// <remarks>
/// All twelve are counts the loader kept, not values the database can derive: the database sees the
/// batches that arrived, not the ones that were enumerated and skipped, and it never sees an HTTP
/// request at all. Every one defaults to zero, so a run that did nothing of a given kind reports zero
/// rather than null — the monitoring app distinguishes "none" from "not recorded" by the run's status,
/// not by a null counter.
/// </remarks>
public sealed record LoadRunCounters
{
    /// <summary>How many of the 24 mirrored code lists were refreshed.</summary>
    public int LookupListsRefreshed { get; init; }

    /// <summary>How many handler versions the API listed.</summary>
    public int SourceRecordsEnumerated { get; init; }

    /// <summary>How many were retrieved in full.</summary>
    public int SourceRecordsFetched { get; init; }

    /// <summary>How many merged as new rows.</summary>
    public int SourceRecordsInserted { get; init; }

    /// <summary>How many merged over an existing row that differed.</summary>
    public int SourceRecordsUpdated { get; init; }

    /// <summary>How many matched an existing row with no change.</summary>
    public int SourceRecordsUnchanged { get; init; }

    /// <summary>How many were soft-deleted because EPA no longer publishes them.</summary>
    public int SourceRecordsSoftDeleted { get; init; }

    /// <summary>How many were deliberately not fetched.</summary>
    public int SourceRecordsSkipped { get; init; }

    /// <summary>How many failed. The reason for each is in <c>logs.HandlerLoadStatus</c>.</summary>
    public int SourceRecordsFailed { get; init; }

    /// <summary>How many HTTP requests the run made.</summary>
    public int HttpRequestCount { get; init; }

    /// <summary>How many of those were retries.</summary>
    public int HttpRetryCount { get; init; }
}

/// <summary>
/// What a payload-taking procedure reports back, and what may be said about the payload afterwards.
/// </summary>
/// <param name="Batch">
/// The element count and SHA-256 digest of the JSON that was sent.
/// </param>
/// <remarks>
/// The digest is here so that a caller can record <i>which</i> batch a call handled without recording
/// the batch. A count and a hex digest are the substitutes AR8 permits for a payload in a log row,
/// because neither can be turned back into a contact name. Returning them from the call rather than
/// leaving the caller to recompute them also means the logged digest is necessarily the digest of the
/// bytes that were actually sent.
/// </remarks>
public abstract record PayloadResult (PayloadBatch Batch);

/// <summary>What <c>dbo.uspMergeHandlerSourceBatch</c> did to each element. Script 400.</summary>
/// <param name="Outcomes">
/// One row per element, saying whether it was inserted, updated or unchanged. Element order is not
/// guaranteed — a set-based <c>MERGE</c> has no order — so rows are matched by natural key.
/// </param>
/// <param name="Batch">The count and digest of the payload sent.</param>
public sealed record MergeBatchResult (IReadOnlyList<MergeOutcomeRow> Outcomes, PayloadBatch Batch)
    : PayloadResult (Batch);

/// <summary>What <c>dbo.uspReconcileCurrentRecord</c> changed. Script 521.</summary>
/// <param name="RowsAffected">How many <c>CurrentRecord</c> flags were corrected.</param>
/// <param name="Observations">
/// How many disagreements were seen. Larger than <paramref name="RowsAffected"/> means some
/// disagreements were recorded without being acted on, which is the case worth investigating rather
/// than the case worth ignoring.
/// </param>
/// <param name="Batch">The count and digest of the payload sent.</param>
public sealed record ReconcileResult (int RowsAffected, int Observations, PayloadBatch Batch)
    : PayloadResult (Batch);

/// <summary>What <c>dbo.uspRefreshLookupSet</c> changed for one code list. Script 523.</summary>
/// <param name="RowsAffected">Codes inserted or updated.</param>
/// <param name="RetiredRows">
/// Codes soft-deleted because EPA no longer publishes them. Never removed — there is no hard delete
/// anywhere in this database, and a retired code must stay resolvable for the handler rows that still
/// reference it.
/// </param>
/// <param name="ChildRows">
/// Nested rows written. Non-zero only for <c>StateDistrict</c>, whose counties are
/// <c>dbo.LookupStateDistrictCounty</c>.
/// </param>
/// <param name="Batch">The count and digest of the payload sent.</param>
public sealed record LookupRefreshResult (
    int RowsAffected, int RetiredRows, int ChildRows, PayloadBatch Batch)
    : PayloadResult (Batch);

/// <summary>What <c>dbo.uspSoftDeleteHandlerSourceSet</c> marked deleted. Script 522.</summary>
/// <param name="RowsAffected">Handler versions marked <c>IsDeleted = 1</c>.</param>
/// <param name="ChildRows">
/// Descendant rows marked with them. The cascade is derived from <c>sys.foreign_keys</c>, so this
/// count moves when a child table is added — which is the point.
/// </param>
/// <param name="Batch">The count and digest of the payload sent.</param>
public sealed record SoftDeleteResult (int RowsAffected, int ChildRows, PayloadBatch Batch)
    : PayloadResult (Batch);

/// <summary>What <c>logs.uspUpsertHandlerLoadStatusSet</c> wrote. Script 520.</summary>
/// <param name="RowsAffected">Status rows inserted or updated.</param>
/// <param name="Batch">The count and digest of the payload sent.</param>
public sealed record UpsertStatusResult (int RowsAffected, PayloadBatch Batch)
    : PayloadResult (Batch);

/// <summary>What <c>logs.uspRecordHandlerLoadAttemptSet</c> wrote. Script 524.</summary>
/// <param name="RowsAffected">
/// Attempt rows appended. Fewer than the elements sent is normal, not suspicious: the table is
/// append-only on <c>(HandlerLoadStatusId, AttemptNumber)</c>, so a re-sent flush writes nothing.
/// </param>
/// <param name="RowsOrphaned">
/// Elements naming a handler version this run never enumerated. They were dropped and the rest were
/// written — the alternative was discarding a whole flush of good diagnostic rows over one bad element.
/// Non-zero means the loader recorded an attempt against a version it never told the database about,
/// which is a sequencing defect worth chasing.
/// </param>
/// <param name="ValuesWithheld">
/// How many individual column values the procedure replaced with a notice: a request path that was not
/// a bare path, a message naming the auth path, or a value wider than its column. The row was still
/// written, because the table's purpose is diagnosis at 03:00 and a withheld field beats a missing row.
/// <b>Non-zero is a loader defect</b> — the loader is meant to send a bare path and a composed message,
/// so the database should never have to withhold anything.
/// </param>
/// <param name="Batch">The count and digest of the payload sent.</param>
public sealed record AttemptRecordResult (
    int RowsAffected, int RowsOrphaned, int ValuesWithheld, PayloadBatch Batch)
    : PayloadResult (Batch);
