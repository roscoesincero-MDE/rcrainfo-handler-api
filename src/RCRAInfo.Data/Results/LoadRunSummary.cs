using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
/// The single-row rollup of one load run, as returned by <c>logs.uspGetLoadRunSummary</c>
/// (script 502).
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
/// no change tracking. <c>CounterDriftDetected</c> and <c>ElapsedIsProvisional</c> are the two
/// columns worth knowing about: the first reports that the run's own counters disagree with the
/// status rows counted underneath them, and the second that the run has not finished, so the
/// elapsed time is a reading rather than a total.
/// </para>
/// <para>
/// The property list mirrors the procedure's projection exactly -- name, order and type. It was
/// emitted from <c>sys.dm_exec_describe_first_result_set</c> rather than typed, and
/// <c>build/check_result_shapes.py</c> re-derives it from the deployed procedure on every guardrail
/// run: the projection and this type drift in one direction and drift silently, because a column
/// this type does not name is simply not materialised and nothing fails.
/// </para>
/// <para>
/// Nullability follows the engine's answer, not intent. Where the projection reports a column
/// nullable it is nullable here, even where the procedure cannot in fact produce a null, because
/// declaring a nullable column non-nullable is the direction that throws at runtime. The reverse is
/// always safe.
/// </para>
/// </remarks>
[Keyless]
public sealed class LoadRunSummary
{
    public int LoadRunId { get; init; }
    public string RunMode { get; init; } = null!;
    public string ActivityLocation { get; init; } = null!;
    public DateOnly? RequestedFromDate { get; init; }
    public DateOnly? RequestedToDate { get; init; }
    public DateOnly? WatermarkBeforeDate { get; init; }
    public DateOnly? WatermarkAfterDate { get; init; }
    public int? OverlapDaysApplied { get; init; }
    public string Status { get; init; } = null!;
    public DateTimeOffset StartedDateUtc { get; init; }
    public DateTimeOffset? CompletedDateUtc { get; init; }
    public long? ElapsedMs { get; init; }
    public int ElapsedIsProvisional { get; init; }
    public int? ResumedFromLoadRunId { get; init; }
    public int LookupListsRefreshed { get; init; }
    public int SourceRecordsEnumerated { get; init; }
    public int SourceRecordsFetched { get; init; }
    public int SourceRecordsInserted { get; init; }
    public int SourceRecordsUpdated { get; init; }
    public int SourceRecordsUnchanged { get; init; }
    public int SourceRecordsSoftDeleted { get; init; }
    public int SourceRecordsSkipped { get; init; }
    public int SourceRecordsFailed { get; init; }
    public int HttpRequestCount { get; init; }
    public int HttpRetryCount { get; init; }
    public string? FailureMessage { get; init; }
    public string InvokedBy { get; init; } = null!;
    public string? MachineName { get; init; }
    public int? ProcessId { get; init; }
    public string? ApplicationVersion { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset AuditModifiedDateUtc { get; init; }
    public int? StatusRowCount { get; init; }
    public int? StatusRowsSoftDeleted { get; init; }
    public int? StatusPending { get; init; }
    public int? StatusInProgress { get; init; }
    public int? StatusSucceeded { get; init; }
    public int? StatusFailed { get; init; }
    public int? StatusSkipped { get; init; }
    public int? OutcomeInserted { get; init; }
    public int? OutcomeUpdated { get; init; }
    public int? OutcomeUnchanged { get; init; }
    public int? OutcomeSoftDeleted { get; init; }
    public int? OutcomeNotSet { get; init; }
    public int? DistinctHandlerCount { get; init; }
    public long? AttemptCountTotal { get; init; }
    public int? OurFailureCount { get; init; }
    public int? DurationMeasuredCount { get; init; }
    public long? TotalDurationMs { get; init; }
    public int? MaxDurationMs { get; init; }
    public DateTimeOffset? FirstSeenMinDateUtc { get; init; }
    public DateTimeOffset? LastCompletedDateUtc { get; init; }
    public int? AttemptRowCount { get; init; }
    public int? AttemptRowsSoftDeleted { get; init; }
    public int? AttemptSucceeded { get; init; }
    public int? AttemptFailed { get; init; }
    public int? AttemptThrottled { get; init; }
    public int? AttemptTimedOut { get; init; }
    public int? AttemptCancelled { get; init; }
    public int? MaxAttemptNumber { get; init; }
    public long? ResponseBytesTotal { get; init; }
    public int? Http4xxCount { get; init; }
    public int? Http5xxCount { get; init; }
    public int? MaxRetryAfterSeconds { get; init; }
    public int? ObservationRowCount { get; init; }
    public int? ObservationRowsSoftDeleted { get; init; }
    public int? ObservationErrorCount { get; init; }
    public int? ObservationWarningCount { get; init; }
    public int? ObservationInfoCount { get; init; }
    public int? ObservationTypeCount { get; init; }
    public int? CounterDriftDetected { get; init; }
}
