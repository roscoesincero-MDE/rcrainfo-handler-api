using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
/// One feed's load watermark and the run parameters it recommends, as returned by
/// <c>config.uspGetLoadWatermark</c> (script 512).
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
/// no change tracking. The four <c>Recommended*</c> columns are the procedure's answer to "what
/// should the next run ask for", derived from the watermark and the overlap. <c>Notes</c> is
/// operator free text and is excluded from that procedure's log parameters by name.
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
public sealed class LoadWatermark
{
    public int LoadWatermarkId { get; init; }
    public string FeedName { get; init; } = null!;
    public string ActivityLocation { get; init; } = null!;
    public DateOnly? WatermarkDate { get; init; }
    public int OverlapDays { get; init; }
    public bool IsEnabled { get; init; }
    public int? LastAdvancedByLoadRunId { get; init; }
    public DateTimeOffset? LastAdvancedDateUtc { get; init; }
    public string? Notes { get; init; }
    public string? RecommendedRunMode { get; init; }
    public DateOnly? RecommendedFromDate { get; init; }
    public DateOnly? RecommendedToDate { get; init; }
    public int? RecommendedOverlapDaysApplied { get; init; }
    public DateTimeOffset AuditModifiedDateUtc { get; init; }
}
