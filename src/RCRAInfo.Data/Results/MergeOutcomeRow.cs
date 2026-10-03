using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
/// What the merge did to one element of a batch, as returned by
/// <c>dbo.uspMergeHandlerSourceBatch</c> (script 400).
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
/// no change tracking. This is how the loader learns per-record outcomes without asking again: the
/// procedure reports them from inside the transaction that committed them, which is the only place
/// they are knowable. <c>Outcome</c> is what the caller then feeds to
/// <c>logs.uspUpsertHandlerLoadStatusSet</c>.
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
public sealed class MergeOutcomeRow
{
    public int HandlerSourceId { get; init; }
    public string HandlerId { get; init; } = null!;
    public string SourceType { get; init; } = null!;
    public int Sequence { get; init; }
    public string Outcome { get; init; } = null!;
}
