namespace RCRAInfo.Loader.Api;

/// <summary>
/// Sends one prepared request to a RCRAInfo data endpoint and classifies whatever comes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>One method, and it takes a <see cref="RcraInfoDataRequest"/> rather than the arguments of a
/// call.</b> The obvious interface here is three methods — <c>GetSummariesAsync</c>,
/// <c>GetSourceAsync</c>, <c>GetOtherIdsAsync</c> — and it would put the validation the factories do
/// behind three doors instead of one, and would give this interface a reason to grow every time an
/// endpoint is added. More importantly it would let a caller build a request this class then has to
/// classify without knowing which endpoint's documented status set applies, which is the one thing the
/// classification depends on.
/// </para>
/// <para>
/// <b>This method does not retry and does not throw for a failed call.</b> Transient retries belong to the
/// resilience handler on the <c>rcrainfo-data</c> client, and everything it gives up on arrives here as an
/// <see cref="ApiFetchResult"/> to be logged. A fetch loop over several hundred thousand handlers cannot
/// be written around exceptions: each one would have to be caught, classified and turned into a log row,
/// which is what <see cref="ApiFetchOutcome"/> already is.
/// </para>
/// <para>
/// <b>That includes a failure to authenticate.</b> A <see cref="RcraInfoAuthException"/> from inside the
/// pipeline — the token handler needed a bearer token and could not get one — is classified like any other
/// failure rather than propagated. It is the only failure whose cause is upstream of the request, so
/// letting it escape would be the one case where a version the run definitely attempted got no attempt row
/// at all. The outcome preserves the distinction the auth client drew: a rejected credential is fatal to
/// the run, an unreachable auth endpoint is not.
/// </para>
/// </remarks>
public interface IRcraInfoDataClient
{
    /// <summary>Sends the request and returns what happened.</summary>
    /// <param name="request">A request from one of <see cref="RcraInfoDataRequest"/>'s factories.</param>
    /// <param name="cancellationToken">Cancels the call; a cancelled call is a result, not an exception.</param>
    /// <returns>The classified outcome, timed, with the payload when there is one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    Task<ApiFetchResult> FetchAsync(
        RcraInfoDataRequest request,
        CancellationToken cancellationToken = default);
}
