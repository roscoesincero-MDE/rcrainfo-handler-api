using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// How much of one handler's history a targeted run asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two values, because the two answers have different costs and neither is a safe default for the
/// other.</b> A handler with thirty years of notifications has thirty-odd versions, and
/// <see cref="EveryVersion"/> fetches all of them — right when the question is "what does EPA hold for this
/// site", wasteful when the question is "is our copy of the current record up to date". The scope is
/// therefore always stated by the operator, and the loader's switch has no implicit value.
/// </para>
/// <para>
/// <b>Neither value is a date range, and that is what keeps a targeted run out of the watermark's
/// business.</b> See <see cref="ILoadRun.RunTargetedAsync"/>: the run asks
/// <c>/hd/sources/summaries?handlerId=…</c>, which is the endpoint's second documented form and takes no
/// dates at all, so there is no window it could claim to have covered.
/// </para>
/// </remarks>
public enum TargetedVersionScope
{
    /// <summary>
    /// Only the version EPA marks as its current record — the (a) path. Usually one version per
    /// <c>(handlerId, sourceType)</c>, so a handler with both an <c>N</c> and an <c>I</c> record yields two.
    /// </summary>
    /// <remarks>
    /// <b>Read from the summaries feed and never inferred.</b> If EPA flags nothing as current, the run
    /// reports that and fetches nothing rather than falling back to the highest sequence — a guess about
    /// which version is current, merged into <c>dbo.HandlerSource</c> and then reconciled by script 521, is
    /// indistinguishable afterwards from EPA having said so.
    /// </remarks>
    CurrentRecord,

    /// <summary>Every version the summaries feed names for the handler — the (b) path, entire history.</summary>
    EveryVersion,
}

/// <summary>
/// One handler asked for by hand: what the loader's <c>--handler-id</c> switch turns into.
/// </summary>
/// <param name="HandlerId">EPA's handler identifier, as typed. Normalised by <see cref="Normalized"/>.</param>
/// <param name="Scope">Latest record, or the whole history.</param>
/// <remarks>
/// <para>
/// <b>A record rather than two parameters, for <c>LoadRunRequest</c>'s reason and one of its own:</b> a
/// string and an enum are exactly the pair a future third option would be appended to, and the validation
/// below has to live somewhere that both the argument parser and the orchestrator can call. The parser needs
/// it so a typo is refused before a credential is proved; the orchestrator needs it because it is also
/// reachable from a test and from any future caller.
/// </para>
/// <para>
/// <b>This is a diagnostic, not a second loading mode.</b> It exists because an operator needs to be able to
/// ask "what does EPA actually hold for this site" without waiting for, or disturbing, the scheduled load —
/// so the run it opens is <c>RunMode = 'Targeted'</c>, which script 510 exempts from the in-flight refusal in
/// both directions and script 525 refuses to resume from. Everything that makes a scheduled run
/// consequential is absent: no watermark is read, none is moved, and no code list is refreshed.
/// </para>
/// </remarks>
public sealed record TargetedLoadRequest(string HandlerId, TargetedVersionScope Scope)
{
    /// <summary>
    /// EPA's identifier, trimmed and upper-cased.
    /// </summary>
    /// <remarks>
    /// <b>Upper-cased deliberately, because the failure mode of not doing it is the worst kind.</b> EPA's
    /// handler identifiers are upper-case throughout the spec's examples, and a lower-cased one comes back
    /// from <c>/hd/sources/summaries</c> as an empty array or a documented <c>404</c> — which is
    /// indistinguishable from "no such handler". An operator would then reasonably conclude the site is not
    /// in RCRAInfo. Upper-casing an already-upper-case identifier changes nothing, so the only case this
    /// affects is the one where it helps.
    /// </remarks>
    public string Normalized() => HandlerId?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>Everything wrong with the request, in a form safe to print.</summary>
    /// <returns>The problems, empty when there are none.</returns>
    /// <remarks>
    /// The handler identifier <i>is</i> reproduced in these messages, and that is permitted: an identifier is
    /// not a secret — script 506 and the plan both say so — and a refusal that will not say which value it
    /// refused is a refusal an operator cannot act on. What is never reproduced anywhere is a credential.
    /// The width check is <see cref="SummaryPayload.MaxHandlerIdLength"/> rather than a literal, so the one
    /// number that has to match <c>logs.HandlerLoadStatus.HandlerId</c> lives in one place.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];
        string id = Normalized();

        if (id.Length == 0)
        {
            problems.Add(
                "--handler-id was given no value. A targeted run exists to ask about one handler, and a "
                + "blank identifier on /hd/sources/summaries does not fail -- it asks EPA for every handler "
                + "it has.");

            return problems;
        }

        if (id.Length > SummaryPayload.MaxHandlerIdLength)
        {
            problems.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "--handler-id '{0}' is {1} characters, wider than the {2} logs.HandlerLoadStatus holds. "
                    + "It is refused rather than clipped, for the reason G36 records: a truncated handlerId "
                    + "names a DIFFERENT handler rather than none.",
                    id,
                    id.Length,
                    SummaryPayload.MaxHandlerIdLength));
        }

        if (id.Any(char.IsWhiteSpace))
        {
            // Interior whitespace only -- the ends were trimmed above. A pasted identifier that broke across
            // a line is the usual source, and it would otherwise be escaped into the query string and come
            // back as a 404 that reads as "no such handler".
            problems.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "--handler-id '{0}' contains a space. EPA's identifiers do not, so this is a paste that "
                    + "picked up a line break -- and the request it would build comes back empty, which "
                    + "reads as a handler that does not exist.",
                    id));
        }

        return problems;
    }
}
