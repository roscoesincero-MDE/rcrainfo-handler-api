using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// How often the buffered status and attempt rows are written. Bound from the <c>RCRAInfoLoad</c>
/// configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both numbers are starting points, not findings.</b> Plan §D2 says to begin at something like 100
/// rows or 30 seconds and tune against F2's measured numbers rather than guessing now, and neither has
/// been measured against EPA yet because that needs a credential. They are configurable so that tuning
/// is a settings change rather than a release.
/// </para>
/// <para>
/// <b>The two thresholds answer different questions, which is why both exist.</b>
/// <see cref="FlushRowCount"/> bounds how much is at risk and how large a single JSON payload gets;
/// <see cref="FlushInterval"/> bounds how stale <c>logs.HandlerLoadStatus</c> is allowed to look to the
/// monitoring web application, which is the whole reason that table is written during a run rather than
/// at the end of one. A row-count trigger alone would leave a run that is grinding through slow fetches
/// showing no progress for as long as the fetches take.
/// </para>
/// </remarks>
public sealed class LoadJournalOptions
{
    /// <summary>The configuration section this binds from.</summary>
    /// <remarks>
    /// Shared with the orchestrator's own settings — <c>RCRAInfoLoad:ActivityLocation</c> is the one
    /// <c>RcraInfoDataRequest.Summaries</c> names in its refusal message. Two classes binding one section
    /// is deliberate: an operator tuning a load reads one block, and the flush interval and the state in
    /// scope are the same kind of decision made by the same person.
    /// </remarks>
    public const string SectionName = "RCRAInfoLoad";

    /// <summary>
    /// How many buffered rows trigger a flush. Counted across the status buffers and the attempt buffer
    /// together, because they are written by one flush and lost by one crash.
    /// </summary>
    public int FlushRowCount { get; set; } = 100;

    /// <summary>How long buffered rows may sit before the next buffered row forces a flush.</summary>
    /// <remarks>
    /// Measured from the end of the previous flush, and checked when a row is buffered rather than by a
    /// background timer — see <c>LoadJournal</c> for why there is no timer.
    /// </remarks>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates everything, for a fail-at-startup check.</summary>
    /// <returns>The problems found, empty when the options are usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (FlushRowCount < 1)
        {
            problems.Add($"{SectionName}:FlushRowCount must be at least 1.");
        }

        if (FlushInterval <= TimeSpan.Zero)
        {
            // Zero would mean "flush on every row", which is not a faster load with better logging: it is
            // one round-trip per handler to the same table the batch procedures exist to avoid, and it
            // reads as a configuration value rather than as the decision it is.
            problems.Add($"{SectionName}:FlushInterval must be greater than zero.");
        }

        return problems;
    }

    /// <summary>The options as a single line for a startup log. Contains no secret; there is none here.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {{ FlushRowCount = {1}, FlushInterval = {2} }}",
            SectionName,
            FlushRowCount,
            FlushInterval);
}
