using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// What a previous unfinished run of this activity location got through, read <b>before</b> the new run
/// opens. The answer to "is there anything to resume from, and what had it already dealt with".
/// </summary>
/// <remarks>
/// <para>
/// <b>Read before the run starts, because <c>logs.uspStartLoadRun</c> takes
/// <c>@ResumedFromLoadRunId</c> as an input.</b> The provenance is recorded on the new run's own row, so
/// the answer has to exist before that row does. Which is also why <see cref="ResumedFromStatus"/> can
/// legitimately read <c>Running</c>: the abandonment sweep lives in the start procedure and has not run
/// yet, so a run killed by a reboot is still marked <c>Running</c> at the moment this was read. See
/// <see cref="LoadRunOptions.AbandonAfterMinutes"/>.
/// </para>
/// <para>
/// <b><see cref="None"/> is a normal answer on four separate paths</b> — no previous run, one that
/// succeeded, one still alive, one too old to trust — and every one of them means the same thing to the
/// caller: fetch everything the walk names. That is safe in all four cases, because
/// <c>dbo.uspMergeHandlerSourceBatch</c> reports an unchanged version as <c>Unchanged</c>: a needless
/// re-fetch costs requests and changes no data.
/// </para>
/// <para>
/// <b>Two sets, not one, and <see cref="Unfinished"/> is the one that is easy to leave out.</b>
/// <see cref="Completed"/> is what the new run skips. <see cref="Unfinished"/> is what the previous run
/// enumerated and never finished — the new run's walk should name those again, and if it does not, EPA
/// has stopped reporting a record it reported yesterday. That is the G25 signal, and nothing else in
/// this application is in a position to see it, because seeing it requires holding both sets at once.
/// </para>
/// </remarks>
/// <param name="ResumedFromLoadRunId">
/// The run these versions came from, or <see langword="null"/> when there is nothing to resume.
/// </param>
/// <param name="ResumedFromRunMode">That run's mode — <c>Full</c>, <c>Incremental</c> or <c>Reconcile</c>.</param>
/// <param name="ResumedFromStatus">
/// Its status <i>as it stands now</i>, which may be <c>Running</c> — see the remarks. Worth logging: it
/// is the only trace of why a resume happened.
/// </param>
/// <param name="ResumedFromStartedDateUtc">When it started, which is what the age limit was applied to.</param>
/// <param name="RequestedFromDate">
/// The window it was told to fetch, or <see langword="null"/> for a full load that requested none.
/// </param>
/// <param name="RequestedToDate">The other end of that window.</param>
/// <param name="Completed">
/// Versions it finished. The new run must not fetch these; it writes script 520's <c>Skip</c> mode for
/// them instead, through <see cref="ILoadJournal.SkipAsync"/>.
/// </param>
/// <param name="Unfinished">
/// Versions it enumerated and did not finish, at any of <c>Pending</c>, <c>InProgress</c>,
/// <c>Failed</c> or <c>Skipped</c>. Re-fetched, and — see the remarks — watched for.
/// </param>
public sealed record LoadResumePoint(
    int? ResumedFromLoadRunId,
    string? ResumedFromRunMode,
    string? ResumedFromStatus,
    DateTimeOffset? ResumedFromStartedDateUtc,
    DateOnly? RequestedFromDate,
    DateOnly? RequestedToDate,
    IReadOnlySet<HandlerVersion> Completed,
    IReadOnlySet<HandlerVersion> Unfinished)
{
    /// <summary>Nothing to resume from. See the remarks on the type: this is a normal answer.</summary>
    public static LoadResumePoint None { get; } =
        new(null, null, null, null, null, null, new HashSet<HandlerVersion>(), new HashSet<HandlerVersion>());

    /// <summary>Whether a previous run was found to resume from.</summary>
    public bool HasResumePoint => ResumedFromLoadRunId is not null;

    /// <summary>How many versions the previous run enumerated, finished or not.</summary>
    public int KnownCount => Completed.Count + Unfinished.Count;

    /// <summary>
    /// Splits the versions this run's walk named into what to fetch and what to skip, and names what the
    /// walk did not produce.
    /// </summary>
    /// <param name="walked">
    /// The versions the summaries walk found, in first-seen order — <c>SummaryWalkReport.Versions</c>
    /// mapped through <c>HandlerSourceSummary.ToVersion</c>.
    /// </param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// <para>
    /// <b>Pure, and separate from the read for that reason.</b> The read needs a database and happens
    /// once; this is arithmetic over two sets and is where every mistake would be. A test can drive it
    /// directly.
    /// </para>
    /// <para>
    /// <b>The walk is de-duplicated here even though it arrives distinct.</b>
    /// <c>SummaryWalkReport.Versions</c> is already de-duplicated across windows, so this is belt and
    /// braces — but the consequence of a repeat is not a slow load: script 520 <i>throws</i> when one
    /// payload names the same key twice, which would fail a whole flush and lose the journal for every
    /// other handler in it.
    /// </para>
    /// <para>
    /// <b><see cref="LoadResumePlan.Vanished"/> is not filtered to
    /// <see cref="Completed"/>.</b> A version the previous run knew about in any state and this walk did
    /// not name is equally interesting, and the previous run's own success or failure on it says nothing
    /// about whether EPA still reports it.
    /// </para>
    /// </remarks>
    public LoadResumePlan Plan(IEnumerable<HandlerVersion> walked)
    {
        ArgumentNullException.ThrowIfNull(walked);

        List<HandlerVersion> toFetch = [];
        List<HandlerVersion> toSkip = [];
        HashSet<HandlerVersion> seen = [];

        foreach (HandlerVersion version in walked)
        {
            if (!seen.Add(version))
            {
                continue;
            }

            if (Completed.Contains(version))
            {
                toSkip.Add(version);
            }
            else
            {
                toFetch.Add(version);
            }
        }

        // Key order rather than the order the two sets happen to iterate in, so a run summary that names
        // the first few is reproducible and a test over them is assertable.
        List<HandlerVersion> vanished =
            [.. Completed.Concat(Unfinished)
                         .Where(version => !seen.Contains(version))
                         .OrderBy(version => version.HandlerId, StringComparer.Ordinal)
                         .ThenBy(version => version.SourceType, StringComparer.Ordinal)
                         .ThenBy(version => version.Sequence)];

        return new LoadResumePlan(this, toFetch, toSkip, vanished);
    }

    /// <summary>
    /// One line for a run log. Identifiers, counts and a status — nothing here could be a credential.
    /// </summary>
    public override string ToString() =>
        HasResumePoint
            ? string.Format(
                CultureInfo.InvariantCulture,
                "resume from run {0} ({1}/{2}, started {3:yyyy-MM-dd HH:mm}Z): {4} completed, {5} unfinished",
                ResumedFromLoadRunId,
                ResumedFromRunMode,
                ResumedFromStatus,
                ResumedFromStartedDateUtc?.UtcDateTime,
                Completed.Count,
                Unfinished.Count)
            : "no resume point";
}

