namespace RCRAInfo.Loader.Load;

/// <summary>
/// One scheduled retrieval, end to end: lookups, walk, resume, fetch, merge, watermark, close.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="RunAsync"/> takes nothing but a cancellation token.</b> Everything a scheduled run is scoped
/// to comes from configuration (<see cref="LoadRunOptions"/>) and from <c>config.LoadWatermark</c>, which is
/// what makes an unattended 2am invocation and a developer's invocation the same call. A method taking a date
/// range would put the decision that governs whether a day is ever asked for again into a command line.
/// </para>
/// <para>
/// <b><see cref="RunTargetedAsync"/> takes a handler and does not break that rule.</b> It is the second
/// method because an operator genuinely needs to ask about one site now, and it is safe to expose because
/// what it takes is a <i>handler identifier</i> rather than a date range: it never reads the watermark, never
/// moves it, and asks the summaries endpoint's <c>handlerId</c> form, which has no dates for it to claim to
/// have covered. The line the first paragraph draws is about what a command line may decide, and a command
/// line may decide which handler to look at.
/// </para>
/// <para>
/// <b>It does not throw for a load that went wrong.</b> A failed run is a
/// <see cref="LoadRunResult"/> with an outcome, because the failure has to be recorded in
/// <c>logs.LoadRun</c> before the process can exit, and an exception escaping the orchestrator is a run
/// left at <c>Running</c> — indistinguishable from one still going until the abandonment sweep. What does
/// escape is a defect in this loader rather than in the load: unusable configuration, and a database that
/// will not accept the terminal write.
/// </para>
/// </remarks>
public interface ILoadRun
{
    /// <summary>Runs one load.</summary>
    /// <param name="cancellationToken">
    /// Cancels the fetching. It does <b>not</b> cancel the flush or the run's closing write — both are
    /// issued on <see cref="CancellationToken.None"/>, because a cancelled token that also cancels the
    /// record of the cancellation loses the buffered status rows and leaves the run row at
    /// <c>Running</c>.
    /// </param>
    /// <returns>What happened, at the granularity an exit code needs.</returns>
    Task<LoadRunResult> RunAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches one handler — the latest record, or its entire history.</summary>
    /// <param name="request">Which handler, and how much of it. Validated here as well as by the caller.</param>
    /// <param name="cancellationToken">
    /// Same contract as <see cref="RunAsync"/>: it cancels the fetching and never the flush or the run's
    /// closing write.
    /// </param>
    /// <returns>What happened, in the same shape a scheduled run reports.</returns>
    /// <exception cref="InvalidOperationException">
    /// The configuration or the request is unusable. Thrown rather than reported, for
    /// <see cref="RunAsync"/>'s reason — nothing was attempted, so there is no run to record it on.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Three stages of a scheduled run are deliberately absent, and each absence is a decision.</b> No
    /// <b>watermark</b> is read or moved, because this run covers no date range and a bookmark it moved would
    /// declare days loaded that nobody asked for. No <b>lookup refresh</b>, because nothing in this database
    /// has a foreign key to a lookup table (G15's decision), so a code EPA added this morning cannot fail the
    /// merge — and twenty-four requests to answer a question about one handler is the wrong trade. No
    /// <b>resume</b>, because an operator asking for a handler now wants it now: <c>LoadResumePlan.FetchAll</c>
    /// skips nothing, so a version a previous run already succeeded on is fetched again and merges as
    /// <c>Unchanged</c>.
    /// </para>
    /// <para>
    /// <b>It reports the same <see cref="LoadRunResult"/> and can therefore end as
    /// <see cref="LoadRunOutcome.Succeeded"/>, which is worth saying out loud.</b> A targeted run's success is
    /// not a claim about the mirror as a whole; the exit code is the same one a scheduled run uses, so a Task
    /// Scheduler action that acquired <c>--handler-id</c> by accident would report a healthy nightly load
    /// while loading one site. That is why the switch has no default and why the console names the handler and
    /// the scope on every run.
    /// </para>
    /// </remarks>
    Task<LoadRunResult> RunTargetedAsync(
        TargetedLoadRequest request,
        CancellationToken cancellationToken = default);
}
