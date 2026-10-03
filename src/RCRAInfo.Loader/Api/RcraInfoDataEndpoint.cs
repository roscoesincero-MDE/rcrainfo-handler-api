namespace RCRAInfo.Loader.Api;

/// <summary>
/// The RCRAInfo data endpoint families this application reads. Named rather than implied, because the
/// pinned spec documents a <b>different set of answers</b> for each and the differences change what a
/// status code means.
/// </summary>
/// <remarks>
/// See <see cref="RcraInfoDataRequest.DocumentsNotFound"/> for the difference that matters most: two of
/// these four document <c>404</c> and two do not, so <c>404</c> is a data condition on two of them and
/// a client defect on the others.
/// </remarks>
public enum RcraInfoDataEndpoint
{
    /// <summary>
    /// <c>GET api/v1/hd/sources/summaries</c> — the version list, and the only entry point to a load.
    /// </summary>
    /// <remarks>
    /// Documented answers: <c>200, 400, 401, 403, 404, 500</c>. It takes <b>no paging parameters</b>, which
    /// is why the initial load windows by date (plan §D2, [R28]).
    /// </remarks>
    Summaries,

    /// <summary>
    /// <c>GET api/v1/hd/sources/{handlerId}/{sourceType}/{sequence}</c> — one full 377-field version.
    /// </summary>
    /// <remarks>Documented answers: <c>200, 401, 403, 404, 500</c>. No <c>400</c>.</remarks>
    Source,

    /// <summary>
    /// <c>GET api/v1/hd/other-ids?handlerId=</c> — the alternate identifiers for one handler.
    /// </summary>
    /// <remarks>
    /// Documented answers: <c>200, 400, 401, 403, 500</c>. <b>No <c>404</c></b> — a handler with no other
    /// identifiers answers <c>200</c> with an empty array, so an empty result here is success and not an
    /// absence.
    /// </remarks>
    OtherIds,

    /// <summary>
    /// <c>GET api/v1/lookup/hd/{list}</c> — one of the 23 mirrored EPA code lists (G15). One member for
    /// all 23, because they answer identically and differ only in the path segment and the payload shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Documented answers: <c>200, 401, 403, 500</c> — identical on all 24 <c>/lookup/hd/*</c> paths, and
    /// <b>a fourth distinct set</b>. Neither <c>404</c> nor <c>400</c> is documented, which has two
    /// consequences. A lookup can never drive a soft delete: retirement of a code is decided by its
    /// <i>absence from a successful payload</i> (script 523), never by a status. And an omitted required
    /// <c>stateCode</c> — five of these lists require one — cannot come back as a well-behaved <c>400</c>;
    /// it arrives as something undocumented, so it classifies as
    /// <see cref="ApiFetchOutcome.Unexpected"/>, or worse as a <c>200</c> carrying every jurisdiction.
    /// <see cref="RcraInfoLookup"/> is what keeps that from being reachable.
    /// </para>
    /// <para>
    /// The lists are fetched <b>before</b> the handler data, which is G15's sequencing decision: a code
    /// arriving on a handler version before it appears in the mirror is a data-quality observation rather
    /// than a failed load, and fetching the lists first is what keeps that rare.
    /// </para>
    /// </remarks>
    Lookup,
}
