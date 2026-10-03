using System.Net;
using System.Net.Http.Headers;
using System.Text;

using RCRAInfo.Data.Payloads;
using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The data client's job is classification, and the classification decides whether a load deletes data.
/// </summary>
/// <remarks>
/// Two groups of assertions carry the weight. The first is that a <c>404</c> is read against the
/// <b>endpoint's</b> documented status set, so the one from <c>/hd/other-ids</c> — which documents none —
/// becomes <see cref="ApiFetchOutcome.Unexpected"/> rather than the soft-delete signal. The second is that
/// a <c>200</c> which is not JSON is a failure: a base address pointing at a web front end answers 200 with
/// a login page, and accepting it would report a successful load of nothing.
/// </remarks>
public class RcraInfoDataClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = new(2026, 1, 31);

    private const string Payload = """[{"handlerId":"MDD000000001","sourceType":"N","sequence":1}]""";

    private static readonly RcraInfoDataRequest SourceRequest =
        RcraInfoDataRequest.Source("MDD000000001", "N", 1);

    private static readonly RcraInfoDataRequest OtherIdsRequest =
        RcraInfoDataRequest.OtherIds("MDD000000001");

    private static readonly RcraInfoDataRequest SummariesRequest =
        RcraInfoDataRequest.Summaries("MD", Start, End);

    // ---- Construction ------------------------------------------------------------------------------

    [Fact]
    public void AClientWithNoBaseAddressIsRefusedAtConstruction()
    {
        // Not on the first call, which for a scheduled task at 2am is a whole run later.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RcraInfoDataClient(new HttpClient(), new TestClock(Now)));

        Assert.Contains("BaseAddress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANullRequestIsRefused()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.FetchAsync(null!));
    }

    // ---- Success ----------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulFetchCarriesThePayloadAndItsSize()
    {
        (RcraInfoDataClient client, StubHandler handler) =
            Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(ApiFetchOutcome.Succeeded, result.Outcome);
        Assert.Equal(Payload, result.Payload);
        Assert.True(result.HasPayload);
        Assert.False(result.IsGone);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(Encoding.UTF8.GetByteCount(Payload), result.ResponseBytes);
        Assert.Null(result.FailureMessage);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TheRequestIsSentWithItsQueryStringResolvedAgainstTheBase()
    {
        (RcraInfoDataClient client, StubHandler handler) =
            Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        await client.FetchAsync(OtherIdsRequest);

        Assert.Equal(
            "https://rcranodepreprod.epa.gov/rcra-api/rest/api/v1/hd/other-ids?handlerId=MDD000000001",
            handler.RequestUris[0]);
    }

    [Fact]
    public async Task ASucceededResultCarriesNoErrorFields()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        // Script 524 refuses a Succeeded attempt that carries an error, so these being null is a
        // requirement of the flush and not merely tidy.
        Assert.Null(result.ApiErrorCode);
        Assert.Null(result.ApiErrorMessage);
        Assert.Null(result.ApiErrorId);
        Assert.Null(result.RetryAfterSeconds);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task AnObjectBodyIsAcceptedAsWellAsAnArray()
    {
        (RcraInfoDataClient client, _) =
            Build(StubHandler.Always(HttpStatusCode.OK, """{"handlerId":"MDD000000001"}"""));

        Assert.Equal(ApiFetchOutcome.Succeeded, (await client.FetchAsync(SourceRequest)).Outcome);
    }

    [Fact]
    public async Task LeadingWhitespaceDoesNotMakeAValidBodyLookLikeHtml()
    {
        (RcraInfoDataClient client, _) =
            Build(StubHandler.Always(HttpStatusCode.OK, "\r\n  " + Payload));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(ApiFetchOutcome.Succeeded, result.Outcome);

        // The body is stored verbatim, whitespace included: it is handed to T-SQL as text, and trimming it
        // here would mean the bytes measured are not the bytes kept.
        Assert.Equal("\r\n  " + Payload, result.Payload);
    }

    // ---- A 200 that is not the answer --------------------------------------------------------------

    [Fact]
    public async Task AnHtmlLoginPageServedWithTwoHundredIsAFailure()
    {
        // The most likely wrong answer to these requests, and the one a BaseAddress pointing at a portal
        // produces. Accepting it would hand HTML to the shredding procedures, which would find no elements
        // and report a successful load of nothing.
        (RcraInfoDataClient client, _) = Build(
            StubHandler.Always(HttpStatusCode.OK, "<html><body>Please sign in</body></html>"));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiFetchOutcome.Unexpected, result.Outcome);
        Assert.False(result.HasPayload);
        Assert.Null(result.Payload);
        Assert.Contains("not JSON", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("/rcra-api/rest", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRejectedBodyIsNotQuotedBackInTheMessage()
    {
        (RcraInfoDataClient client, _) = Build(
            StubHandler.Always(HttpStatusCode.OK, "<html>session=abcdef0123456789</html>"));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        // A page served in place of the API can hold a session cookie, a token, or the request it could not
        // route. The first character and the byte count are enough to diagnose it.
        Assert.DoesNotContain("abcdef0123456789", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("begins '<'", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyTwoHundredIsAFailureAndNotAnEmptyResult()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK));

        ApiFetchResult result = await client.FetchAsync(OtherIdsRequest);

        // An endpoint with nothing to return sends an empty JSON array. Nothing at all is a different
        // event, and the distinction is exactly the one other-ids depends on: "no other ids" is [].
        Assert.Equal(ApiFetchOutcome.Unexpected, result.Outcome);
        Assert.Contains("empty body", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Equal(0, result.ResponseBytes);
    }

    // ---- 404, per endpoint ------------------------------------------------------------------------

    [Fact]
    public async Task ANotFoundFromTheDetailEndpointIsTheSoftDeleteSignal()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.NotFound));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(ApiFetchOutcome.NotFound, result.Outcome);
        Assert.True(result.IsGone);
        Assert.Equal("Succeeded", result.Outcome.ToStatus());
        Assert.Contains("G23", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANotFoundFromOtherIdsIsAClientDefectAndNotADeletion()
    {
        // The assertion this whole class exists for. other-ids documents 200, 400, 401, 403 and 500 -- no
        // 404 -- so a 404 there did not come from EPA saying the handler is gone. The plan carried the
        // wrong path form for this endpoint until 2026-09-06; had it shipped, every handler in the state
        // would have answered 404, and reading that as NotFound would have soft deleted all of them.
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.NotFound));

        ApiFetchResult result = await client.FetchAsync(OtherIdsRequest);

        Assert.Equal(ApiFetchOutcome.Unexpected, result.Outcome);
        Assert.False(result.IsGone);
        Assert.Equal(404, result.HttpStatusCode);

        // And the message says so in words, because the operator reading it will otherwise assume the
        // obvious meaning of 404.
        Assert.Contains("NOT an absent record", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("built it wrongly", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANotFoundFromSummariesIsADocumentedAnswer()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.NotFound));

        Assert.Equal(ApiFetchOutcome.NotFound, (await client.FetchAsync(SummariesRequest)).Outcome);
    }

    // ---- The other statuses -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ApiFetchOutcome.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized, ApiFetchOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ApiFetchOutcome.AccessDenied)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiFetchOutcome.Throttled)]
    [InlineData(HttpStatusCode.RequestTimeout, ApiFetchOutcome.ServiceFailure)]
    [InlineData(HttpStatusCode.InternalServerError, ApiFetchOutcome.ServiceFailure)]
    [InlineData(HttpStatusCode.BadGateway, ApiFetchOutcome.ServiceFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiFetchOutcome.ServiceFailure)]
    [InlineData(HttpStatusCode.GatewayTimeout, ApiFetchOutcome.ServiceFailure)]
    public async Task StatusesMapToOutcomes(HttpStatusCode status, ApiFetchOutcome expected)
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(status));

        Assert.Equal(expected, (await client.FetchAsync(SummariesRequest)).Outcome);
    }

    [Fact]
    public async Task A408IsAServiceFailureAndNotATimedOut()
    {
        // ApiFetchOutcome.TimedOut means this client gave up waiting. A 408 means EPA answered -- promptly
        // enough to answer -- and the two want different remedies: one is a concurrency question for this
        // application, the other is EPA's.
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.RequestTimeout));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiFetchOutcome.ServiceFailure, result.Outcome);
        Assert.NotEqual(ApiFetchOutcome.TimedOut, result.Outcome);
        Assert.Equal(408, result.HttpStatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public async Task AStatusTheSpecDoesNotDocumentIsUnexpected(HttpStatusCode status)
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(status));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiFetchOutcome.Unexpected, result.Outcome);
        Assert.Contains("does not document", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadRequestOnTheDetailEndpointIsStillABadRequest()
    {
        // The detail endpoint documents no 400, so this could have been classified Unexpected. It is not:
        // a 400 is EPA naming the defect, and BadRequest is the outcome that stops the run, which is the
        // right response to a request this client is building wrongly. Undocumented-ness only decides the
        // 404 case, where the two readings have opposite consequences for the data.
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.BadRequest));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(ApiFetchOutcome.BadRequest, result.Outcome);
        Assert.True(result.Outcome.IsFatalToTheRun());
    }

    // ---- EPA's error envelope ---------------------------------------------------------------------

    [Fact]
    public async Task EpasErrorCodeAndIdAreCarriedIntoTheResult()
    {
        const string Body = """
            {"code":"E_AccessDenied","message":"Not permitted for MD","errorId":"abc-123",
             "errorDate":"2026-09-06T02:00:00Z"}
            """;

        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.Forbidden, Body));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiError.AccessDeniedCode, result.ApiErrorCode);
        Assert.Equal("abc-123", result.ApiErrorId);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero), result.ApiErrorDate);
    }

    [Fact]
    public async Task EpasProseIsCarriedHereEvenThoughTheAuthClientDropsIts()
    {
        // The asymmetry is deliberate. logs.HandlerLoadAttempt.ApiErrorMessage exists for exactly this and
        // its description says "stored as received"; the hazard the auth client guards against is a gateway
        // echoing a request path that IS the credential, and a data request's path holds handler ids.
        const string Body = """{"code":"E_Whatever","message":"Handler id must be 12 characters"}""";

        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.BadRequest, Body));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal("Handler id must be 12 characters", result.ApiErrorMessage);
    }

    [Fact]
    public async Task OurOwnFailureMessageIsComposedAndNeverEpasWords()
    {
        const string Body = """{"code":"E_Whatever","message":"EPA prose that must not be borrowed"}""";

        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.BadRequest, Body));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        // Two columns, two authors: ApiErrorMessage is EPA's, FailureMessage is ours. Script 320's
        // description makes the distinction load-bearing -- one is a problem to raise with EPA, the other
        // is ours -- and merging them would lose which is which.
        Assert.DoesNotContain("borrowed", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("defect in this application", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHtmlErrorBodyIsToleratedAndLeavesTheErrorFieldsNull()
    {
        // A gateway, a proxy, an nginx page. Ordinary, and not a second failure: the status is what matters
        // and it is already in hand.
        (RcraInfoDataClient client, _) = Build(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("<html>502</html>", Encoding.UTF8, "text/html"),
            }));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiFetchOutcome.ServiceFailure, result.Outcome);
        Assert.Null(result.ApiErrorCode);
        Assert.Null(result.ApiErrorMessage);
        Assert.NotNull(result.FailureMessage);
    }

    // ---- Retry-After ------------------------------------------------------------------------------

    [Fact]
    public async Task ARetryAfterInSecondsIsRead()
    {
        (RcraInfoDataClient client, _) = Build(Throttled(retryAfter: new RetryConditionHeaderValue(
            TimeSpan.FromSeconds(90))));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiFetchOutcome.Throttled, result.Outcome);
        Assert.Equal(90, result.RetryAfterSeconds);
        Assert.Contains("90 seconds", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetryAfterAsAnHttpDateIsAlsoRead()
    {
        // The form most code drops, because RetryConditionHeaderValue.Delta is null for it. A 429 from a
        // load balancer in front of RCRAInfo is as likely as one from the application, and the balancer
        // chooses the form -- so reading only Delta would leave G21 unanswered while EPA was answering it.
        (RcraInfoDataClient client, _) = Build(Throttled(
            retryAfter: new RetryConditionHeaderValue(Now.AddMinutes(2))));

        Assert.Equal(120, (await client.FetchAsync(SummariesRequest)).RetryAfterSeconds);
    }

    [Fact]
    public async Task AnHttpDateAlreadyPastMeansNoWaitRatherThanANegativeOne()
    {
        (RcraInfoDataClient client, _) = Build(Throttled(
            retryAfter: new RetryConditionHeaderValue(Now.AddMinutes(-5))));

        Assert.Equal(0, (await client.FetchAsync(SummariesRequest)).RetryAfterSeconds);
    }

    [Fact]
    public async Task AnAbsurdRetryAfterIsCappedRatherThanObeyed()
    {
        // A week is a gateway misconfiguration, not an instruction, and an overnight load that honoured it
        // would sleep past its window. Capped rather than discarded: that EPA asked for a long wait is
        // worth recording.
        (RcraInfoDataClient client, _) = Build(Throttled(
            retryAfter: new RetryConditionHeaderValue(TimeSpan.FromDays(7))));

        Assert.Equal(3600, (await client.FetchAsync(SummariesRequest)).RetryAfterSeconds);
    }

    [Fact]
    public async Task AThrottleWithNoRetryAfterSaysTheWaitIsOurs()
    {
        (RcraInfoDataClient client, _) = Build(Throttled(retryAfter: null));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Null(result.RetryAfterSeconds);
        Assert.Contains("G21", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetryAfterOnAServiceFailureIsAlsoRead()
    {
        // 503 with Retry-After is the documented pairing in RFC 9110, and EPA's 500 is a documented answer
        // on all three endpoints. Reading the header only for 429 would drop the one case where a
        // maintenance window announces its own length.
        (RcraInfoDataClient client, _) = Build(new StubHandler((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

            return response;
        }));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(ApiFetchOutcome.ServiceFailure, result.Outcome);
        Assert.Equal(30, result.RetryAfterSeconds);
    }

    // ---- Transport failures ------------------------------------------------------------------------

    [Fact]
    public async Task ARefusedConnectionIsUnreachableAndNamesTheCauseNotTheUri()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made")));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(ApiFetchOutcome.Unreachable, result.Outcome);
        Assert.True(result.Outcome.IsRetryable());
        Assert.Null(result.HttpStatusCode);
        Assert.Contains("ConnectionError", result.FailureMessage!, StringComparison.Ordinal);

        // The exception's own message is not borrowed, even though this one is harmless: the rule is
        // structural, because an HTTP-stack message can carry the request URI and a URI carries a query
        // string.
        Assert.DoesNotContain("No connection could be made", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANullStatusIsSentRatherThanAZeroWhenThereWasNoResponse()
    {
        // Script 524 bounds HttpStatusCode to 100-599 and would refuse a 0 -- but more to the point a 0
        // reads like a status code to whoever queries the table.
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new HttpRequestException(HttpRequestError.NameResolutionError)));

        Assert.Null((await client.FetchAsync(SummariesRequest)).HttpStatusCode);
    }

    [Fact]
    public async Task ACancellationIsAResultAndNotAnException()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new OperationCanceledException()));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest, cts.Token);

        Assert.Equal(ApiFetchOutcome.Cancelled, result.Outcome);

        // Skipped, not Failed: an orderly shutdown must not be indistinguishable from EPA refusing us, and
        // 524 exempts Cancelled from needing any detail at all -- a process told to stop has nothing to add.
        Assert.Equal("Skipped", result.Outcome.ToStatus());
        Assert.Null(result.FailureMessage);
        Assert.Null(result.HttpStatusCode);
    }

    [Fact]
    public async Task ATimeoutIsTimedOutAndNotCancelled()
    {
        // .NET reports both as OperationCanceledException and only the token tells them apart. Getting this
        // backwards would report every timeout as a clean skip and lose the one signal that answers G21
        // without asking EPA.
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new OperationCanceledException()));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest, CancellationToken.None);

        Assert.Equal(ApiFetchOutcome.TimedOut, result.Outcome);
        Assert.Equal("TimedOut", result.Outcome.ToAttemptOutcome());
        Assert.Contains("G21", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatStopsMidStreamIsUnreachableRatherThanUnexpected()
    {
        // The headers arrived and the body did not. Retryable, and distinct from a body that arrived whole
        // and would not parse -- which is not.
        (RcraInfoDataClient client, _) = Build(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ThrowingContent(),
            }));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(ApiFetchOutcome.Unreachable, result.Outcome);
        Assert.True(result.Outcome.IsRetryable());
        Assert.Equal(200, result.HttpStatusCode);
    }

    // ---- Auth failing upstream of the request ------------------------------------------------------

    [Theory]
    [InlineData(ApiAuthOutcome.InvalidCredentials, ApiFetchOutcome.Unauthorized)]
    [InlineData(ApiAuthOutcome.AccessDenied, ApiFetchOutcome.AccessDenied)]
    [InlineData(ApiAuthOutcome.ServiceFailure, ApiFetchOutcome.ServiceFailure)]
    [InlineData(ApiAuthOutcome.Unreachable, ApiFetchOutcome.Unreachable)]
    [InlineData(ApiAuthOutcome.Unexpected, ApiFetchOutcome.Unexpected)]
    public async Task AnAuthFailureIsClassifiedRatherThanAllowedToEscape(
        ApiAuthOutcome authOutcome,
        ApiFetchOutcome expected)
    {
        // The token handler throws from inside the pipeline when no token can be had, and that is the one
        // failure whose cause is upstream of the request -- so letting it propagate would leave a version
        // the run definitely attempted with no attempt row at all.
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new RcraInfoAuthException(new ApiAuthResult(authOutcome, null, "EPA said no.", 401))));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.Equal(expected, result.Outcome);
        Assert.Contains("never sent", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthServiceFailureStaysRetryableRatherThanEndingTheRun()
    {
        // The reflexive mapping is to make every auth failure fatal -- a load with no token fetches nothing
        // -- and it would turn one 500 from EPA's auth endpoint into an abandoned night's load. A rejected
        // credential stays rejected; an unwell service does not.
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new RcraInfoAuthException(
                new ApiAuthResult(ApiAuthOutcome.ServiceFailure, null, "EPA's auth endpoint failed.", 500))));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.True(result.Outcome.IsRetryable());
        Assert.False(result.Outcome.IsFatalToTheRun());
    }

    [Fact]
    public async Task ARejectedCredentialReachingHereStillEndsTheRun()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new RcraInfoAuthException(
                new ApiAuthResult(ApiAuthOutcome.InvalidCredentials, null, "EPA rejected it.", 401))));

        ApiFetchResult result = await client.FetchAsync(SummariesRequest);

        Assert.True(result.Outcome.IsFatalToTheRun());
        Assert.False(result.Outcome.IsRetryable());

        // The status and code the auth client classified are carried through, so the attempt row says what
        // EPA answered even though the failure happened before this request existed.
        Assert.Equal(401, result.HttpStatusCode);
    }

    [Fact]
    public async Task AnAuthFailuresDiagnosticIsBorrowedBecauseItIsCredentialFreeByConstruction()
    {
        // The one message this client does copy from elsewhere. RcraInfoAuthException's own remarks make the
        // guarantee: its message is the auth result's Diagnostic, which every path in RcraInfoAuthClient
        // runs through EnsureNoCredential before returning.
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new RcraInfoAuthException(
                new ApiAuthResult(ApiAuthOutcome.Unreachable, null, "DNS did not resolve.", null))));

        Assert.Contains(
            "DNS did not resolve.",
            (await client.FetchAsync(SummariesRequest)).FailureMessage!,
            StringComparison.Ordinal);
    }

    // ---- Timing -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheStampsComeFromTheClockAndTheDurationIsNeverNegative()
    {
        TestClock clock = new(Now);
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK, Payload), clock);

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(Now, result.StartedDateUtc);
        Assert.Equal(Now, result.CompletedDateUtc);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task TheDurationIsMeasuredRatherThanSubtractedFromTheStamps()
    {
        // A frozen clock makes the two stamps equal, so a duration derived from them would be 0. The
        // stopwatch is independent of the clock, which is what script 524 asks for -- it derives DurationMs
        // from the stamps only when the caller omits it, because a Stopwatch beats DATETIME2 arithmetic.
        TestClock clock = new(Now);

        (RcraInfoDataClient client, _) = Build(new StubHandler((_, _) =>
        {
            Thread.Sleep(25);

            return StubHandler.Respond(HttpStatusCode.OK, Payload);
        }), clock);

        ApiFetchResult result = await client.FetchAsync(SourceRequest);

        Assert.Equal(result.StartedDateUtc, result.CompletedDateUtc);
        Assert.True(
            result.DurationMs >= 20,
            $"expected a measured duration, got {result.DurationMs} ms");
    }

    // ---- The attempt-log projection ----------------------------------------------------------------

    [Fact]
    public async Task TheAttemptElementLogsTheAbsolutePathAndNeverTheQueryString()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        ApiFetchResult result = await client.FetchAsync(OtherIdsRequest);
        HandlerLoadAttemptElement element = result.ToAttemptElement("MDD000000001", "N", 1, 1);

        // The one place this assignment happens in the solution, so this is the one place to assert it.
        Assert.Equal("/api/v1/hd/other-ids", element.RequestPath);
        Assert.DoesNotContain("handlerId", element.RequestPath!, StringComparison.Ordinal);
        Assert.DoesNotContain('?', element.RequestPath!);
        Assert.StartsWith("/", element.RequestPath!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAttemptElementCarriesTheKeyTheCallerSuppliesAndTheOutcomeItProjects()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.NotFound));

        ApiFetchResult result = await client.FetchAsync(SourceRequest);
        HandlerLoadAttemptElement element = result.ToAttemptElement("MDD000000001", "N", 7, 3);

        Assert.Equal("MDD000000001", element.HandlerId);
        Assert.Equal("N", element.SourceType);
        Assert.Equal(7, element.Sequence);
        Assert.Equal(3, element.AttemptNumber);

        // Failed as an attempt, Succeeded as a status: the attempt row records what the call did, the
        // status row records whether the version still needs work.
        Assert.Equal("Failed", element.Outcome);
        Assert.Equal("Succeeded", result.Outcome.ToStatus());
        Assert.Equal(404, element.HttpStatusCode);
    }

    [Fact]
    public async Task TheAttemptElementSuppliesAStartedStampBecauseTheColumnsDefaultWouldStampTheFlush()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        HandlerLoadAttemptElement element = (await client.FetchAsync(SourceRequest))
            .ToAttemptElement("MDD000000001", "N", 1, 1);

        Assert.Equal(Now, element.StartedDateUtc);
        Assert.Equal(Now, element.CompletedDateUtc);
        Assert.NotNull(element.DurationMs);
    }

    [Fact]
    public async Task AThrottledAttemptCarriesItsRetryAfterIntoTheLog()
    {
        (RcraInfoDataClient client, _) = Build(Throttled(new RetryConditionHeaderValue(
            TimeSpan.FromSeconds(45))));

        HandlerLoadAttemptElement element = (await client.FetchAsync(SummariesRequest))
            .ToAttemptElement("MDD000000001", "N", 1, 2);

        Assert.Equal("Throttled", element.Outcome);
        Assert.Equal(45, element.RetryAfterSeconds);
    }

    [Fact]
    public async Task EveryFailedAttemptCarriesSomeDetailBecause524RefusesOneThatDoesNot()
    {
        // 524 refuses a Failed, Throttled or TimedOut attempt that has no status code, no error code and no
        // failure message -- "an attempt that only records that something went wrong repeats what Outcome
        // already said". Cancelled is the one exemption. Asserted across every failure this client can
        // produce, because a refusal here discards the whole flush behind it.
        (HttpStatusCode? Status, Exception? Throw)[] cases =
        [
            (HttpStatusCode.BadRequest, null),
            (HttpStatusCode.Unauthorized, null),
            (HttpStatusCode.Forbidden, null),
            (HttpStatusCode.NotFound, null),
            (HttpStatusCode.TooManyRequests, null),
            (HttpStatusCode.InternalServerError, null),
            (HttpStatusCode.Conflict, null),
            (null, new HttpRequestException(HttpRequestError.ConnectionError)),
            (null, new OperationCanceledException()),
        ];

        foreach ((HttpStatusCode? status, Exception? thrown) in cases)
        {
            (RcraInfoDataClient client, _) = Build(
                thrown is null ? StubHandler.Always(status!.Value) : StubHandler.Throwing(thrown));

            ApiFetchResult result = await client.FetchAsync(SummariesRequest);
            HandlerLoadAttemptElement element = result.ToAttemptElement("MDD000000001", "N", 1, 1);

            if (element.Outcome is "Succeeded" or "Cancelled")
            {
                continue;
            }

            Assert.True(
                element.HttpStatusCode is not null
                || element.ApiErrorCode is not null
                || element.FailureMessage is not null,
                $"{result.Outcome} produced an attempt row with no detail, which 524 refuses.");
        }
    }

    // ---- ToString ---------------------------------------------------------------------------------

    [Fact]
    public async Task ToStringNamesTheOutcomeAndThePathAndNotTheQueryStringOrThePayload()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Always(HttpStatusCode.OK, Payload));

        string described = (await client.FetchAsync(OtherIdsRequest)).ToString();

        Assert.Contains("Succeeded", described, StringComparison.Ordinal);
        Assert.Contains("api/v1/hd/other-ids", described, StringComparison.Ordinal);
        Assert.DoesNotContain("handlerId=", described, StringComparison.Ordinal);
        Assert.DoesNotContain("sequence", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToStringSaysSoWhenThereWasNoResponseAtAll()
    {
        (RcraInfoDataClient client, _) = Build(StubHandler.Throwing(
            new HttpRequestException(HttpRequestError.NameResolutionError)));

        Assert.Contains(
            "no response",
            (await client.FetchAsync(SummariesRequest)).ToString(),
            StringComparison.Ordinal);
    }

    // ---- Fixtures ---------------------------------------------------------------------------------

    private static (RcraInfoDataClient Client, StubHandler Handler) Build(
        StubHandler handler,
        TestClock? clock = null)
    {
        HttpClient http = new(handler)
        {
            BaseAddress = new Uri("https://rcranodepreprod.epa.gov/rcra-api/rest/"),
        };

        return (new RcraInfoDataClient(http, clock ?? new TestClock(Now)), handler);
    }

    private static StubHandler Throttled(RetryConditionHeaderValue? retryAfter) =>
        new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);

            if (retryAfter is not null)
            {
                response.Headers.RetryAfter = retryAfter;
            }

            return response;
        });

    /// <summary>Content whose body fails part-way through, the way a dropped connection does.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
