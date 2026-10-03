namespace RCRAInfo.Loader.Api;

/// <summary>
/// The four questions every <see cref="ApiFetchOutcome"/> has to answer: may it be retried, does it end
/// the run, and what does it become in <c>logs.HandlerLoadAttempt.Outcome</c> and
/// <c>logs.HandlerLoadStatus.Status</c>.
/// </summary>
/// <remarks>
/// <para>
/// All four are written as <c>switch</c> expressions listing <b>every named value with no default
/// arm</b>. That is the point of putting them here rather than at the call sites: adding a value to
/// <see cref="ApiFetchOutcome"/> without deciding all four things is <c>CS8509</c> — a warning by
/// default, an error under this solution's <c>TreatWarningsAsErrors</c> — instead of a value that is
/// silently non-retryable, silently non-fatal, and silently logged as <c>Failed</c>.
/// </para>
/// <para>
/// <c>CS8524</c> is suppressed below, and it is a different diagnostic from the one being relied on.
/// It fires on the <i>unnamed</i> case — a value produced by casting an integer, such as
/// <c>(ApiFetchOutcome) 11</c> — and it fires even when every named value is covered, so satisfying it
/// would mean adding a <c>_</c> arm, which is exactly what would stop <c>CS8509</c> from ever firing
/// again. Suppressing the weaker guard is what keeps the stronger one: nothing in this solution casts
/// an integer to this type, and a new member of the enum is the failure actually worth catching.
/// </para>
/// </remarks>
public static class ApiFetchOutcomeExtensions
{
    // See the remarks on this type: CS8524 is the unnamed-value case, and satisfying it would
    // require a `_` arm that would permanently silence CS8509 -- the diagnostic that actually
    // catches a new ApiFetchOutcome nobody classified.
#pragma warning disable CS8524
    /// <summary>Whether calling again could plausibly produce a different answer.</summary>
    /// <param name="outcome">The outcome to classify.</param>
    /// <returns>Whether the caller may retry.</returns>
    /// <remarks>
    /// <para>
    /// <b>Four of the eleven are retryable, and the interesting decisions are the refusals.</b>
    /// <see cref="ApiFetchOutcome.Unauthorized"/> is not retryable even though a token problem sounds like
    /// the most retryable thing here — <c>ApiTokenHandler</c> has already retried once with a fresh token
    /// by the time this value exists, so a second attempt repeats work that just failed.
    /// <see cref="ApiFetchOutcome.NotFound"/> is not retryable because it is an <i>answer</i>, not a
    /// failure. <see cref="ApiFetchOutcome.BadRequest"/> and <see cref="ApiFetchOutcome.AccessDenied"/> are
    /// not retryable because the defect is on this side or in the account's scope, and both are worse than
    /// useless when repeated across a whole population.
    /// </para>
    /// <para>
    /// <b>Retryable is not the same as "the run continues".</b> This method answers only whether one more
    /// attempt is worth making at this handler. Whether a non-retryable outcome ends the run is the
    /// orchestrator's decision and a different question: <see cref="ApiFetchOutcome.NotFound"/> is
    /// non-retryable and ordinary, while <see cref="ApiFetchOutcome.AccessDenied"/> is non-retryable and
    /// fatal.
    /// </para>
    /// </remarks>
    public static bool IsRetryable(this ApiFetchOutcome outcome) =>
        outcome switch
        {
            ApiFetchOutcome.Throttled => true,
            ApiFetchOutcome.ServiceFailure => true,
            ApiFetchOutcome.Unreachable => true,
            ApiFetchOutcome.TimedOut => true,

            ApiFetchOutcome.Succeeded => false,
            ApiFetchOutcome.NotFound => false,
            ApiFetchOutcome.BadRequest => false,
            ApiFetchOutcome.Unauthorized => false,
            ApiFetchOutcome.AccessDenied => false,
            ApiFetchOutcome.Cancelled => false,
            ApiFetchOutcome.Unexpected => false,
        };

    /// <summary>
    /// Whether this outcome should stop the whole run rather than mark one handler and move on.
    /// </summary>
    /// <param name="outcome">The outcome to classify.</param>
    /// <returns>Whether the run cannot usefully continue.</returns>
    /// <remarks>
    /// <b>The reason this is a separate question from <see cref="IsRetryable"/>: a per-handler loop turns a
    /// systemic failure into a sustained one.</b> An account without Maryland scope, or a client building
    /// malformed requests, fails identically on every one of several hundred thousand handlers — and the
    /// reflexive shape for a fetch loop is <c>catch</c>, record, continue, which would send all of them.
    /// Both of these are also the two outcomes whose remedy needs a person, so continuing buys nothing and
    /// costs EPA a great deal.
    /// </remarks>
    public static bool IsFatalToTheRun(this ApiFetchOutcome outcome) =>
        outcome switch
        {
            ApiFetchOutcome.BadRequest => true,
            ApiFetchOutcome.AccessDenied => true,
            ApiFetchOutcome.Unauthorized => true,

            ApiFetchOutcome.Succeeded => false,
            ApiFetchOutcome.NotFound => false,
            ApiFetchOutcome.Throttled => false,
            ApiFetchOutcome.ServiceFailure => false,
            ApiFetchOutcome.Unreachable => false,
            ApiFetchOutcome.TimedOut => false,
            ApiFetchOutcome.Cancelled => false,
            ApiFetchOutcome.Unexpected => false,
        };

