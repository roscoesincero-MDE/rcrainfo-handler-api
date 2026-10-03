namespace RCRAInfo.Data.Payloads;

/// <summary>
/// One element of <c>dbo.uspSoftDeleteHandlerSourceSet</c>'s <c>@Elements</c> array (script 522):
/// the natural key of one handler version to soft-delete.
/// </summary>
/// <remarks>
/// <para>
/// There is no hard delete anywhere in this database, so this names a row to mark
/// <c>IsDeleted = 1</c> — never one to remove. The procedure cascades that across every descendant
/// of <c>dbo.HandlerSource</c>, and <c>build/check_cascade_coverage.py</c> derives the descendant
/// list from <c>sys.foreign_keys</c> so a new child table cannot be left behind live under a deleted
/// parent.
/// </para>
/// <para>
/// The companion <c>@Reason</c> parameter is not part of this element and is a scalar on the call.
/// It <i>is</i> logged, on purpose — it is the operator's justification and the log row is the only
/// place it can live — which is exactly why it must carry nothing else.
/// </para>
/// </remarks>
public sealed class HandlerKeyElement
{
    /// <summary>The handler's EPA identifier. Required.</summary>
    public string HandlerId { get; init; } = null!;

    /// <summary>The source-type code. Required.</summary>
    public string SourceType { get; init; } = null!;

    /// <summary>EPA's version sequence. Required.</summary>
    public int Sequence { get; init; }
}
