namespace RCRAInfo.Data.Payloads;

/// <summary>
/// One element of <c>dbo.uspRefreshLookupSet</c>'s <c>@Elements</c> array (script 523): one code
/// from one of the 24 mirrored EPA code lists.
/// </summary>
/// <remarks>
/// <para>
/// One type serves all 23 response definitions, because the procedure takes one payload shape and
/// dispatches on <c>@LookupName</c>. Most lists carry only
/// <see cref="ActivityLocation"/>, <see cref="Code"/>, <see cref="Description"/> and
/// <see cref="Active"/>; the remaining properties belong to one or two lists each and are null for
/// the rest. That mirrors the procedure's own shred table, which is one wide table for all of them.
/// </para>
/// <para>
/// The booleans matter more than they look. Script 523's header calls this out as the trap the file
/// would have fallen into: <c>JSON_VALUE</c> returns the <i>text</i> of a JSON boolean, so
/// <c>true</c> arrives as the string <c>'true'</c>, and <c>TRY_CAST (N'true' AS BIT)</c> is
/// <c>NULL</c>. Written the obvious way, every <c>Active</c>, <c>IndustryApp</c>,
/// <c>BrLoadActive</c> and <c>Acute</c> value across all 24 lists would arrive null, silently, and
/// the mirror would look loaded. The procedure maps the four accepted spellings explicitly; a
/// <see cref="bool"/> on this side cannot produce a fifth.
/// </para>
/// </remarks>
public sealed class LookupElement
{
    /// <summary>
    /// The state this code belongs to, for the lists that are jurisdiction-scoped. Null for the
    /// national lists — which of the 23 have one is decided by the same <c>@LookupName</c> dispatch,
    /// and <c>build/check_lookup_coverage.py</c> holds that agreement against <c>sys.columns</c>.
    /// </summary>
    public string? ActivityLocation { get; init; }

    /// <summary>The code. Required; its width varies by list, from 1 to 10 characters.</summary>
    public string Code { get; init; } = null!;

    /// <summary>What the code means.</summary>
    public string? Description { get; init; }

    /// <summary>Whether EPA still publishes this code as active.</summary>
    public bool? Active { get; init; }

    /// <summary>Display order, where the list has one.</summary>
    public long? SortOrder { get; init; }

    /// <summary>Sub-classification carried by the lists that have one.</summary>
    public string? CodeType { get; init; }

    /// <summary>Whether the waste code is acute. Waste-code lists only.</summary>
    public bool? Acute { get; init; }

    /// <summary>Whether the code applies to industry. NAICS-shaped lists only.</summary>
    public bool? IndustryApp { get; init; }

    /// <summary>Whether the code is active for Biennial Report loading.</summary>
    public bool? BrLoadActive { get; init; }

    /// <summary>
    /// The episodic event type this project references. Shredded from the nested object rather than
    /// resolved against <c>dbo.LookupEpisodicType</c>, for the reason the generated table's header
    /// gives. <c>EpisodicProject</c> only.
    /// </summary>
    public LookupElement? EpisodicType { get; init; }

    /// <summary>
    /// The counties nested inside a state district. <c>StateDistrict</c> only, and the reason G15's
    /// 23 response definitions became 24 tables: this array is
    /// <c>dbo.LookupStateDistrictCounty</c>. Merged in the same call and the same transaction as its
    /// parent, and retired with it — a county left live under a district EPA no longer publishes
    /// would read as current.
    /// </summary>
    public IReadOnlyList<LookupElement>? Counties { get; init; }
}
