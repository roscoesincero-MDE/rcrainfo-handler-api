namespace RCRAInfo.Loader.Api;

/// <summary>
/// The names of the two <c>HttpClient</c> registrations, kept apart on purpose.
/// </summary>
/// <remarks>
/// One client would be simpler and wrong. The auth client sends the API ID and Key <b>in the request
/// path</b>; the data client sends a bearer token in a header and handler identifiers in the path. They
/// need different timeouts, different response-size limits, and — most importantly — the auth client must
/// never acquire a logger, an interceptor, or a telemetry enricher that records a URI. Separating them
/// means that constraint applies to one registration that is three lines long instead of to every future
/// addition to a shared one.
/// </remarks>
public static class RcraInfoApiClients
{
    /// <summary>The client that calls <c>GET /api/v1/auth/{apiId}/{apiKey}</c>. Nothing else uses it.</summary>
    public const string Auth = "rcrainfo-auth";

    /// <summary>The client that calls the Handler data endpoints, carrying a bearer token.</summary>
    public const string Data = "rcrainfo-data";
}
