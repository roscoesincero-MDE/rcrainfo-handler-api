namespace RCRAInfo.Loader.Api;

/// <summary>
/// The message handler that makes <see cref="RequestPacer"/> apply to every request EPA actually receives.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is registered last and is therefore innermost, closest to the network — and that placement is the
/// whole design.</b> The handler chain on <c>rcrainfo-data</c> runs
/// <c>ApiTokenHandler</c> → resilience → this. Put the gate <i>outside</i> the resilience handler and it
/// paces logical requests while Polly's retries slip past unpaced, which is precisely backwards: a retry
/// storm is when EPA has already said it is unhappy and is the moment pacing matters most. Innermost, every
/// attempt is a paced request, and a service that answers <c>429</c> is not answered with three unpaced
/// attempts in the same second.
/// </para>
/// <para>
/// <b>The response body is buffered here, inside the lease.</b> Two problems close together. The lease would
/// otherwise end when the response <i>headers</i> arrive, while the body — a summaries window is the largest
/// response this loader asks for — downloads outside it, so <c>MaxConcurrentRequests</c> would bound
/// something narrower than "requests in flight". And the <c>rcrainfo-data</c> client deliberately has no
/// <c>HttpClient.Timeout</c> because the resilience pipeline owns its per-attempt timeouts — but that
/// pipeline sits <i>above</i> this handler, so a read that happened after this handler returned would be
/// covered by no timeout at all. Buffering here puts the download inside both the lease and
/// <c>AttemptTimeout</c>. It costs nothing in memory that was not already being spent:
/// <c>RcraInfoDataClient</c> reads the whole body into a string regardless.
/// </para>
/// <para>
/// <b>Rate limiting in a handler rather than in the fetch loop</b> is the same argument as for the token
/// handler. A loop is one caller. The gate has to hold for a caller nobody has written yet — a reconciliation
/// pass, a one-off backfill, a diagnostic in the monitoring app — and a ceiling that only the main loop
/// respects is not a ceiling.
/// </para>
/// <para>
/// <b>Nothing is logged here.</b> Not the URI, not the wait. This handler sees a request that
/// <c>ApiTokenHandler</c> has already put a bearer token on, so it is one of the two classes in the solution
/// holding a live credential in a header; both are registered <c>RemoveAllLoggers()</c> and neither logs. A
/// wait worth knowing about is visible as <c>ApiFetchResult.DurationMs</c> and in
/// <see cref="RequestPacer.Reservations"/>.
/// </para>
/// </remarks>
/// <param name="pacer">The process-wide gate. A singleton; two pacers are two rate limits.</param>
public sealed class RequestPacingHandler(RequestPacer pacer) : DelegatingHandler
{
    private readonly RequestPacer pacer = pacer ?? throw new ArgumentNullException(nameof(pacer));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using IDisposable lease = await this.pacer.AcquireAsync(cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Read the body while the lease is still held. See the class remarks: this is what keeps the
            // download inside both the concurrency ceiling and the resilience handler's attempt timeout.
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A response that cannot be returned must not be left open. Its disposal is otherwise the
            // caller's, and there is no caller on this path.
            response.Dispose();

            throw;
        }

        return response;
    }
}
