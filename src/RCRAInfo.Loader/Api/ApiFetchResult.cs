using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// One call to a RCRAInfo data endpoint, classified, timed, and carrying everything
/// <c>logs.HandlerLoadAttempt</c> needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists so that the fetch and the record of the fetch are produced together.</b> The
/// alternative shape — return the payload, let the caller notice what happened and assemble a log row —
/// puts the timing, the status, the <c>Retry-After</c> and the request path in the caller's hands at the
/// point where it is busy with the data. Attempts are buffered and flushed in batches, so a caller that
/// forgets one field does not find out until an operator reads the table weeks later and finds
/// <c>DurationMs</c> measuring the flush.
/// </para>
/// <para>
/// <see cref="ToAttemptElement"/> is the whole point: it is the one place the AR8 rules are applied, so
/// <see cref="RcraInfoDataRequest.Path"/> reaches the log and
/// <see cref="RcraInfoDataRequest.RelativeUri"/> cannot.
/// </para>
/// <para>
/// <b><see cref="Payload"/> is non-null exactly when <see cref="Outcome"/> is
/// <see cref="ApiFetchOutcome.Succeeded"/></b>, and the client guarantees it. A <c>404</c> is a successful
/// classification with no payload, which is why the two are not the same question.
/// </para>
/// </remarks>
public sealed record ApiFetchResult
{
    /// <summary>How the call ended. The only field that is always meaningful.</summary>
    public required ApiFetchOutcome Outcome { get; init; }

    /// <summary>The request, for the path to log and the endpoint that was called.</summary>
    public required RcraInfoDataRequest Request { get; init; }

    /// <summary>When the request went out.</summary>
    public required DateTimeOffset StartedDateUtc { get; init; }

    /// <summary>When the answer arrived, or when the attempt was given up on.</summary>
    public required DateTimeOffset CompletedDateUtc { get; init; }

    /// <summary>How long the call took, measured rather than subtracted from the two stamps.</summary>
    public required int DurationMs { get; init; }

    /// <summary>
    /// The response body, verbatim, and only for <see cref="ApiFetchOutcome.Succeeded"/>.
    /// </summary>
    /// <remarks>
    /// Kept as text rather than parsed here because the mirror stores EPA's JSON as
    /// <c>NVARCHAR (MAX)</c> and the shredding happens in T-SQL. Parsing it in this class would mean
    /// deserializing 377 fields to re-serialize them, and would put a second definition of the payload
    /// shape beside the one the procedures already hold.
    /// </remarks>
    public string? Payload { get; init; }

    /// <summary>The HTTP status, or null when there was no response at all.</summary>
    /// <remarks>
    /// Null and not <c>0</c>. The column is bounded to 100–599 by script 524, and a <c>0</c> would be
    /// refused there — but more to the point it reads like a status code to whoever queries the table.
    /// </remarks>
    public int? HttpStatusCode { get; init; }

    /// <summary>The size of the decoded body. See <c>RcraInfoDataClient</c> on what this measures.</summary>
    public int? ResponseBytes { get; init; }

    /// <summary>EPA's <c>Retry-After</c>, in seconds, when it sent one.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>EPA's symbolic error code.</summary>
    public string? ApiErrorCode { get; init; }

    /// <summary>EPA's error prose. See <c>RcraInfoDataClient</c> on why this one is carried and the auth client's is not.</summary>
    public string? ApiErrorMessage { get; init; }

    /// <summary>EPA's correlation identifier, which is what their support asks for.</summary>
    public string? ApiErrorId { get; init; }

    /// <summary>When EPA says the error occurred.</summary>
    public DateTimeOffset? ApiErrorDate { get; init; }

    /// <summary>This project's own description, for the failures EPA never described.</summary>
    /// <remarks>Composed, never <c>exception.ToString ()</c> — an HTTP-stack message can carry the URI.</remarks>
    public string? FailureMessage { get; init; }

    /// <summary>Whether the call produced a payload to load.</summary>
    public bool HasPayload => Outcome == ApiFetchOutcome.Succeeded && Payload is not null;

    /// <summary>Whether EPA said this record is gone — the AR7 soft-delete signal.</summary>
    /// <remarks>
    /// Reads the classified outcome and not the status code, so it is false for a <c>404</c> from
    /// <see cref="RcraInfoDataEndpoint.OtherIds"/>, which documents none and where a <c>404</c> means this
    /// client built the request wrongly.
    /// </remarks>
    public bool IsGone => Outcome == ApiFetchOutcome.NotFound;

    /// <summary>Builds the <c>logs.HandlerLoadAttempt</c> row for this call.</summary>
    /// <param name="handlerId">The handler the call was about.</param>
    /// <param name="sourceType">The source-type code.</param>
    /// <param name="sequence">EPA's version sequence.</param>
    /// <param name="attemptNumber">Which attempt this was, counted by the caller from 1.</param>
    /// <returns>The element to buffer for the next flush.</returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="HandlerLoadAttemptElement.RequestPath"/> is set from
    /// <see cref="RcraInfoDataRequest.LogPath"/> here, and this is the only assignment of it in the
    /// solution.</b> That is what makes AR8 a property of the code rather than a rule callers follow:
    /// there is no path by which <see cref="RcraInfoDataRequest.RelativeUri"/> — which for two of the three
    /// endpoints carries a query string — reaches a table the monitoring web application can read.
    /// </para>
    /// <para>
    /// <b><see cref="RcraInfoDataRequest.LogPath"/> and not
    /// <see cref="RcraInfoDataRequest.Path"/></b>, which is the distinction script 524 turns into data:
    /// it accepts a path only if it begins with a slash and <i>replaces the whole value</i> when it does
    /// not, counting it into <c>@ValuesWithheld</c>. Getting this wrong would not throw — it would fill
    /// the attempt log with withheld notices and report a defect count for a load that otherwise worked.
    /// Keeping that count at zero is this method's job.
    /// </para>
    /// <para>
    /// The three key fields are parameters rather than properties of this record because the summaries call
    /// is not about one version — it returns many — so a fetch does not always know a version. Attempts
    /// are logged per version, and the caller is what knows which one it was asking about.
    /// </para>
    /// </remarks>
    public HandlerLoadAttemptElement ToAttemptElement(
        string handlerId,
        string sourceType,
        int sequence,
        int attemptNumber) =>
        new()
        {
            HandlerId = handlerId,
            SourceType = sourceType,
            Sequence = sequence,
            AttemptNumber = attemptNumber,
            StartedDateUtc = StartedDateUtc,
            CompletedDateUtc = CompletedDateUtc,
            DurationMs = DurationMs,
            Outcome = Outcome.ToAttemptOutcome(),
            HttpStatusCode = HttpStatusCode,
            RequestPath = Request.LogPath,
            ResponseBytes = ResponseBytes,
            RetryAfterSeconds = RetryAfterSeconds,
            ApiErrorCode = ApiErrorCode,
            ApiErrorMessage = ApiErrorMessage,
            ApiErrorId = ApiErrorId,
            ApiErrorDate = ApiErrorDate,
            FailureMessage = FailureMessage,
        };

    /// <summary>The outcome, the endpoint and the status. Never the payload and never the URI.</summary>
    /// <returns>A short description safe to put in a message.</returns>
    public override string ToString() =>
        $"{Outcome} from {Request.Path} ({HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no response"}) in {DurationMs} ms";
}
