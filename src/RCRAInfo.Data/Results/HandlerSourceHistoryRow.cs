using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
/// One version of one handler, as projected by <c>dbo.uspGetHandlerSourceHistoryPage</c>
/// (script 504).
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
/// no change tracking. <c>CurrentRecord</c> is the distinguishing column: the history page shows
/// every version of a handler, of which at most one is current. Contact columns are excluded as
/// PII, and <c>SrcUpdatedBy</c> with them, because it identifies an EPA user.
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
/// always safe. That is also why <c>TotalRows</c> is <c>int?</c> here although two of the three paged shapes
/// report it non-nullable -- one uniform declaration that cannot throw beats three that mirror an
/// artifact of the optimizer.
/// </para>
/// </remarks>
[Keyless]
public sealed class HandlerSourceHistoryRow
{
    public int Ordinal { get; init; }
    public int HandlerSourceId { get; init; }
    public string HandlerId { get; init; } = null!;
    public string ActivityLocation { get; init; } = null!;
    public string SourceType { get; init; } = null!;
    public string? SourceTypeDescription { get; init; }
    public int Sequence { get; init; }
    public bool? CurrentRecord { get; init; }
    public DateOnly? ReceivedDate { get; init; }
    public string? HandlerName { get; init; }
    public string? SiteLocationAddress1 { get; init; }
    public string? SiteLocationCity { get; init; }
    public string? SiteLocationStateCode { get; init; }
    public string? SiteLocationZip { get; init; }
    public string? SiteLocationCountyDescription { get; init; }
    public string? WasteFederalGeneratorCategoryCode { get; init; }
    public string? WasteFederalGeneratorCategoryDescription { get; init; }
    public bool? WasteTsd { get; init; }
    public bool? WasteTransporter { get; init; }
    public bool? WasteRecyclerActivity { get; init; }
    public DateOnly? SrcUpdatedDate { get; init; }
    public DateTimeOffset AuditModifiedDateUtc { get; init; }
    public int? TotalRows { get; init; }
}
