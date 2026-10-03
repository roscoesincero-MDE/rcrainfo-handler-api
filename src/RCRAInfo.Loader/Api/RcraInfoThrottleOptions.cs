using System.Globalization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// How hard this loader is willing to push EPA, and what it does when EPA pushes back. Bound from the
/// <c>RCRAInfoApi:Throttle</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two settings answer G21 and the rest configure a pipeline that already existed.</b>
/// <see cref="MaxRequestsPerSecond"/> and <see cref="MaxConcurrentRequests"/> are the loader's own rate
/// gate — <c>RequestPacer</c>. Everything below them tunes the standard resilience handler that has been on
/// the <c>rcrainfo-data</c> client since §D1: retries, backoff, timeouts and the circuit breaker were
/// running on package defaults, which is to say on numbers chosen for a generic web application rather than
/// for a several-hundred-thousand-request overnight batch against a government service. Naming them here
/// makes them a decision, and makes F2 able to change one number rather than a call site.
/// </para>
/// <para>
/// <b>Two requests per second is a stated assumption, not a published limit.</b> EPA publishes no rate
/// limit for the RCRAInfo REST API — that is what G21 asks about, and it stayed open through §D1 because
/// there was no credential to measure with. There is one now, and the answer chosen is deliberately
/// conservative: 2 requests/second is roughly 172,000 requests a day, and the initial load's request count
/// is about twice the handler-version count because <c>other-ids</c> is per-handler and in scope ([R8]).
/// So the default is slow enough that an initial load takes days rather than hours, and that is the right
/// trade for a first run against a service whose tolerance is unmeasured: being throttled is recoverable,
/// being <i>blocked</i> is a phone call. Raise it once F2 has watched a real load and counted the
/// <c>429</c>s.
/// </para>
/// <para>
/// <b>The rate gate and the retry pipeline are not the same mechanism and neither replaces the other.</b>
/// The gate is what this loader does when EPA is <i>healthy</i> — it bounds a rate nobody asked us to
/// bound. The retry settings are what it does when EPA says stop, and the difference matters because
/// <see cref="MaxRequestsPerSecond"/> is a number this project invented while <c>Retry-After</c> is an
/// instruction from the far end.
/// </para>
/// </remarks>
public sealed class RcraInfoThrottleOptions
{
    /// <summary>The configuration section this binds from — nested under <c>RCRAInfoApi</c>.</summary>
    public const string SectionName = RcraInfoApiOptions.SectionName + ":Throttle";

    /// <summary>The most requests per second this loader will send to EPA. Two by default.</summary>
    /// <remarks>
    /// A rate and not a burst: <c>RequestPacer</c> spaces requests by <c>1 / MaxRequestsPerSecond</c> and
    /// does not accumulate credit for idle time. A bucket that let a paused run spend its savings would
    /// produce the one shape most likely to be noticed at the far end — a sudden burst after a quiet
    /// period, which is what an automated abuse filter is built to look for.
    /// </remarks>
    public double MaxRequestsPerSecond { get; set; } = 2;

