using RCRAInfo.Core.Credentials;
using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Credentials;

/// <summary>
/// Plan §4.3's real API validator: proves the RCRAInfo API ID and Key by calling EPA's auth endpoint and
/// getting a token, which is the only thing that proves them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces <see cref="ApiCredentialShapeValidator"/> as the answer to "do these credentials
/// work", and does not replace it as a check.</b> Both run, shape first: the shape check is offline and
/// free and catches the paste mistakes with a message naming the character position, while this one costs
/// a network round-trip and can only say "EPA said no". Composed in that order by
/// <c>CredentialValidators.All</c>, which short-circuits — so a value with a quotation mark in it is
/// never sent to EPA, and an operator who pasted the surrounding quotes is told exactly that instead of
/// being told their key is invalid.
/// </para>
/// <para>
/// <b>Sealing happens only on proof.</b> Every outcome other than a token — EPA unreachable, EPA failing,
/// an undocumented answer — is reported as a rejection, so the bootstrapper does not encrypt. That is the
/// same rule <c>SqlCredentialValidator</c> follows for a SQL Server it cannot reach, and for the same
/// reason: a sealed credential cannot be read back and corrected, so sealing one that was never verified
/// trades a five-minute re-paste today for a re-seed from the password manager later. The messages say
/// which case it was, because "could not be verified" and "was rejected" send an operator to different
/// places.
/// </para>
/// <para>
/// <b>A 403 is also reported as a rejection here</b>, and that is a deliberate difference from
/// <see cref="ApiAuthOutcome.AccessDenied"/>'s meaning on a data call. The pinned spec documents 200, 401
/// and 500 for the auth endpoint and no 403 at all, so a 403 arriving here is an answer from something
/// this client does not model — plausibly a gateway in front of EPA rather than EPA. Sealing on it would
/// be sealing on a guess.
/// </para>
/// <para>
/// <b>Owns its own <c>HttpClient</c>, and does not come from the container.</b> This validator runs
/// <i>before</i> there is a host: the credential file may still need sealing, and the AR4 bootstrap has to
/// be allowed to fail into an exit code rather than a start-up exception (plan §4.2). It also means the
/// client is built here with no logging attached at all, which for this one call is the difference between
/// a clean run and the API Key in a log — see <see cref="RcraInfoAuthClient"/>.
/// </para>
/// </remarks>
public sealed class ApiCredentialValidator : IDisposable
{
    private readonly IRcraInfoAuthClient client;
    private readonly HttpClient? owned;

    /// <summary>Creates a validator over an existing auth client.</summary>
    /// <param name="client">The auth client to use.</param>
    public ApiCredentialValidator(IRcraInfoAuthClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        this.client = client;
    }

    private ApiCredentialValidator(IRcraInfoAuthClient client, HttpClient owned)
        : this(client)
    {
        this.owned = owned;
    }

    /// <summary>This validator as the delegate <c>CredentialBootstrapper</c> takes.</summary>
    public CredentialValidator Delegate => ValidateAsync;

    /// <summary>Builds a validator with its own HTTP client from configuration.</summary>
    /// <param name="options">The API options. Only the base address and request timeout are used.</param>
    /// <returns>A validator the caller must dispose.</returns>
    /// <exception cref="ArgumentException">
    /// The base address is missing or unusable. The message is the one
    /// <see cref="RcraInfoApiOptions.TryGetBaseUri"/> produces, which names the setting and both published
    /// EPA addresses.
    /// </exception>
    public static ApiCredentialValidator Create(RcraInfoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.TryGetBaseUri(out Uri? baseUri, out string? problem))
        {
            throw new ArgumentException(problem, nameof(options));
        }

        HttpClient http = new()
        {
            BaseAddress = baseUri,
            Timeout = options.RequestTimeout,
            MaxResponseContentBufferSize = 64 * 1024,
        };

        try
        {
            return new ApiCredentialValidator(new RcraInfoAuthClient(http, TimeProvider.System), http);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>Calls EPA's auth endpoint with the pair and reports whether a token came back.</summary>
    /// <param name="credentials">The credentials to prove.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether EPA issued a token for them.</returns>
    public async Task<CredentialValidation> ValidateAsync(
        ApplicationCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (string.IsNullOrWhiteSpace(credentials.ApiId) || string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return CredentialValidation.Invalid(
                "The credential file carries no RCRAInfo API ID and Key. The console application calls EPA "
                + "on every run, so seed it from the loader's secrets.Template.json rather than the "
                + "monitor's.");
        }

        ApiAuthResult result = await client
            .AuthenticateAsync(credentials.ApiId, credentials.ApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return CredentialValidation.Valid;
        }

        // result.Diagnostic is credential-free by construction -- RcraInfoAuthClient checks it against both
        // halves before returning it. That guarantee is what makes it safe to put here, because this string
        // is written to logs.ExecutionLog and the monitoring web application can read that table (AR8).
        string prefix = result.Outcome switch
        {
            ApiAuthOutcome.InvalidCredentials => "EPA rejected the RCRAInfo API credential.",
            ApiAuthOutcome.AccessDenied =>
                "EPA answered the auth call with 403, which its own specification does not list for this "
                + "endpoint. The credential has not been proved, so it has not been sealed.",
            _ =>
                "The RCRAInfo API credential could NOT BE VERIFIED, which is not the same as being wrong. "
                + "Nothing has been sealed, and the value in the credential file is unchanged -- fix the "
                + "cause and run again.",
        };

        return CredentialValidation.Invalid($"{prefix} {result.Diagnostic}");
    }

    /// <inheritdoc/>
    public void Dispose() => owned?.Dispose();
}