    /// <summary>
    /// The value <c>logs.HandlerLoadAttempt.Outcome</c> accepts for this outcome.
    /// </summary>
    /// <param name="outcome">The outcome to project.</param>
    /// <returns>One of <c>Succeeded</c>, <c>Failed</c>, <c>Throttled</c>, <c>TimedOut</c>, <c>Cancelled</c>.</returns>
    /// <remarks>
    /// <para>
    /// The domain is closed by <c>CK_logs_HandlerLoadAttempt_Outcome</c> and by
    /// <c>logs.uspRecordHandlerLoadAttemptSet</c>, which refuses anything outside it rather than letting the
    /// constraint reject the row with an engine error naming no element. <b>These strings are the second
    /// copy of that list, and they are strings rather than an enum for the reason
    /// <c>HandlerLoadAttemptElement.Outcome</c> gives</b> — the constraint is the definition, and a C# enum
    /// would be a third copy. What keeps them honest is a database round-trip: <c>AttemptLogTests</c> writes
    /// each of the five and reads it back, so a typo here fails a test rather than a nightly load.
    /// </para>
    /// <para>
    /// <b>Six values collapse to <c>Failed</c>, and nothing is lost, because the row carries more than the
    /// outcome.</b> <see cref="ApiFetchOutcome.NotFound"/>, <see cref="ApiFetchOutcome.BadRequest"/>,
    /// <see cref="ApiFetchOutcome.Unauthorized"/> and <see cref="ApiFetchOutcome.AccessDenied"/> are told
    /// apart in the log by <c>HttpStatusCode</c> (404, 400, 401, 403), and
    /// <see cref="ApiFetchOutcome.ServiceFailure"/>, <see cref="ApiFetchOutcome.Unreachable"/> and
    /// <see cref="ApiFetchOutcome.Unexpected"/> by that plus <c>ApiErrorCode</c>. A reader of the attempt
    /// log therefore still has the distinction; what the column gives up is the ability to <c>GROUP BY</c>
    /// it, which is what <c>logs.uspGetLoadRunSummary</c>'s separate 4xx and 5xx counts exist for.
    /// </para>
    /// </remarks>
    public static string ToAttemptOutcome(this ApiFetchOutcome outcome) =>
        outcome switch
        {
            ApiFetchOutcome.Succeeded => "Succeeded",
            ApiFetchOutcome.Throttled => "Throttled",
            ApiFetchOutcome.TimedOut => "TimedOut",
            ApiFetchOutcome.Cancelled => "Cancelled",

            ApiFetchOutcome.NotFound => "Failed",
            ApiFetchOutcome.BadRequest => "Failed",
            ApiFetchOutcome.Unauthorized => "Failed",
            ApiFetchOutcome.AccessDenied => "Failed",
            ApiFetchOutcome.ServiceFailure => "Failed",
            ApiFetchOutcome.Unreachable => "Failed",
            ApiFetchOutcome.Unexpected => "Failed",
        };

    /// <summary>
    /// The value <c>logs.HandlerLoadStatus.Status</c> takes for a version whose fetch ended this way,
    /// once every retry is spent.
    /// </summary>
    /// <param name="outcome">The outcome to project.</param>
    /// <returns>One of <c>Succeeded</c>, <c>Failed</c>, <c>Skipped</c>.</returns>
    /// <remarks>
    /// <para>
    /// Constrained by <c>CK_logs_HandlerLoadStatus_Status</c> to <c>Pending</c>, <c>InProgress</c>,
    /// <c>Succeeded</c>, <c>Failed</c> and <c>Skipped</c>. The first two are not reachable from here:
    /// <c>Pending</c> is written when the run enumerates its work and <c>InProgress</c> while a fetch is in
    /// flight, so neither describes a finished attempt.
    /// </para>
    /// <para>
    /// <b><see cref="ApiFetchOutcome.Cancelled"/> maps to <c>Skipped</c> and not to <c>Failed</c>, which is
    /// the only judgement in this method.</b> A version the run never got to because the process was
    /// stopped was not attempted and did not fail — recording it as <c>Failed</c> would make an orderly
    /// shutdown indistinguishable from EPA refusing us, and would put a Failed row in front of an operator
    /// who has nothing to fix. <c>Skipped</c> is the value the status table's own description reserves for
    /// "deliberately not attempted", and it leaves the version eligible for the next run.
    /// </para>
    /// <para>
    /// <b><see cref="ApiFetchOutcome.NotFound"/> maps to <c>Succeeded</c>, and that reads wrong until the
    /// grain is right.</b> This column records whether the run <i>dealt with</i> the version, not whether
    /// data arrived. A <c>404</c> is a complete, usable answer that feeds the AR7 soft delete, and a run
    /// that resumes must not re-fetch it — which is exactly what <c>Failed</c> would cause, forever, since
    /// the record will still be absent tomorrow. The distinction stays visible in
    /// <c>logs.HandlerLoadStatus.Outcome</c>, whose <c>SoftDeleted</c> value says what actually happened.
    /// </para>
    /// </remarks>
    public static string ToStatus(this ApiFetchOutcome outcome) =>
        outcome switch
        {
            ApiFetchOutcome.Succeeded => "Succeeded",
            ApiFetchOutcome.NotFound => "Succeeded",
            ApiFetchOutcome.Cancelled => "Skipped",

            ApiFetchOutcome.BadRequest => "Failed",
            ApiFetchOutcome.Unauthorized => "Failed",
            ApiFetchOutcome.AccessDenied => "Failed",
            ApiFetchOutcome.Throttled => "Failed",
            ApiFetchOutcome.ServiceFailure => "Failed",
            ApiFetchOutcome.Unreachable => "Failed",
            ApiFetchOutcome.TimedOut => "Failed",
            ApiFetchOutcome.Unexpected => "Failed",
        };

#pragma warning restore CS8524
}
