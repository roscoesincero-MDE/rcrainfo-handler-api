namespace RCRAInfo.Data.Payloads;

/// <summary>
/// One element of <c>dbo.uspReconcileCurrentRecord</c>'s <c>@Summaries</c> array (script 521): a
/// handler version and whether EPA calls it the current one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CurrentRecord"/> is a <see cref="bool"/> and not a nullable one, which is a deliberate
/// narrowing of what the procedure accepts. It maps the strings <c>true</c>, <c>false</c>, <c>1</c>
/// and <c>0</c> explicitly and refuses anything else <i>by name</i> — because
/// <c>TRY_CAST (N'true' AS BIT)</c> returns <c>NULL</c>, so a value read as "not current" by
/// accident would demote a version EPA calls current. A <see cref="bool"/> here cannot produce a
/// value outside that set.
/// </para>
/// </remarks>
public sealed class HandlerVersionElement
{
    /// <summary>The handler's EPA identifier. Required.</summary>
    public string HandlerId { get; init; } = null!;

    /// <summary>The source-type code. Required.</summary>
    public string SourceType { get; init; } = null!;

    /// <summary>EPA's version sequence. Required.</summary>
    public int Sequence { get; init; }

    /// <summary>Whether EPA reports this version as the current record.</summary>
    public bool CurrentRecord { get; init; }

    /// <summary>
    /// The date EPA received this submission. Optional — script 521 uses it only to break the
    /// ambiguity tie and falls back to <see cref="Sequence"/> when it is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nullable, and not validated at either end, unlike every other property here.</b> That is
    /// the deliberate opposite of <see cref="CurrentRecord"/>'s treatment: this field never decides
    /// <i>whether</i> a version is current, only <i>which</i> of two versions EPA has already called
    /// current wins, so an absent value costs the ordering it used to have rather than demoting
    /// anything. It also lets the procedure and this assembly deploy in either order.
    /// </para>
    /// <para>
    /// [R43] It exists because ordering by <see cref="Sequence"/> alone was wrong. Sequence is an
    /// insertion order, not a chronology, so where MDE submitted one form to EPA several times the
    /// highest sequence is the last duplicate rather than the newest record — <c>MDD985416569</c>,
    /// whose sequences 3, 5, 6 and 7 are four copies of one 2025-01-02 submission while sequence 4 is
    /// the real 2026-05-11 update EPA also flags current.
    /// </para>
    /// </remarks>
    public DateOnly? ReceivedDate { get; init; }
}
