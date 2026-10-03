using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// What one run is scoped to. Bound from the same <c>RCRAInfoLoad</c> section as
/// <see cref="LoadJournalOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two classes over one section, for the reason <see cref="LoadJournalOptions.SectionName"/> gives</b> —
/// an operator tuning a load reads one block. They are separate types because the flush thresholds are
/// performance settings that may be changed freely, and this one is not: see
/// <see cref="ActivityLocation"/>.
/// </para>
/// </remarks>
public sealed class LoadRunOptions
{
    /// <summary>The configuration section this binds from — the same one the journal binds.</summary>
    public const string SectionName = LoadJournalOptions.SectionName;

    /// <summary>
    /// The widest <see cref="WindowDays"/> this loader will accept — one year.
    /// </summary>
    /// <remarks>
    /// A year is not a measurement either; it is the point past which "walk the population in slices" has
    /// stopped being what the code is doing. An initial load covering thirty years is 30 windows at this
    /// ceiling and 1,565 at the default, and both are fine — one request for all thirty years is the shape
    /// [R28] names as the one to avoid.
    /// </remarks>
    public const int MaxWindowDays = 366;

    /// <summary>
    /// The shortest <see cref="AbandonAfterMinutes"/> this loader will accept — fifteen minutes.
    /// </summary>
    /// <remarks>
    /// The same floor <c>logs.uspStartLoadRun</c> and <c>logs.uspGetHandlerLoadResumeSet</c> both
    /// enforce, repeated here so an unusable value stops the run at startup with the name of the
    /// <i>setting</i> rather than at the first database call with the name of a parameter.
    /// </remarks>
    public const int MinAbandonAfterMinutes = 15;

    /// <summary>
    /// The earliest <see cref="InitialLoadFromDate"/> this loader will accept — 1900-01-01.
    /// </summary>
    /// <remarks>
    /// Not a claim about RCRA, which was enacted in 1976 and whose notification programme began in 1980. It
    /// is a guard against the default <see cref="DateOnly"/> and against a mistyped year: <c>0001-01-01</c>
    /// at the default <see cref="WindowDays"/> is 105,000 windows, which is not a long load but a loop that
    /// never finishes, and it would be entered by a configuration file with a digit missing.
    /// </remarks>
    public static readonly DateOnly MinInitialLoadFromDate = new(1900, 1, 1);

    /// <summary>
    /// The value to configure absent a decision from MDE, named in the refusal message — 1980-01-01.
    /// </summary>
    /// <remarks>
    /// The year EPA's notification requirement took effect, so no handler source can predate it by any
    /// honest reading. It is a <i>recommendation</i> and not a default for the reason
    /// <see cref="InitialLoadFromDate"/> gives, and it carries one known risk worth stating: a legacy
    /// migration stamped with a placeholder date — <c>1900-01-01</c> is the usual one — would sit before it
    /// and never be asked for. Workstream F2 can settle that with a single wide window, because the answer
    /// is one request.
    /// </remarks>
    public const string RecommendedInitialLoadFromDate = "1980-01-01";

    /// <summary>
    /// The two-letter activity location this database holds. <c>MD</c> for this project (G2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is deliberately no default.</b> A default would mean nobody ever states which
    /// jurisdiction's data this database is, and the value is not a preference: it selects the handlers
    /// <c>dbo.uspReconcileCurrentRecord</c> reconciles and the jurisdiction the five scoped code lists are
    /// fetched for. Absent, the run fails at startup with the name of the setting, which is the only
    /// failure mode here that costs nothing.
    /// </para>
    /// <para>
    /// <b>Changing it on an existing database is not a configuration change.</b> Script 523 retires codes
    /// only within the activity locations a payload mentions (plan [R16]), so pointing an established
    /// mirror at a second state does not move it — it <i>adds</i> that state's codes and handlers beside
    /// Maryland's and then keeps both, with the previous jurisdiction's rows never refreshed and never
    /// retired. Nothing in the schema forbids that and no status code reports it; it is stated here because
    /// this string is where it would begin.
    /// </para>
    /// </remarks>
    public string ActivityLocation { get; set; } = string.Empty;

