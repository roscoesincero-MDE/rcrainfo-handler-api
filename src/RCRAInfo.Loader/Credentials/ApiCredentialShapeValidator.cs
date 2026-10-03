using System.Buffers;
using System.Globalization;

using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Loader.Credentials;

/// <summary>
/// Checks that the RCRAInfo API ID and Key look like they were pasted correctly. It does <b>not</b>
/// check that they work — <see cref="ApiCredentialValidator"/> does that, by calling EPA.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is the disclosure.</b> Plan §4.3 validates the API credential by calling
/// <c>GET /api/v1/auth/{apiId}/{apiKey}</c>. While that endpoint had no client, this class stood in for
/// it, and naming it <c>ApiCredentialValidator</c> would have put a validator in the composition that
/// reported every value as good with no way for the next reader to tell. D1 built the real one, and this
/// keeps its narrower name because its job is unchanged: it validates a shape.
/// </para>
/// <para>
/// <b>It still runs, first.</b> <c>CredentialValidators.All</c> short-circuits, so an offline check that
/// costs nothing goes ahead of a network round-trip — and its message is better where it applies. A value
/// pasted with the surrounding quotation marks gets "position 0 is a quote, the paste took more than the
/// value" instead of EPA's "401, invalid credentials", which would send an operator to regenerate a key
/// that was never wrong.
/// </para>
/// <para>
/// <b>The check is deliberately not a format check.</b> Nothing published says how long an RCRAInfo API
/// Key is or which characters it uses — G1 is still open, and the sole recorded knowledge is that a
/// developer logs in to preProd and copies two values. A length or character-class rule invented here
/// would reject a legitimate key the first time EPA changed its generator, at 2am, with a message
/// asserting something this project never knew.
/// </para>
/// </remarks>
public static class ApiCredentialShapeValidator
{
    /// <summary>Characters that mean the paste took more than the value.</summary>
    /// <remarks>
    /// A quotation mark is the one that actually happens: selecting the value inside a JSON file and
    /// catching a quote at one end. It cannot be a legitimate part of a credential that travels in a URI
    /// path, which is what makes refusing it safe.
    /// </remarks>
    private static readonly SearchValues<char> NeverInACredential =
        SearchValues.Create (['"', '\'', '\r', '\n', '\t', ' ']);

    /// <summary>This validator as the delegate <c>CredentialBootstrapper</c> takes.</summary>
    public static CredentialValidator Delegate { get; } = ValidateAsync;

    /// <summary>Checks the pair's shape.</summary>
    /// <param name="credentials">The credentials to inspect.</param>
    /// <param name="cancellationToken">Cancellation token. Unused; nothing here waits on anything.</param>
    /// <returns>Whether the pair looks like it was pasted correctly.</returns>
    public static Task<CredentialValidation> ValidateAsync (
        ApplicationCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (credentials);
        cancellationToken.ThrowIfCancellationRequested ();

        return Task.FromResult (Check (credentials));
    }

    private static CredentialValidation Check (ApplicationCredentials credentials)
    {
        // Both or neither is already enforced by CredentialFile, which refuses half a pair. Absent is a
        // legitimate state for the monitoring application; for this one it means the file was seeded from
        // the wrong template.
        if (credentials.ApiId is null || credentials.ApiKey is null)
        {
            return CredentialValidation.Invalid (
                "The credential file carries no RCRAInfo API ID and Key. The console application calls "
                + "EPA on every run, so seed it from the loader's secrets.Template.json rather than the "
                + "monitor's.");
        }

        CredentialValidation? idProblem = Inspect (credentials.ApiId, "ApiId");

        if (idProblem is not null)
        {
            return idProblem;
        }

        CredentialValidation? keyProblem = Inspect (credentials.ApiKey, "ApiKey");

        if (keyProblem is not null)
        {
            return keyProblem;
        }

        // The Key and the ID are generated together and are never the same value. Equal ones mean one
        // field was pasted into both, which is the mistake that produces an auth failure whose message
        // says nothing about a copy-and-paste.
        if (string.Equals (credentials.ApiId, credentials.ApiKey, StringComparison.Ordinal))
        {
            return CredentialValidation.Invalid (
                "ApiId and ApiKey hold the same value. They are generated as a pair and are never equal, "
                + "so one of them was pasted into both fields.");
        }

        return CredentialValidation.Valid;
    }

    private static CredentialValidation? Inspect (string value, string field)
    {
        // Every message below names the field and the character position, and never the value: EPA's auth
        // endpoint carries both halves in the URI path, so the Key is as much a secret as the SQL
        // password, and this message is logged where the monitoring web application can read it (AR8).
        int at = value.IndexOfAny (NeverInACredential);

        if (at >= 0)
        {
            return CredentialValidation.Invalid (
                string.Format (
                    CultureInfo.InvariantCulture,
                    "'{0}' contains a quote, a space or a line break at position {1}. RCRAInfo sends "
                    + "both halves of this credential in a request URI, so none of those can be part of "
                    + "it -- the paste took more than the value. The value is not shown here because it "
                    + "is a secret.",
                    field,
                    at));
        }

        return null;
    }
}
