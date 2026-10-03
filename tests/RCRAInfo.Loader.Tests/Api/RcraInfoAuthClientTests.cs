using System.Globalization;
using System.Net;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The six-way classification, and the rule that no diagnostic may ever repeat the credential.
/// </summary>
public class RcraInfoAuthClientTests
{
    private const string ApiId = "MDTESTAPIID00001";
    private const string ApiKey = "MDTESTAPIKEY-0123456789";
    private const string Base = "https://rcranodepreprod.epa.gov/rcra-api/rest/";

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ATokenIsReturnedAndStampedWithOurOwnClock()
    {
        DateTimeOffset expires = Now.AddMinutes(20);
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.OK,
            $"{{\"token\":\"eyJhbGciOi.stub\",\"expiration\":\"{expires.ToString("O", CultureInfo.InvariantCulture)}\"}}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Succeeded, result.Outcome);
        Assert.Equal("eyJhbGciOi.stub", result.Require().Value);
        Assert.Equal(expires, result.Require().ExpiresAt);

        // IssuedAt is OUR clock, not EPA's: the whole point of the refresh floor is to be measurable against
        // the clock whose disagreement with EPA is the thing being defended against.
        Assert.Equal(Now, result.Require().IssuedAt);
        Assert.Equal(TimeSpan.FromMinutes(20), result.Require().Lifetime);
    }

    [Fact]
    public async Task TheRequestGoesToTheDocumentedPathWithBothHalvesEscaped()
    {
        StubHandler handler = StubHandler.Always(HttpStatusCode.OK, "{\"token\":\"t\"}");

        await CallAsync(handler, apiId: "a/b", apiKey: "c d?e");

        // A '/' in a Key would otherwise change which endpoint is called and the answer would be a 404 that
        // reads like a service outage.
        Assert.Equal(
            Base + "api/v1/auth/a%2Fb/c%20d%3Fe",
            Assert.Single(handler.RequestUris));
    }

