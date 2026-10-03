using System.Globalization;
using System.Text;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// What the record-detail feed writes for its date fields, beside what the summaries feed writes — the G25
/// answer for <c>HandlerSource</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is a count, a size, a duration, a handler identifier, a version key, a JSON path or
/// EPA's raw date text.</b> That is AR8's rule for <c>logs.ExecutionLog</c>, and it holds all the harder here
/// even though this report never reaches the database: it is printed to a console an operator may paste into a
/// ticket, which is a wider audience than a log table rather than a narrower one. No credential, no URI, no
/// query string, no <c>FailureMessage</c>, and no contact field.
/// </para>
/// <para>
/// <b>The raw date text is the exception, and it is the point of the probe rather than a lapse.</b> A date of
/// record is not personal information, and the selection rule in <see cref="RawDateScan"/> — a property name
/// ending in <c>Date</c> — makes it impossible for a contact name or an email address to be carried here by a
/// walk of a handler payload.
/// </para>
/// <para>
/// <b><see cref="Disagreements"/> is what this report is for.</b> Two lists of strings would leave a reader to
/// diff them by eye; a named list of fields whose shapes differ between the two feeds is a finding or an
/// all-clear. An empty list is the expected result and is stated as such rather than left as an absence.
/// </para>
/// <para>
/// <b>[R46] And that line has to be checkable from itself.</b> Its first version named the fields and nothing
/// else, and the first live run — <c>MD0570024000</c>, 2026-09-07 — printed three fields as differing "in shape"
/// when every value on both sides was a bare <c>"yyyy-MM-dd"</c>. The comparison was on raw values, which cannot
/// agree across a 23-version summaries body and a one-version record body. Each entry now carries both shapes,
/// so a reader can see whether the claim holds without diffing the two blocks above it.
/// </para>
/// </remarks>
/// <param name="HandlerId">The handler asked about. Not a secret (script 506).</param>
/// <param name="SourceType">The source type of the version fetched, or <see langword="null"/>.</param>
/// <param name="Sequence">The sequence of the version fetched, or <see langword="null"/>.</param>
/// <param name="ChosenBecause">
/// Why that version and not another, in one phrase. Printed because the choice is the probe's and not the
/// operator's, and a reader comparing this against EPA's screen needs to know which row to look at.
/// </param>
/// <param name="VersionsAvailable">
/// Every <c>sourceType</c>/<c>sequence</c> the summaries feed named for this handler, ascending. Printed so a
/// reader can re-run against a different version without querying anything.
/// </param>
/// <param name="SummariesHttpStatusCode">What EPA answered for the summaries call.</param>
/// <param name="SummariesDurationMs">How long the summaries call took.</param>
/// <param name="SummariesResponseBytes">The summaries response size.</param>
/// <param name="SourceHttpStatusCode">What EPA answered for the record-detail call.</param>
/// <param name="SourceDurationMs">How long the record-detail call took.</param>
/// <param name="SourceResponseBytes">
/// The record-detail response size. Worth having beside the summaries size: it is the per-version cost the
/// initial load pays several hundred thousand times.
/// </param>
/// <param name="SummariesRawDates">
/// Distinct raw JSON text per path from the summaries body — G25's already-answered half, re-read here so the
/// comparison is against this call rather than against a note from a previous revision.
/// </param>
/// <param name="SourceRawDates">
/// Distinct raw JSON text per path from the record-detail body. <b>This is the open half of G25</b> and the
/// reason the probe exists.
/// </param>
/// <param name="Disagreements">
/// Date fields present in both bodies whose <b>shapes</b> differ, each entry naming the field and both sides'
/// shapes — record detail first, matching the order the blocks are printed in. Empty means the two feeds agree.
/// <b>[R46] Shapes and not values:</b> the two feeds are asked different questions, so the summaries body
/// carries every version's dates and the record body carries one version's, and a comparison of raw values
/// could never report agreement for a handler with more than one version.
/// </param>
/// <param name="Problem">
/// Why the probe has no answer, or <see langword="null"/>. Safe to print: an outcome name, a status code or a
/// JSON path, never any part of a body.
/// </param>
public sealed record SourceProbeReport(
    string HandlerId,
    string? SourceType,
    int? Sequence,
    string? ChosenBecause,
    IReadOnlyList<string> VersionsAvailable,
    int? SummariesHttpStatusCode,
    int SummariesDurationMs,
    int? SummariesResponseBytes,
    int? SourceHttpStatusCode,
    int SourceDurationMs,
    int? SourceResponseBytes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SummariesRawDates,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SourceRawDates,
    IReadOnlyList<string> Disagreements,
    string? Problem)
{
    /// <summary>Whether the record-detail body was fetched and scanned.</summary>
    public bool IsAnswered => Problem is null;

    /// <summary>The version fetched, as EPA's own screen writes it.</summary>
    public string? Version =>
        SourceType is null || Sequence is null
            ? null
            : string.Format(CultureInfo.InvariantCulture, "{0}/{1}", SourceType, Sequence);

    /// <summary>The whole report, as an operator should see it.</summary>
    /// <returns>Several lines. Contains no credential, no URI and no query string.</returns>
    public override string ToString()
    {
        StringBuilder text = new();

        text.Append(CultureInfo.InvariantCulture, $"Record-detail probe: {HandlerId}")
            .AppendLine()
            .Append(CultureInfo.InvariantCulture, $"  summaries: http={Status(SummariesHttpStatusCode)} ")
            .Append(CultureInfo.InvariantCulture, $"duration={SummariesDurationMs}ms ")
            .Append(CultureInfo.InvariantCulture, $"bytes={Size(SummariesResponseBytes)}")
            .AppendLine();

        if (VersionsAvailable.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"  versions EPA names ({VersionsAvailable.Count}): ")
                .AppendLine(string.Join(", ", VersionsAvailable));
        }

        if (Version is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"  fetched {Version} -- {ChosenBecause}")
                .AppendLine()
                .Append(CultureInfo.InvariantCulture, $"  record detail: http={Status(SourceHttpStatusCode)} ")
                .Append(CultureInfo.InvariantCulture, $"duration={SourceDurationMs}ms ")
                .Append(CultureInfo.InvariantCulture, $"bytes={Size(SourceResponseBytes)}")
                .AppendLine();
        }

        if (!IsAnswered)
        {
            text.Append("  no answer: ").AppendLine(Problem);

            return text.ToString();
        }

        // The record-detail scan first, because it is the half G25 has open and the half that cannot be
        // recovered later: no column retains a raw body, so a shape seen and not written down is unobserved.
        Append(text, "record detail (/hd/sources/{id}/{type}/{seq}) -- THE OPEN HALF OF G25", SourceRawDates);
        Append(text, "summaries (/hd/sources/summaries) -- answered in [R37], re-read for comparison",
            SummariesRawDates);

        if (Disagreements.Count == 0)
        {
            text.AppendLine(
                "  the two feeds write every date field they share in the same shape, so the summaries answer "
                + "does carry over to the record detail for these fields. That is the expected result and is "
                + "stated rather than left as a silence -- it is a finding either way.");
        }
        else
        {
            // [R46] The shapes are printed beside the field name, on both sides, so the claim can be checked
            // from the line that makes it. The first version named the fields only, and its first live run named
            // three fields that did not differ at all -- a claim a reader had no way to test without diffing the
            // two blocks above by eye, which is the work the line exists to save.
            text.Append(CultureInfo.InvariantCulture,
                    $"  {Disagreements.Count} date field(s) DIFFER in shape (record detail vs summaries):")
                .AppendLine();

            foreach (string entry in Disagreements)
            {
                text.Append("    ").AppendLine(entry);
            }

            text.AppendLine(
                "    Read the two blocks above for the exact text. This is the [R33] shape: a value the "
                + "converter accepts today because it happens to fit, on a field nobody had read raw.");
        }

        return text.ToString();
    }

    private static void Append(
        StringBuilder text,
        string caption,
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan)
    {
        text.Append("  ").Append(caption).AppendLine(":");

        if (scan.Count == 0)
        {
            text.AppendLine("    (no date-shaped property anywhere in the body)");

            return;
        }

        foreach ((string path, IReadOnlyList<string> samples) in scan)
        {
            text.Append("    ")
                .Append(path)
                .Append(": ")
                .AppendLine(samples.Count == 0 ? "(present, no value captured)" : string.Join(", ", samples));
        }
    }

    private static string Status(int? code) =>
        code?.ToString(CultureInfo.InvariantCulture) ?? "(none)";

    private static string Size(int? bytes) =>
        bytes?.ToString(CultureInfo.InvariantCulture) ?? "(none)";
}
