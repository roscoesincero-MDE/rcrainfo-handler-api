using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Calls the three RCRAInfo Handler data endpoints and turns every possible answer into one
/// <see cref="ApiFetchResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The difference from <see cref="RcraInfoAuthClient"/> is where the secret is, and it changes what
/// this class may report.</b> The auth call carries the API ID and Key <i>in its path</i>, so nothing
/// derived from its URI or its body may be reported at all. These calls carry a bearer token in a header
/// and handler identifiers in the path or query string. A handler id is not a secret — script 506 says so
/// explicitly — so this client can afford to be specific about what it asked for, and
/// <c>logs.HandlerLoadAttempt</c> is designed for exactly that. What it still may not do is put the query
/// string in the log (AR8) or the bearer token in a message.
/// </para>
/// <para>
/// <b>EPA's error prose is carried here and dropped by the auth client, and the asymmetry is deliberate
/// rather than an oversight.</b> <c>logs.HandlerLoadAttempt.ApiErrorMessage</c> exists for it — its
/// description says <i>"stored as received"</i> — and it is the column an operator reads to find out what
/// EPA actually objected to. The hazard the auth client guards against does not apply: the documented leak
/// is a gateway echoing a request path it could not route, and for a data call that path holds handler
/// identifiers. Script 524 keeps the backstop anyway, replacing any message that names the auth path, and
/// the column is excluded from <c>@KeyParameters</c> by name so it never reaches
/// <c>logs.ExecutionLog</c>.
/// </para>
/// <para>
/// <b>What this class does not have is the token</b>, because <see cref="ApiTokenHandler"/> attaches it
/// further down the pipeline. So there is no <c>EnsureNoCredential</c> here of the kind the auth client
/// ends with: the value to check against is not in scope, by construction. That is the argument for the
/// arrangement rather than a gap in it — the credential is reachable in exactly one class, and it is the
/// one that cannot log.
/// </para>
/// <para>
/// <b>Nothing here retries and nothing here throws for a failed call.</b> The resilience handler on the
/// <c>rcrainfo-data</c> registration owns transient retries; whatever it gives up on is classified and
/// returned. See <see cref="IRcraInfoDataClient"/> on why a fetch loop cannot be built out of exceptions.
/// </para>
/// </remarks>
public sealed class RcraInfoDataClient : IRcraInfoDataClient
{
    /// <summary>
    /// The largest <c>Retry-After</c> this client will report: one hour.
    /// </summary>
    /// <remarks>
    /// The column is an <c>INT</c> of seconds, so an absurd value would store fine and then be obeyed. A
    /// <c>Retry-After</c> of a week is a gateway misconfiguration, not an instruction — and a scheduled
    /// overnight load that honoured it would sleep past its window and report nothing. Capped rather than
    /// discarded: the fact that EPA asked for a long wait is worth recording.
    /// </remarks>
    private const int MaxRetryAfterSeconds = 3600;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly TimeProvider clock;

    /// <summary>Creates the client.</summary>
    /// <param name="client">
    /// The <c>rcrainfo-data</c> client. Its <c>BaseAddress</c> must be set and must end in a slash, and it
    /// must have no logging attached — stock <c>IHttpClientFactory</c> logging writes request headers at
    /// <c>Trace</c>, which for this client is <c>Authorization: Bearer …</c>.
    /// </param>
    /// <param name="clock">The clock, so timings and an HTTP-date <c>Retry-After</c> are testable.</param>
    public RcraInfoDataClient(HttpClient client, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);

        if (client.BaseAddress is null)
        {
            throw new ArgumentException(
                "The HttpClient has no BaseAddress, so every data path would resolve against nothing. Set "
                + $"it from {RcraInfoApiOptions.SectionName}:BaseAddress.",
                nameof(client));
        }