    /// <summary>
    /// Where a full load starts. Required for a full load, unused for every other run, and
    /// <see langword="null"/> until somebody states it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A full load has no start date anywhere else, and until this setting existed it had none at
    /// all.</b> Script 512 returns <c>RecommendedFromDate = NULL</c> when <c>config.LoadWatermark</c> has
    /// never advanced, and the comment beside it says that is "how the loader knows to ask for everything
    /// rather than for a window". But <c>/hd/sources/summaries</c> cannot be asked for everything: its
    /// <c>startDate</c> is required by the operation's prose, the walk is date-windowed because the endpoint
    /// has no paging, and an unbounded call is refused by <c>RcraInfoDataRequest.Summaries</c> by design. So
    /// "everything" has to be given a first day, and this is where it is given one.
    /// </para>
    /// <para>
    /// <b>There is deliberately no default, for <see cref="ActivityLocation"/>'s reason.</b> This value
    /// decides how far back the mirror reaches, and a default would decide it silently — a year too late and
    /// the earliest handler sources are absent, in a database whose only completeness signal is that the
    /// watermark advanced. That failure is invisible: the walk reports every window it asked for as walked,
    /// and it never asked. <see cref="RecommendedInitialLoadFromDate"/> is named in the refusal so the
    /// operator does not have to guess, and the refusal costs one run that fetched nothing.
    /// </para>
    /// <para>
    /// <b>It is a floor and not a start.</b> A run whose watermark <i>has</i> advanced ignores this
    /// entirely; script 512's <c>RecommendedFromDate</c> wins whenever it has one. So an operator who wants
    /// to re-load history does not edit this — they rewind the watermark through
    /// <c>config.uspSetLoadWatermark</c>, which is the object that records who moved it and when.
    /// </para>
    /// <para>
    /// <b>The gap it closes is the one the schedule cannot be relied on to keep.</b> A missed week, a
    /// month of a disabled task, or a first load covering the whole notification era are the same shape of
    /// request — one wide range, split into <see cref="WindowDays"/> windows — and the walk already handles
    /// thousands of windows. Forty-six years at the default width is roughly 2,400 of them.
    /// </para>
    /// </remarks>
    public DateOnly? InitialLoadFromDate { get; set; }

    /// <summary>
    /// How many days one <c>/hd/sources/summaries</c> call asks for. Seven by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only lever on summaries response size, because that endpoint has no paging</b> — no
    /// <c>offset</c>, no <c>limit</c>, unlike the three <c>/ce/</c> summaries endpoints beside it (plan
    /// [R28]). One call with a start date early enough to mean "everything" is a single request whose
    /// response holds the entire Maryland population, and the failure mode is not an error: it is a very
    /// large <c>200</c>, or a timeout, and neither is testable in pieces.
    /// </para>
    /// <para>
    /// <b>Seven is a starting point, not a measurement, and it is deliberately on the small side.</b>
    /// Nothing in the spec bounds how many versions one day carries, and the response has no envelope —
    /// no total, no <c>hasMore</c> — so there is no way to tell a complete window from a service-imposed
    /// cap by inspecting the body. A narrow window makes the question moot; a wide one makes it
    /// unanswerable. Workstream F2 measures a real response and this is the number it tunes.
    /// </para>
    /// <para>
    /// <b>It is also the retry granularity.</b> A window that fails is re-fetched whole, and — because the
    /// watermark cannot advance past a window that did not complete — one failed window in an overnight
    /// initial load costs the day's progress from that point on. Wider windows mean fewer requests and more
    /// work lost per failure.
    /// </para>
    /// </remarks>
    public int WindowDays { get; set; } = 7;

    /// <summary>
    /// How long a run may sit at <c>Running</c> before the next run counts it as killed. Twelve hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One setting because two procedures must agree, and nothing can make them agree but the
    /// caller.</b> <c>logs.uspStartLoadRun</c> uses it to sweep a stale <c>Running</c> row to
    /// <c>Abandoned</c>; <c>logs.uspGetHandlerLoadResumeSet</c> uses it to decide whether a
    /// <c>Running</c> row is a corpse worth resuming from. The resume read necessarily runs
    /// <i>first</i> — the start procedure takes <c>@ResumedFromLoadRunId</c> as an input — so at the
    /// moment it looks, a reboot-killed run has not been swept yet and is still marked <c>Running</c>.
    /// Reading it as terminal-only would return nothing on exactly the occasion resume exists for.
    /// </para>
    /// <para>
    /// So both procedures apply the same rule a moment apart, and disagreement is a real defect in both
    /// directions: a shorter value at the resume read treats a healthy in-flight run as resumable and
    /// the second run skips versions the first is still writing, and a longer one puts the silent no-op
    /// back. Binding one setting and passing it to both is what removes the possibility.
    /// </para>
    /// <para>
    /// <b>Twelve hours is the database's own default and the floor of fifteen minutes is enforced
    /// there</b>, in both procedures. An initial load is expected to run for hours, so the threshold has
    /// to be longer than a legitimate run and shorter than the gap between scheduled ones.
    /// </para>
    /// </remarks>
    public int AbandonAfterMinutes { get; set; } = 720;

