using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
/// One handler version the previous unfinished run of this activity location already dealt with, as
/// projected by <c>logs.uspGetHandlerLoadResumeSet</c> (script 525).
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
/// no change tracking. The property list mirrors the procedure's projection exactly — name, order and
/// type — and <c>build/check_result_shapes.py</c> re-derives it from the deployed procedure on every
/// guardrail run, because the projection and this type drift in one direction and drift silently: a
/// column this type does not name is simply not materialised and nothing fails.
/// </para>
/// <para>
/// <b>The six <c>ResumedFrom*</c> properties are the same values on every row.</b> They describe the
/// run these versions came from, not the version, and are repeated per row in the same style as
/// <c>TotalRows</c> on the paged shapes — one round trip carries both the plan and the set, and the
/// alternative (a second result set, or OUTPUT parameters beside the rows) is worse in a specific way
/// documented in <c>build/check_result_shapes.py</c>: outputs are not populated until the rows have
/// been consumed.
/// </para>
/// <para>
/// <b><see cref="Status"/> is the only property the fetch decision reads, and only one of its values
/// means "skip".</b> <c>Succeeded</c> means the previous run finished this version and the resuming
/// run must not fetch it. Every other value — <c>Pending</c>, <c>InProgress</c>, <c>Failed</c>,
/// <c>Skipped</c> — means the version was enumerated and never finished, and is returned for a
/// different reason: its <i>absence</i> from the resuming run's summaries walk is the G25 signal, and
/// nothing else in the system holds both sets. See the procedure's header.
/// </para>
/// <para>
/// Nullability follows the engine's answer, not intent. The two window dates are nullable because
/// <c>logs.LoadRun.RequestedFromDate</c> and <c>RequestedToDate</c> are — a full initial load
/// requests no window at all.
/// </para>
/// </remarks>
[Keyless]
public sealed class HandlerLoadResumeRow
{
    public int ResumedFromLoadRunId { get; init; }
    public string ResumedFromRunMode { get; init; } = null!;
    public string ResumedFromStatus { get; init; } = null!;
    public DateTimeOffset ResumedFromStartedDateUtc { get; init; }
    public DateOnly? ResumedFromRequestedFromDate { get; init; }
    public DateOnly? ResumedFromRequestedToDate { get; init; }
    public string HandlerId { get; init; } = null!;
    public string ActivityLocation { get; init; } = null!;
    public string SourceType { get; init; } = null!;
    public int Sequence { get; init; }
    public string Status { get; init; } = null!;
    public int AttemptCount { get; init; }
}