    /// <summary>How many requests may be in flight at once. Two by default.</summary>
    /// <remarks>
    /// <para>
    /// Separate from the rate because they bound different things. The rate bounds how often a request
    /// <i>starts</i>; this bounds how many are open, which is what decides how long the pacer's queue can
    /// get and therefore how much of a per-attempt timeout is spent waiting rather than fetching. At the
    /// defaults the wait can never exceed one second.
    /// </para>
    /// <para>
    /// It is also the ceiling on the fetch loop's parallelism, and it is enforced in the message handler
    /// rather than only in the loop on purpose: a loop is one caller, and a second caller added later — a
    /// reconciliation pass, a one-off backfill — would otherwise double the load on EPA with nothing in
    /// configuration changing.
    /// </para>
    /// </remarks>
    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>How many times a transient failure is retried before the outcome is reported. Three.</summary>
    /// <remarks>
    /// The package default, kept, and kept deliberately: the retries that matter here are <c>429</c>,
    /// <c>5xx</c> and a dropped connection, and a fourth attempt at a service that has failed three times
    /// with backoff is far more likely to be prolonging an outage than to be finding a window. What makes
    /// an overnight load survive a bad hour is <c>RCRAInfoLoad:WindowDays</c> and
    /// <c>logs.HandlerLoadStatus</c>, not a longer retry ladder.
    /// </remarks>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>The base delay before the first retry. Two seconds.</summary>
    /// <remarks>
    /// Exponential with jitter from here — 2s, 4s, 8s, each randomised. Jitter is not cosmetic even for a
    /// single-process loader: at <see cref="MaxConcurrentRequests"/> above one, a service failure fails
    /// every in-flight request at the same instant, and un-jittered backoff would then re-send them
    /// together, forever, in step.
    /// </remarks>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The ceiling on any single retry delay. Thirty seconds.</summary>
    /// <remarks>
    /// <para>
    /// Applies to the generated backoff. Whether it also clamps a <c>Retry-After</c> header depends on the
    /// resilience package's version, and the uncovered case is deliberately left uncovered: if EPA's own
    /// header asks for longer than this, honouring it verbatim is following an instruction from the service
    /// rather than a defect. What must not happen is the reverse — this loader inventing a delay longer than
    /// EPA asked for and calling it politeness.
    /// </para>
    /// <para>
    /// A long wait is not lost work. The journal keeps buffering, the run stays alive, and
    /// <c>LoadJournal</c>'s remarks note that a long <c>Retry-After</c> is exactly the case where rows sit
    /// buffered — bounded by the wait, and flushed unconditionally on exit.
    /// </para>
    /// </remarks>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long one attempt may take, including its share of the pacer's queue. Thirty seconds.
    /// </summary>
    /// <remarks>
    /// Matches <see cref="RcraInfoApiOptions.RequestTimeout"/>, which is the auth client's
    /// <c>HttpClient.Timeout</c>; the data client has none of its own because the resilience pipeline owns
    /// per-attempt timeouts for it (see <c>ApiServiceCollectionExtensions</c>). The two are separate
    /// settings rather than one because a summaries window is a much larger response than a token, and F2
    /// may well need to move this without touching that.
    /// </remarks>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one logical request may take across all its attempts. Two minutes.</summary>
    /// <remarks>
    /// Must exceed <see cref="AttemptTimeout"/>, and by enough to be worth having: three attempts at 30
    /// seconds plus 2s + 4s of backoff is about 96 seconds, so two minutes covers the ladder this
    /// configuration actually describes. Set it below that and the total timeout silently becomes the retry
    /// limit — the attempts stop happening and nothing says the setting is why.
    /// </remarks>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The interval between two requests at <see cref="MaxRequestsPerSecond"/>.</summary>
    /// <remarks>
    /// Computed from ticks rather than from seconds so that a fractional rate — 0.5 requests/second, which
    /// is what a measured <c>429</c> problem would call for — is exact rather than rounded to nothing.
    /// </remarks>
    public TimeSpan RequestInterval =>
        MaxRequestsPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)Math.Ceiling(TimeSpan.TicksPerSecond / MaxRequestsPerSecond));

    /// <summary>Validates everything, for a fail-at-startup check.</summary>
    /// <returns>The problems found, empty when the options are usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (MaxRequestsPerSecond <= 0 || double.IsNaN(MaxRequestsPerSecond)
            || double.IsInfinity(MaxRequestsPerSecond))
        {
            // No "0 means unlimited" reading. That convention makes the most dangerous configuration the
            // one that looks like an absent value, and this is the setting that decides whether an
            // unattended overnight run looks like a load or like an attack.
            problems.Add(
                $"{SectionName}:{nameof(MaxRequestsPerSecond)} must be a positive number. It was "
                + FormattableString.Invariant($"{MaxRequestsPerSecond}")
                + ". There is no 'unlimited' value: EPA publishes no rate limit (G21), so the only "
                + "defensible setting is one somebody chose.");
        }

        if (MaxConcurrentRequests < 1)
        {
            problems.Add(
                $"{SectionName}:{nameof(MaxConcurrentRequests)} must be at least 1. It was "
                + $"{MaxConcurrentRequests}, which would let no request through at all.");
        }

        if (MaxRetryAttempts < 0)
        {
            problems.Add(
                $"{SectionName}:{nameof(MaxRetryAttempts)} cannot be negative. It was "
                + $"{MaxRetryAttempts}. Zero is legal and means one attempt with no retry.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:{nameof(RetryDelay)} cannot be negative.");
        }

        if (MaxRetryDelay < RetryDelay)
        {
            problems.Add(
                $"{SectionName}:{nameof(MaxRetryDelay)} ({MaxRetryDelay}) is shorter than "
                + $"{nameof(RetryDelay)} ({RetryDelay}), which would cap the first retry below its own "
                + "base delay.");
        }

        if (AttemptTimeout <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:{nameof(AttemptTimeout)} must be greater than zero.");
        }

        if (TotalRequestTimeout <= AttemptTimeout)
        {
            // The resilience handler validates this itself and fails at start-up; saying it here says WHY,
            // which the package's message does not.
            problems.Add(
                $"{SectionName}:{nameof(TotalRequestTimeout)} ({TotalRequestTimeout}) must be longer than "
                + $"{nameof(AttemptTimeout)} ({AttemptTimeout}). Otherwise the total timeout becomes the "
                + "real retry limit and the configured attempts silently stop happening.");
        }

        return problems;
    }

    /// <summary>The options as a single line for a startup log. Contains no secret; there is none here.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {{ MaxRequestsPerSecond = {1}, MaxConcurrentRequests = {2}, RequestInterval = {3}, "
            + "MaxRetryAttempts = {4}, RetryDelay = {5}, MaxRetryDelay = {6}, AttemptTimeout = {7}, "
            + "TotalRequestTimeout = {8} }}",
            SectionName,
            MaxRequestsPerSecond,
            MaxConcurrentRequests,
            RequestInterval,
            MaxRetryAttempts,
            RetryDelay,
            MaxRetryDelay,
            AttemptTimeout,
            TotalRequestTimeout);
}