    /// <summary>
    /// How old a previous run's success may be and still be trusted enough to skip. Forty-eight hours;
    /// <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A correctness guard rather than housekeeping.</b> Skipping a version because an earlier run
    /// succeeded on it is a claim that EPA has not changed it since — and EPA <i>does</i> update a
    /// handler source in place: <c>dbo.HandlerSource</c> mirrors <c>SrcUpdatedDate</c> for that reason,
    /// and the natural key does not move when it happens. The older the success, the weaker the claim.
    /// </para>
    /// <para>
    /// The asymmetry is what makes the default conservative: re-fetching a version that has not changed
    /// costs two requests and <c>dbo.uspMergeHandlerSourceBatch</c> reports it <c>Unchanged</c>, while
    /// skipping one that has changed leaves a stale row nothing will ever ask about again. Forty-eight
    /// hours covers a nightly schedule with a night to spare; <see langword="null"/> is for an operator
    /// re-driving a long initial load by hand, who has decided to accept that.
    /// </para>
    /// </remarks>
    public int? ResumeMaxAgeHours { get; set; } = 48;

    /// <summary>
    /// How many fetched handler sources one <c>dbo.uspMergeHandlerSourceBatch</c> call carries. Twenty-five.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the checkpoint granularity of the fetch loop, and it is not the journal's.</b>
    /// <see cref="LoadJournalOptions.FlushRowCount"/> decides how often status rows reach
    /// <c>logs.HandlerLoadStatus</c>; this decides how often the <i>data</i> reaches
    /// <c>dbo.HandlerSource</c>. A killed run re-fetches at most this many versions whose payloads had been
    /// downloaded but not yet merged — and re-fetching is safe, because the merge reports an unchanged
    /// version as <c>Unchanged</c> and writes nothing.
    /// </para>
    /// <para>
    /// <b>Twenty-five rather than the journal's hundred, because these are not comparable rows.</b> A
    /// status row is a natural key and a status; a handler source is the largest document this project
    /// handles — 215 mapped columns and 18 repeating collections, three of them nested — and one merge call
    /// touches nineteen tables inside a single transaction. The cost of a wide batch is not the JSON, it is
    /// the length of that transaction. Workstream F2 tunes this against a measured payload size, alongside
    /// <see cref="WindowDays"/> and the throttle numbers.
    /// </para>
    /// <para>
    /// <b>The ceiling is the writer's, not this setting's.</b> <c>PayloadJson.Serialize</c> throws above
    /// <c>RCRAInfoDataOptions.MaxPayloadElements</c>, so the loop takes the smaller of this value and
    /// <see cref="ILoadRunWriter.MaxElementsPerCall"/> rather than validating against a copy of a number
    /// that lives somewhere else — the same reasoning <see cref="ILoadJournalWriter"/> records.
    /// </para>
    /// </remarks>
    public int FetchBatchSize { get; set; } = 25;

    /// <summary>Validates everything, for a fail-at-startup check.</summary>
    /// <returns>The problems found, empty when the options are usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        string location = ActivityLocation?.Trim() ?? string.Empty;

        if (location.Length == 0)
        {
            problems.Add(
                $"{SectionName}:{nameof(ActivityLocation)} is required. It is the two-letter state this "
                + "load is scoped to (MD for this project, G2).");
        }
        else if (location.Length != 2 || !location.All(char.IsAsciiLetter))
        {
            // Checked here as well as in RcraInfoDataRequest because the two failures land in different
            // places: this one stops an unattended 2am run before it opens a connection, and the other
            // one stops a request that was already being built.
            problems.Add(
                $"{SectionName}:{nameof(ActivityLocation)} must be two ASCII letters. It was "
                + $"'{location}', which is {location.Length} character(s).");
        }

        if (WindowDays < 1)
        {
            problems.Add(
                $"{SectionName}:{nameof(WindowDays)} must be at least 1. It was {WindowDays}, and a "
                + "window of zero days cannot be asked for -- EPA answers an inverted range with 200 and "
                + "an empty array, which reads as a quiet week.");
        }
        else if (WindowDays > MaxWindowDays)
        {
            // A ceiling and not just a floor. There is no paging on this endpoint and no total in the
            // response, so a window wide enough to hold years of versions produces one very large 200 that
            // cannot be checked for completeness -- see WindowDays.
            problems.Add(
                $"{SectionName}:{nameof(WindowDays)} must be at most {MaxWindowDays}. It was {WindowDays}. "
                + "/hd/sources/summaries has no paging and its response has no total, so a window this "
                + "wide cannot be checked for completeness -- narrow it and let the loop do the walking.");
        }