        this.client = client;
        this.clock = clock;
    }

    /// <inheritdoc/>
    public async Task<ApiFetchResult> FetchAsync(
        RcraInfoDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset started = clock.GetUtcNow();
        long ticks = Stopwatch.GetTimestamp();

        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(request.RelativeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return await ClassifyAsync(request, response, started, ticks, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RcraInfoAuthException error)
        {
            // The call never went out: ApiTokenHandler needed a bearer token and RcraInfoTokenProvider
            // could not get one, so it threw from inside the pipeline. Classified rather than allowed to
            // propagate, for two reasons. It is the one failure whose cause is upstream of the request, so
            // the attempt log would otherwise have no row at all for a version the run definitely tried;
            // and the remedy differs by auth outcome in exactly the way ApiFetchOutcome already encodes --
            // a rejected credential stops the run, an unreachable auth endpoint does not.
            return Failed(
                request,
                FromAuthFailure(error.Outcome),
                started,
                ticks,
                status: error.StatusCode,
                failureMessage:
                    "No bearer token could be obtained, so this request was never sent. "
                    + error.Message,
                errorCode: error.ErrorCode);
        }
        catch (HttpRequestException error)
        {
            // HttpRequestError, not the message. Measured on the auth call and the same stack applies:
            // these messages carry host and port rather than the path. But "measured" is not "guaranteed
            // for every future error kind", and the enum names the cause without touching the URI at all.
            return Failed(
                request,
                ApiFetchOutcome.Unreachable,
                started,
                ticks,
                failureMessage:
                    $"EPA's RCRAInfo service could not be reached ({error.HttpRequestError}). Check network "
                    + "connectivity, DNS and any outbound proxy from the machine running the scheduled "
                    + "task. This is retryable and says nothing about the record requested.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled. Ordered before the timeout case because only the token tells the two
            // apart, and this one must not be reported as a failure: it projects to Skipped, which leaves
            // the version eligible for the next run.
            return Failed(
                request,
                ApiFetchOutcome.Cancelled,
                started,
                ticks,
                failureMessage: null);
        }
        catch (OperationCanceledException)
        {
            // The request timed out; the caller did not cancel.
            return Failed(
                request,
                ApiFetchOutcome.TimedOut,
                started,
                ticks,
                failureMessage:
                    "EPA's RCRAInfo service did not answer within the time allowed for one attempt. A run "
                    + "whose failures are timeouts has found EPA's throughput limit without being told "
                    + "about it (G21), so the count of these is worth reading before the individual rows.");
        }
    }

    /// <summary>Maps an auth failure onto the fetch outcome with the same remedy.</summary>
    /// <remarks>
    /// <para>
    /// Two enums meet here, and the mapping is not the identity even where the names line up.
    /// <see cref="ApiAuthOutcome.InvalidCredentials"/> becomes
    /// <see cref="ApiFetchOutcome.Unauthorized"/>: EPA rejected the API ID and Key at the auth endpoint,
    /// which reaches this class as "this request had no token", and both mean the credential needs
    /// re-seeding and the run must stop.
    /// </para>
    /// <para>
    /// <b><see cref="ApiAuthOutcome.ServiceFailure"/> becomes
    /// <see cref="ApiFetchOutcome.ServiceFailure"/> and stays retryable, which is the case worth being
    /// careful about.</b> The reflexive mapping is to make every auth failure fatal — a load with no token
    /// fetches nothing, after all — and it would turn one 500 from EPA's auth endpoint into an abandoned
    /// night's load. The distinction the auth client already drew is the one to preserve: a rejected
    /// credential stays rejected, an unwell service does not.
    /// </para>
    /// </remarks>
    // No default arm, and CS8524 suppressed for the same reason ApiFetchOutcomeExtensions gives: a `_` here
    // would silence CS8509, which is the diagnostic that catches an ApiAuthOutcome nobody mapped.
#pragma warning disable CS8524
    private static ApiFetchOutcome FromAuthFailure(ApiAuthOutcome outcome) =>
        outcome switch
        {
            ApiAuthOutcome.InvalidCredentials => ApiFetchOutcome.Unauthorized,
            ApiAuthOutcome.AccessDenied => ApiFetchOutcome.AccessDenied,
            ApiAuthOutcome.ServiceFailure => ApiFetchOutcome.ServiceFailure,
            ApiAuthOutcome.Unreachable => ApiFetchOutcome.Unreachable,

            // Succeeded is unreachable -- a succeeded auth does not throw -- and is listed rather than
            // folded into a default so that CS8509 still fires if ApiAuthOutcome grows a value.
            ApiAuthOutcome.Succeeded => ApiFetchOutcome.Unexpected,
            ApiAuthOutcome.Unexpected => ApiFetchOutcome.Unexpected,
        };
#pragma warning restore CS8524

    private async Task<ApiFetchResult> ClassifyAsync(
        RcraInfoDataRequest request,
        HttpResponseMessage response,
        DateTimeOffset started,
        long ticks,
        CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return await ReadPayloadAsync(request, response, started, ticks, cancellationToken)
                .ConfigureAwait(false);
        }

        ApiError? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        int? retryAfter = ReadRetryAfter(response);

        // The status is what drives this, not ApiError.code. The spec marks the code required, and a
        // gateway that never reaches EPA's application will not send one at all -- while the status always
        // arrives. Same ordering as the auth client, for the same reason.
        ApiFetchOutcome outcome = response.StatusCode switch
        {
            HttpStatusCode.NotFound => request.DocumentsNotFound
                ? ApiFetchOutcome.NotFound
                : ApiFetchOutcome.Unexpected,

            HttpStatusCode.BadRequest => ApiFetchOutcome.BadRequest,
            HttpStatusCode.Unauthorized => ApiFetchOutcome.Unauthorized,
            HttpStatusCode.Forbidden => ApiFetchOutcome.AccessDenied,
            HttpStatusCode.TooManyRequests => ApiFetchOutcome.Throttled,
            HttpStatusCode.RequestTimeout => ApiFetchOutcome.ServiceFailure,

            _ when status >= 500 => ApiFetchOutcome.ServiceFailure,
            _ => ApiFetchOutcome.Unexpected,
        };

        return new ApiFetchResult
        {
            Outcome = outcome,
            Request = request,
            StartedDateUtc = started,
            CompletedDateUtc = clock.GetUtcNow(),
            DurationMs = Elapsed(ticks),
            HttpStatusCode = status,
            RetryAfterSeconds = retryAfter,
            ApiErrorCode = error?.Code,
            ApiErrorMessage = error?.Message,
            ApiErrorId = error?.ErrorId,
            ApiErrorDate = error?.ErrorDate,
            FailureMessage = Describe(request, outcome, status, retryAfter),
        };
    }

    /// <summary>
    /// A composed explanation of a failure, in this project's words. Never EPA's, never an exception's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One per outcome, and each says what to do rather than restating the status code the row already
    /// carries. The three fatal ones name the remedy and say plainly that the run stops, because the
    /// operator reading them at 7am has a load that produced nothing and needs to know whether to wait or
    /// to act.
    /// </para>
    /// <para>
    /// The <see cref="ApiFetchOutcome.Unexpected"/> message is the one worth reading twice: the
    /// undocumented-<c>404</c> case is not phrased as "record missing", because it is not. It is the spec
    /// and the service disagreeing, and the most likely cause is this client.
    /// </para>
    /// </remarks>
    private static string Describe(
        RcraInfoDataRequest request,
        ApiFetchOutcome outcome,
        int status,
        int? retryAfter)
    {
        string endpoint = request.Endpoint.ToString();

        return outcome switch
        {
            ApiFetchOutcome.NotFound =>
                $"EPA does not have this record ({status} from {endpoint}, which documents 404). This is an "
                + "answer rather than a failure: it is the deletion signal AR7 uses, because whether the "
                + "summaries feed reports deletions at all is still open (G23).",

            ApiFetchOutcome.BadRequest =>
                $"EPA rejected the request as malformed (400 from {endpoint}). This is a defect in this "
                + "application, not a data condition, and every request built the same way will be "
                + "rejected the same way -- so the run stops rather than sending it several hundred "
                + "thousand more times.",

            ApiFetchOutcome.Unauthorized =>
                "EPA rejected the bearer token (401). A fresh token has already been fetched and retried "
                + "once, so this is not retried again: the credential itself needs re-seeding (plan §4.2). "
                + "The run stops.",

            ApiFetchOutcome.AccessDenied =>
                $"EPA accepted the token and refused the request (403 from {endpoint}). This is a "
                + "permissions or scope problem -- RCRAInfo grants API access by state and region (G2) -- "
                + "and re-seeding the credential will not change it. The run stops; ask EPA to widen the "
                + "account's scope.",

            ApiFetchOutcome.Throttled => retryAfter is null
                ? "EPA asked this client to slow down (429) and sent no Retry-After, so the wait is this "
                  + "application's to choose (G21)."
                : $"EPA asked this client to slow down (429) and to wait {retryAfter.Value.ToString(CultureInfo.InvariantCulture)} "
                  + "seconds. That figure is EPA's own answer to G21 for this moment and is worth tuning "
                  + "the run's concurrency from.",

            ApiFetchOutcome.ServiceFailure =>
                $"EPA's RCRAInfo service failed ({status}). Retryable, and nothing about the credential or "
                + "the record requested is implied by it.",

            _ => request.DocumentsNotFound || status != (int)HttpStatusCode.NotFound
                ? $"EPA answered {status}, which the pinned spec does not document for {endpoint}. Retrying "
                  + "will not change it. Either the service has changed or this request is not reaching it "
                  + $"-- confirm {RcraInfoApiOptions.SectionName}:BaseAddress ends with the service's "
                  + "/rcra-api/rest path."
                : $"EPA answered 404, and {endpoint} documents no 404 at all -- it answers 200 with an "
                  + "empty array when there is nothing to return. So this is NOT an absent record and must "
                  + "not be treated as a deletion: the request reached something that has no such route, "
                  + "which means this client built it wrongly or a gateway is answering in EPA's place.",
        };
    }

    /// <summary>Reads a 200 body, and refuses one that is not the JSON this application asked for.</summary>
    /// <remarks>
    /// <para>
    /// <b>The body is checked to be JSON-shaped before it is accepted, and that check earns its place.</b>
    /// The most likely wrong answer to these requests is not malformed JSON — it is a login page, a proxy
    /// notice or a portal redirect served with status 200, which is precisely what a
    /// <c>BaseAddress</c> pointing at a web front end produces. Accepting it would hand several kilobytes
    /// of HTML to the shredding procedures, which would find no elements and report a successful load of
    /// nothing.
    /// </para>
    /// <para>
    /// The check is a first-character test rather than a parse. Parsing here would deserialize 377 fields
    /// only to discard the result — the payload is handed to T-SQL as text and shredded there — and would
    /// spend that on every one of several hundred thousand responses to catch a misconfiguration that
    /// announces itself on the first one.
    /// </para>
    /// </remarks>
    private async Task<ApiFetchResult> ReadPayloadAsync(
        RcraInfoDataRequest request,
        HttpResponseMessage response,
        DateTimeOffset started,
        long ticks,
        CancellationToken cancellationToken)
    {
        string body;

        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            // The headers arrived and the body did not: a connection dropped mid-stream, or a length that
            // does not match. Retryable, and distinct from a body that arrived and would not parse.
            return Failed(
                request,
                ApiFetchOutcome.Unreachable,
                started,
                ticks,
                status: (int)response.StatusCode,
                failureMessage:
                    "EPA answered 200 and the response body did not finish arriving "
                    + $"({error.HttpRequestError}). Retryable.");
        }

        // The decoded size, not the transfer size: this is compared against what the payload should be, so
        // a compressed byte count would make an empty 200 and a small one look alike. ResponseBytes exists
        // to spot a truncated or empty success, which is a question about the JSON and not the wire.
        int bytes = Encoding.UTF8.GetByteCount(body);

        string trimmed = body.TrimStart();

        if (trimmed.Length == 0)
        {
            return Failed(
                request,
                ApiFetchOutcome.Unexpected,
                started,
                ticks,
                status: (int)response.StatusCode,
                bytes: bytes,
                failureMessage:
                    "EPA answered 200 with an empty body. An endpoint with nothing to return answers with "
                    + "an empty JSON array, so this is a failure rather than an empty result -- treating it "
                    + "as one would report a successful load of no records.");
        }

        if (trimmed[0] is not ('{' or '['))
        {
            return Failed(
                request,
                ApiFetchOutcome.Unexpected,
                started,
                ticks,
                status: (int)response.StatusCode,
                bytes: bytes,
                failureMessage:
                    $"EPA answered 200 with a body that is not JSON (it begins '{trimmed[0]}', and "
                    + $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes arrived). The body is not "
                    + "quoted here. The usual cause is a base address pointing at a web front end rather "
                    + $"than the REST root: confirm {RcraInfoApiOptions.SectionName}:BaseAddress ends with "
                    + "/rcra-api/rest.");
        }

        return new ApiFetchResult
        {
            Outcome = ApiFetchOutcome.Succeeded,
            Request = request,
            StartedDateUtc = started,
            CompletedDateUtc = clock.GetUtcNow(),
            DurationMs = Elapsed(ticks),
            Payload = body,
            HttpStatusCode = (int)response.StatusCode,
            ResponseBytes = bytes,
        };
    }

    private static async Task<ApiError?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<ApiError>(RcraInfoJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A non-JSON error body is ordinary: a proxy, a gateway, an HTML page. There is nothing
            // structured to take from it, and the status is already known.
            return null;
        }
        catch (NotSupportedException)
        {
            // An unreadable or absent content type. Same reasoning.
            return null;
        }
        catch (HttpRequestException)
        {
            // The error body itself did not finish arriving. The status is what matters and it is already
            // in hand, so this is not worth turning into a different outcome.
            return null;
        }
    }

    /// <summary>Reads <c>Retry-After</c> in either of the two forms RFC 9110 allows.</summary>
    /// <remarks>
    /// <b>Both forms are handled because a gateway chooses, not EPA.</b> The header may be a delta in
    /// seconds or an HTTP-date, and a 429 from a load balancer in front of RCRAInfo is as likely as one
    /// from the application. Reading only <c>Delta</c> — which is the shape of the field most code touches
    /// — would silently drop the instruction and leave G21 unanswered while EPA was answering it. A date
    /// already in the past becomes <c>0</c>, meaning "no wait required", rather than a negative number the
    /// column would happily store.
    /// </remarks>
    private int? ReadRetryAfter(HttpResponseMessage response)
    {
        System.Net.Http.Headers.RetryConditionHeaderValue? header = response.Headers.RetryAfter;

        if (header is null)
        {
            return null;
        }

        double seconds;

        if (header.Delta is { } delta)
        {
            seconds = delta.TotalSeconds;
        }
        else if (header.Date is { } date)
        {
            seconds = (date - clock.GetUtcNow()).TotalSeconds;
        }
        else
        {
            return null;
        }

        return (int)Math.Clamp(Math.Ceiling(seconds), 0, MaxRetryAfterSeconds);
    }

    private ApiFetchResult Failed(
        RcraInfoDataRequest request,
        ApiFetchOutcome outcome,
        DateTimeOffset started,
        long ticks,
        string? failureMessage,
        int? status = null,
        int? bytes = null,
        string? errorCode = null) =>
        new()
        {
            Outcome = outcome,
            Request = request,
            StartedDateUtc = started,
            CompletedDateUtc = clock.GetUtcNow(),
            DurationMs = Elapsed(ticks),
            HttpStatusCode = status,
            ResponseBytes = bytes,
            ApiErrorCode = errorCode,
            FailureMessage = failureMessage,
        };

    /// <summary>
    /// Elapsed milliseconds from a <see cref="Stopwatch"/> timestamp, never negative.
    /// </summary>
    /// <remarks>
    /// A <see cref="Stopwatch"/> and not the difference between the two <see cref="TimeProvider"/> stamps,
    /// because script 524 only derives <c>DurationMs</c> from those when it is omitted and says a stopwatch
    /// beats a <c>DATETIME2</c> subtraction. The clamp is for the procedure's benefit: it refuses a
    /// negative duration outright, and refusing a flush over a rounding artefact would discard every
    /// attempt buffered behind it.
    /// </remarks>
    private static int Elapsed(long ticks) =>
        (int)Math.Clamp(Stopwatch.GetElapsedTime(ticks).TotalMilliseconds, 0, int.MaxValue);
}