    [Fact]
    public async Task TheBodyEpaActuallySendsReads()
    {
        // Copied from the first live call this solution ever made to EPA's auth endpoint (preprod,
        // 2026-09-06). It answered 200 with a token, and this client rejected the answer: the offset is
        // "+0000", the ISO 8601 BASIC form, and Utf8JsonReader.GetDateTimeOffset implements RFC 3339
        // strictly and requires "+00:00" or "Z". Every test above this one was written with
        // DateTimeOffset "O" formatting, which is RFC 3339 -- so the suite agreed with the spec and the
        // spec disagreed with the service. This case is the service.
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.OK,
            "{\"token\":\"eyJhbGciOi.stub\",\"expiration\":\"2026-09-06T13:40:44.361+0000\"}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 6, 13, 40, 44, 361, TimeSpan.Zero),
            result.Require().ExpiresAt);
    }

    [Fact]
    public async Task AnErrorDateInEpasOwnOffsetFormReadsToo()
    {
        // The same divergence, on the other body EPA sends. ApiError is deserialized by BOTH clients, and
        // before RcraInfoJson they used two different options objects -- so this is also the regression for
        // "the same error body parsed on one path and not on the other".
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.Unauthorized,
            $"{{\"code\":\"{ApiError.InvalidCredentialsCode}\",\"message\":\"Invalid credentials\","
            + "\"errorId\":\"b6a1-0009\",\"errorDate\":\"2026-09-06T13:40:44.361+0000\"}");

        ApiAuthResult result = await CallAsync(handler);

        // The date is not itself reported anywhere -- what matters is that failing to read it did not cost
        // the error CODE, which is the one field that decides whether the credential needs re-seeding.
        Assert.Equal(ApiAuthOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(ApiError.InvalidCredentialsCode, result.ErrorCode);
        Assert.Contains("b6a1-0009", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpirationInAFormNobodyCanReadIsUnexpectedRatherThanImmediate()
    {
        // Not folded into "absent expiration means immediate". An unreadable expiry treated as immediate
        // would keep the load running while re-authenticating at the floor rate for hours, aimed at the one
        // endpoint that carries the credential in its URI, with nothing in the log saying why.
        // Epoch milliseconds, which is the other form a service switches to.
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.OK,
            "{\"token\":\"eyJhbGciOi.stub\",\"expiration\":\"1789040444361\"}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Unexpected, result.Outcome);
        Assert.Contains("not the documented JSON", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentExpirationIsTreatedAsImmediateAndNotAsForever()
    {
        StubHandler handler = StubHandler.Always(HttpStatusCode.OK, "{\"token\":\"t\"}");

        ApiAuthResult result = await CallAsync(handler);

        // "No expiration" must never mean "never expires". A token this application believes is eternal is
        // one it keeps sending after EPA stops accepting it, and the symptom is a wave of 401s mid-load.
        Assert.Equal(Now, result.Require().ExpiresAt);
        Assert.Equal(TimeSpan.Zero, result.Require().Lifetime);
    }

    [Fact]
    public async Task ASuccessWithNoTokenIsAFailure()
    {
        StubHandler handler = StubHandler.Always(HttpStatusCode.OK, "{\"expiration\":\"2026-09-06T02:20:00Z\"}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Unexpected, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Contains("no token", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASuccessThatIsNotJsonIsReportedWithoutQuotingTheBody()
    {
        StubHandler handler = new((_, _) => StubHandler.Respond(
            HttpStatusCode.OK,
            "<html><title>Sign in to EPA</title></html>"));

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Unexpected, result.Outcome);

        // The body of a SUCCESSFUL auth response is a bearer token, so the parser's message -- which quotes
        // the offending JSON -- is dropped and only the position is reported.
        Assert.DoesNotContain("Sign in", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("not the documented JSON", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A401IsAnInvalidCredentialAndSaysToReseed()
    {
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.Unauthorized,
            $"{{\"code\":\"{ApiError.InvalidCredentialsCode}\",\"message\":\"Invalid credentials\","
            + "\"errorId\":\"b6a1-0002\",\"errorDate\":\"2026-09-06T02:00:00Z\"}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(ApiError.InvalidCredentialsCode, result.ErrorCode);
        Assert.False(result.IsRetryable);

        // The two things EPA support asks for are reported; the prose is not.
        Assert.Contains(ApiError.InvalidCredentialsCode, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("b6a1-0002", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid credentials", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("re-seed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A403IsAScopeProblemAndSaysReseedingWillNotHelp()
    {
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.Forbidden,
            $"{{\"code\":\"{ApiError.AccessDeniedCode}\",\"message\":\"no\",\"errorId\":\"x\"}}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.AccessDenied, result.Outcome);
        Assert.False(result.IsRetryable);

        // Conflating this with a 401 is the specific mistake plan §D1 names: it sends an operator to
        // regenerate a key that works, to solve a permissions problem only EPA can solve.
        Assert.Contains("permissions or scope", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("will not change it", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task EpaFailingOrThrottlingIsRetryable(HttpStatusCode status)
    {
        ApiAuthResult result = await CallAsync(StubHandler.Always(status));

        Assert.Equal(ApiAuthOutcome.ServiceFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task AnUndocumentedAnswerIsUnexpectedAndNotRetried(HttpStatusCode status)
    {
        ApiAuthResult result = await CallAsync(StubHandler.Always(status));

        Assert.Equal(ApiAuthOutcome.Unexpected, result.Outcome);
        Assert.False(result.IsRetryable);

        // A 404 here is nearly always a base address pointing at the wrong path, so the message says where
        // to look rather than describing the status.
        Assert.Contains("/rcra-api/rest", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorBodyThatIsNotJsonCostsNothingAndSaysNothingAboutItself()
    {
        // A proxy or gateway page, which is the ordinary case for a 502. It is not parsed and not quoted:
        // gateways echo the request path they could not route, and that path holds the credential.
        StubHandler handler = new((_, _) => StubHandler.Respond(
            HttpStatusCode.BadGateway,
            $"<html>no route for /rcra-api/rest/api/v1/auth/{ApiId}/{ApiKey}</html>"));

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.ServiceFailure, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.DoesNotContain(ApiKey, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransportFailureIsUnreachableAndReportsTheTypeNotTheMessage()
    {
        StubHandler handler = StubHandler.Throwing(
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (h:443)"));

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Unreachable, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Null(result.StatusCode);

        // HttpRequestError names the cause; the message is not repeated. Measured to carry host and port
        // only -- but this is the one call where being wrong about that costs a leaked credential.
        Assert.Contains("NameResolutionError", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("No such host", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsUnreachableAndACallerCancellationIsNot()
    {
        StubHandler handler = StubHandler.Throwing(new TaskCanceledException("timed out"));

        ApiAuthResult timedOut = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.Unreachable, timedOut.Outcome);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // A caller who cancelled gets an exception, not a classified failure. Reporting "EPA is unreachable"
        // for a shutdown would put a network fault in front of an operator who pressed Ctrl+C.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CallAsync(handler, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task ADiagnosticThatWouldRepeatTheCredentialIsWithheldEntirely()
    {
        // EPA echoing the request into a field this client DOES report. Contrived, and it is the reason the
        // check exists: every other defence here is about what this code writes, and this one is about what
        // the far end sends back.
        StubHandler handler = StubHandler.Always(
            HttpStatusCode.Unauthorized,
            $"{{\"code\":\"E_Invalid\",\"message\":\"no\",\"errorId\":\"{ApiKey}\"}}");

        ApiAuthResult result = await CallAsync(handler);

        Assert.Equal(ApiAuthOutcome.InvalidCredentials, result.Outcome);
        Assert.DoesNotContain(ApiKey, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("withheld in full", result.Diagnostic, StringComparison.Ordinal);

        // The status still gets through, because it is the part that is always safe.
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task NoDiagnosticOnAnyPathContainsEitherHalfOfTheCredential()
    {
        // The rule, swept across every outcome at once. A future edit that adds "(key: ...)" to one message
        // to make it actionable fails here rather than in a log the monitoring web application can read.
        (HttpStatusCode Status, string? Body)[] answers =
        [
            (HttpStatusCode.OK, "{\"token\":\"t\"}"),
            (HttpStatusCode.OK, "not json"),
            (HttpStatusCode.Unauthorized, $"{{\"code\":\"{ApiId}\",\"errorId\":\"{ApiKey}\"}}"),
            (HttpStatusCode.Forbidden, null),
            (HttpStatusCode.InternalServerError, $"<html>{ApiKey}</html>"),
            (HttpStatusCode.NotFound, null),
        ];

        foreach ((HttpStatusCode status, string? body) in answers)
        {
            ApiAuthResult result = await CallAsync(new StubHandler((_, _) => StubHandler.Respond(status, body)));

            Assert.DoesNotContain(ApiId, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiId, result.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, result.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AClientWithNoBaseAddressIsRefusedAtConstruction()
    {
        using HttpClient http = new();

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RcraInfoAuthClient(http, TimeProvider.System));

        Assert.Contains("BaseAddress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyCredentialIsRefusedBeforeAnythingIsSent()
    {
        StubHandler handler = StubHandler.Always(HttpStatusCode.OK, "{\"token\":\"t\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(handler, apiKey: "   "));
        Assert.Equal(0, handler.Calls);
    }

    private static async Task<ApiAuthResult> CallAsync(
        StubHandler handler,
        string apiId = ApiId,
        string apiKey = ApiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpClient http = new(handler) { BaseAddress = new Uri(Base) };

        RcraInfoAuthClient client = new(http, new TestClock(Now));

        return await client.AuthenticateAsync(apiId, apiKey, cancellationToken);
    }
}