        // The same floor both procedures enforce, checked here so the refusal names the setting rather
        // than the parameter. Below it, logs.uspGetHandlerLoadResumeSet would offer a healthy in-flight
        // run's versions to a second run as work already done.
        if (AbandonAfterMinutes < MinAbandonAfterMinutes)
        {
            problems.Add(
                $"{SectionName}:{nameof(AbandonAfterMinutes)} must be at least "
                + $"{MinAbandonAfterMinutes}. It was {AbandonAfterMinutes}, and both "
                + "logs.uspStartLoadRun and logs.uspGetHandlerLoadResumeSet refuse a shorter threshold: "
                + "it would treat a healthy in-flight run as an abandoned one, sweep its row and hand "
                + "its unfinished versions to a second run.");
        }

        if (FetchBatchSize < 1)
        {
            // A floor and no ceiling: the ceiling is the writer's element limit, applied by the loop at
            // the point of use. Zero or negative would make the fetch loop merge nothing at all while
            // every status row still reported Succeeded -- a run that downloads the population and stores
            // none of it, with no error anywhere.
            problems.Add(
                $"{SectionName}:{nameof(FetchBatchSize)} must be at least 1. It was {FetchBatchSize}, "
                + "which would fetch every version and merge none of them, and report success.");
        }

        // Only the shape, and only when set. Absent is legal here and refused at the point of use instead:
        // it is required for a FULL load and unused for every other run, so validating it at startup would
        // stop a perfectly ordinary incremental run over a setting it never reads. LoadRun.Refuse names the
        // setting on the one run that needs it.
        if (InitialLoadFromDate is { } initial && initial < MinInitialLoadFromDate)
        {
            problems.Add(
                $"{SectionName}:{nameof(InitialLoadFromDate)} must be on or after "
                + $"{MinInitialLoadFromDate:yyyy-MM-dd}. It was {initial:yyyy-MM-dd}, which at "
                + $"{nameof(WindowDays)} = {WindowDays} would split into tens of thousands of windows -- the "
                + "shape a mistyped year produces, and it presents as a load that never finishes rather than "
                + $"as an error. Set it to {RecommendedInitialLoadFromDate} unless MDE has decided "
                + "otherwise (G38).");
        }

        if (ResumeMaxAgeHours is <= 0)
        {
            // Not folded into the check above: zero or negative here rejects every resume candidate,
            // which returns an empty set and reads as "nothing to resume" -- a configuration mistake
            // that makes every resumed run silently re-fetch the whole population and still succeed.
            problems.Add(
                $"{SectionName}:{nameof(ResumeMaxAgeHours)} must be at least 1 when set. It was "
                + $"{ResumeMaxAgeHours}, which rejects every resume candidate -- so a resumed run would "
                + "re-fetch everything and report success. Omit it, or set it to null, for no limit.");
        }

        return problems;
    }

    /// <summary>
    /// The activity location as every request form wants it: trimmed and upper-cased.
    /// </summary>
    /// <returns>The two-letter code.</returns>
    /// <remarks>
    /// Normalised in one place because it is compared against <c>dbo.HandlerSource.ActivityLocation</c>,
    /// which the loader writes from EPA's own payload — where it is upper case.
    /// </remarks>
    public string NormalizedActivityLocation() =>
        ActivityLocation.Trim().ToUpperInvariant();

    /// <summary>The options as a single line for a startup log. Contains no secret; there is none here.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {{ ActivityLocation = {1}, WindowDays = {2}, FetchBatchSize = {3}, "
            + "AbandonAfterMinutes = {4}, ResumeMaxAgeHours = {5}, InitialLoadFromDate = {6} }}",
            SectionName,
            ActivityLocation,
            WindowDays,
            FetchBatchSize,
            AbandonAfterMinutes,
            ResumeMaxAgeHours?.ToString(CultureInfo.InvariantCulture) ?? "(no limit)",

            // Words rather than an empty gap, for the reason ResumeMaxAgeHours prints "(no limit)": a
            // startup line reading "InitialLoadFromDate = " is indistinguishable from a truncated message,
            // and this is the setting whose absence stops a full load.
            InitialLoadFromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(not set)");
}
