using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// One handler version, by the natural key every log table and every procedure in this database agrees
/// on: <c>HandlerId</c>, <c>SourceType</c>, <c>Sequence</c>.
/// </summary>
/// <param name="HandlerId">The handler's EPA identifier. At most 12 characters.</param>
/// <param name="SourceType">The source-type code. Exactly one character.</param>
/// <param name="Sequence">EPA's version sequence.</param>
/// <remarks>
/// <para>
/// A record struct rather than three loose parameters, for one reason that is not tidiness: it gives
/// value equality, which is what lets <see cref="LoadJournal"/> hold its buffers keyed by version.
/// Script 520 <b>throws</b> when one payload names the same key twice — a set with two rows for one
/// handler would make the result depend on which the engine applied last — so the buffer has to be able
/// to recognise a repeat, and a tuple of three strings compared by hand is where that goes wrong.
/// </para>
/// <para>
/// It is the same key as <see cref="HandlerKeyElement"/>, which exists on the other side of the
/// assembly boundary as a JSON payload element. <see cref="ToKeyElement"/> converts, so the orchestrator
/// can hand a soft delete the versions it already has.
/// </para>
/// </remarks>
public readonly record struct HandlerVersion(string HandlerId, string SourceType, int Sequence)
{
    /// <summary>This version as script 522's payload element.</summary>
    /// <returns>The element to add to a soft-delete set.</returns>
    public HandlerKeyElement ToKeyElement() =>
        new()
        {
            HandlerId = HandlerId,
            SourceType = SourceType,
            Sequence = Sequence,
        };

    /// <summary>The key, for a message. Holds identifiers only, which AR8 permits in a log.</summary>
    public override string ToString() => $"{HandlerId}/{SourceType}/{Sequence}";
}
