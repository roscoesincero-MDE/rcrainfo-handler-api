namespace RCRAInfo.Loader.Load;

/// <summary>
/// One handler an operator asked the record-detail probe about, and whether it is a handler worth asking for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type rather than a bare string, for <see cref="TargetedLoadRequest"/>'s reason:</b> the refusals
/// belong beside the value. A throw out of a stage is reported by <c>Program</c> as unusable
/// <i>configuration</i>, and a handler identifier typed into a Task Scheduler action is not configuration —
/// validating here makes it exit code 9 with the switch named, which sends the operator to the argument list.
/// </para>
/// <para>
/// <b>It takes a handler and not a version, which is the one interesting decision in this type.</b> The
/// alternative was <c>--probe-source &lt;id&gt; &lt;sourceType&gt; &lt;sequence&gt;</c>, and it was rejected
/// because an operator does not know a valid triple: sequence numbers are scoped per source type, EPA's own
/// numbering has gaps in 3.4% of lineages, and the version list is not printed anywhere the operator can read
/// before running this. A wrong triple would come back <c>404</c>, and a <c>404</c> is exactly the answer this
/// probe must not produce spuriously — it is the shape the loader reads as "EPA withdrew this record".
/// </para>
/// <para>
/// So the probe asks the summaries feed which versions exist and then fetches one. That costs a second request
/// and buys the thing G25 actually needs: <b>both feeds' raw date text for the same handler, in one errand,
/// with a direct comparison between them.</b> The two feeds are already known to differ in date shape, so an
/// answer for one does not transfer to the other, and reading them separately would leave a reader to diff two
/// console blocks by eye.
/// </para>
/// </remarks>
/// <param name="HandlerId">EPA's handler identifier, as typed. Normalised by <see cref="Normalized"/>.</param>
public sealed record SourceProbeRequest(string HandlerId)
{
    /// <summary>The identifier, trimmed and upper-cased.</summary>
    /// <remarks>
    /// Upper-cased for <see cref="TargetedLoadRequest.Normalized"/>'s reason: EPA's identifiers are upper case,
    /// the path segment is case-sensitive, and an operator pasting from an email may not be.
    /// </remarks>
    public string Normalized() => HandlerId?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>Everything wrong with this request.</summary>
    /// <returns>Zero or more messages, safe to print. Empty means the probe may be asked for it.</returns>
    /// <remarks>
    /// The width check is <see cref="SummaryPayload.MaxHandlerIdLength"/> rather than a literal, so the one
    /// place that number is stated governs the argument list too.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];
        string id = Normalized();

        if (id.Length == 0)
        {
            problems.Add(
                "--probe-source needs a handler identifier and none was given. There is no default: a probe "
                + "with no handler would have to choose one, and a diagnostic that picks its own subject "
                + "cannot be pointed at the record somebody is actually asking about.");

            return problems;
        }

        if (id.Length > SummaryPayload.MaxHandlerIdLength)
        {
            problems.Add(
                $"'{id}' is {id.Length} characters and a handler identifier is at most "
                + $"{SummaryPayload.MaxHandlerIdLength}. Refused here rather than sent, because EPA answers an "
                + "identifier it does not hold with a 404 -- which this codebase reads as a withdrawn record.");
        }

        return problems;
    }

    /// <summary>The request, for a console line.</summary>
    /// <returns>The normalised identifier, which is not a secret (script 506). Contains nothing else.</returns>
    public override string ToString() => Normalized();
}
