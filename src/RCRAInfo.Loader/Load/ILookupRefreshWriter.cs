using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// The one database call the lookup refresh stage makes: <c>dbo.uspRefreshLookupSet</c>, script 523.
/// </summary>
/// <remarks>
/// <para>
/// A second narrow seam over <c>RCRAInfoContext</c>, for the reason <see cref="ILoadJournalWriter"/> gives —
/// the context is <c>sealed</c>, and the stage's decisions (what it refuses to send, what it does after a
/// failure, whether it stops) are testable only against a writer that can be observed. Kept separate from
/// the journal's writer rather than widened into it: the journal writes about the run, this writes reference
/// data, and one interface holding both would let a stage reach a procedure it has no business calling.
/// </para>
/// <para>
/// <b>One method, and it is the only method in the solution that can retire a code.</b> Script 523 in
/// <c>Full</c> mode retires every code absent from the payload, within the activity locations the payload
/// mentions. Nothing else in the loader does that, and nothing about the call reports it as unusual — the
/// retirement count comes back as a number beside the write count, which is why
/// <see cref="LookupWriteCounts.RetiredRows"/> is returned rather than discarded.
/// </para>
/// </remarks>
public interface ILookupRefreshWriter
{
    /// <summary>The most elements one call may carry.</summary>
    /// <remarks>
    /// The writer's limit and not the caller's, for the reason <see cref="ILoadJournalWriter.MaxElementsPerCall"/>
    /// gives. It matters more here than it looks: no <c>/lookup/hd</c> endpoint pages, so a list larger than
    /// this cannot be split into two <c>Full</c>-mode calls — the second would retire everything in the
    /// first. See <c>LookupRefresh</c>.
    /// </remarks>
    int MaxElementsPerCall { get; }

    /// <summary>Refreshes one mirrored code list. Script 523.</summary>
    /// <param name="loadRunId">The run this refresh belongs to.</param>
    /// <param name="lookupName">Script 523's <c>@LookupName</c> — <c>RcraInfoLookup.Name</c>.</param>
    /// <param name="mode">
    /// <c>Full</c> or <c>Upsert</c>. <c>Full</c> retires what the payload omits; <c>Upsert</c> retires
    /// nothing.
    /// </param>
    /// <param name="elements">The codes as EPA published them, unfiltered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the procedure wrote, retired and wrote to the child table.</returns>
    Task<LookupWriteCounts> RefreshAsync(
        int loadRunId,
        string lookupName,
        string mode,
        IReadOnlyCollection<LookupElement> elements,
        CancellationToken cancellationToken = default);
}

/// <summary>What script 523 reported for one refresh.</summary>
/// <param name="RowsAffected">Codes inserted or updated in the list's own table.</param>
/// <param name="RetiredRows">
/// Codes soft-deleted because the payload did not contain them. <b>Not a defect count and not a statistic —
/// the number to look at.</b> A handful across 23 lists is EPA retiring codes; a number close to the table's
/// size is this loader having sent a narrower payload than it meant to, and the run reports success either
/// way.
/// </param>
/// <param name="ChildRows">
/// Rows written to <c>dbo.LookupStateDistrictCounty</c>. Non-zero only for <c>StateDistrict</c>, which is
/// the one list whose payload nests a second table.
/// </param>
public readonly record struct LookupWriteCounts(int RowsAffected, int RetiredRows, int ChildRows);