/// <summary>
/// What this run should do with the versions its walk named, given what the previous run got through.
/// </summary>
/// <param name="Point">Where the plan came from. <c>LoadResumePoint.None</c> when nothing was resumed.</param>
/// <param name="ToFetch">
/// Versions to request from EPA, in the walk's first-seen order. Everything, when there is no resume
/// point.
/// </param>
/// <param name="ToSkip">
/// Versions the previous run finished. <b>These still need <see cref="ILoadJournal.SkipAsync"/></b> —
/// see <see cref="SkipRate"/> for what the grid looks like otherwise.
/// </param>
/// <param name="Vanished">
/// Versions the previous run knew about that this walk did not name, in key order. <b>Not an error and
/// not actionable by this run</b> — reported because the alternative is that nobody ever notices (G25).
/// </param>
public sealed record LoadResumePlan(
    LoadResumePoint Point,
    IReadOnlyList<HandlerVersion> ToFetch,
    IReadOnlyList<HandlerVersion> ToSkip,
    IReadOnlyList<HandlerVersion> Vanished)
{
    /// <summary>A plan with nothing resumed: fetch everything the walk named.</summary>
    /// <param name="walked">The walk's versions.</param>
    /// <returns>The plan.</returns>
    public static LoadResumePlan FetchAll(IEnumerable<HandlerVersion> walked) =>
        LoadResumePoint.None.Plan(walked);

    /// <summary>How many versions the walk named, after de-duplication.</summary>
    public int WalkedCount => ToFetch.Count + ToSkip.Count;

    /// <summary>
    /// What fraction of the walk this run does not have to request, as a percentage.
    /// </summary>
    /// <remarks>
    /// The number that says whether resume is working. At <c>2</c> requests per second and roughly two
    /// requests per handler version (<c>other-ids</c> is a separate call, plan [R8]), an initial load
    /// that has to start over costs about as long as it had already spent — so a resumed run reporting a
    /// skip rate of zero when it found a resume point is a defect worth chasing, and this is where it
    /// shows.
    /// </remarks>
    public double SkipRate => WalkedCount == 0 ? 0 : ToSkip.Count * 100.0 / WalkedCount;

    /// <summary>
    /// One line for a run log. Counts only, plus whatever <see cref="LoadResumePoint.ToString"/> holds.
    /// </summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}; walk named {1}: {2} to fetch, {3} to skip ({4:F1}%), {5} vanished",
            Point,
            WalkedCount,
            ToFetch.Count,
            ToSkip.Count,
            SkipRate,
            Vanished.Count);
}
