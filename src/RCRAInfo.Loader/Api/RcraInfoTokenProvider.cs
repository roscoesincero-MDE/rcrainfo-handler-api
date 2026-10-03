using Microsoft.Extensions.Options;

using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Holds one bearer token for the whole process, renews it before it expires, and lets exactly one
/// caller at a time do the renewing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three behaviours, and each one is here because of a specific failure.</b>
/// </para>
/// <para>
/// <b>Proactive renewal</b> (plan §D1). The token lives 20 minutes and an initial load outlives many
/// of them, so waiting for a <c>401</c> would mean one failed request per token per worker — thousands
/// of avoidable round-trips over a full load, each one an error line an operator has to learn to
/// ignore. The margin comes from <see cref="RcraInfoApiOptions.RefreshDueAt"/>, which caps it at half
/// the token's life so a short-lived token cannot cause a renewal per request.
/// </para>
/// <para>
/// <b>One renewal at a time.</b> D2 fetches with bounded concurrency, so every worker crosses the
/// expiry boundary within a few milliseconds of the others. Without the gate below, N workers make N
/// auth calls — a thundering herd aimed at the one endpoint whose failure stops the entire load, and
/// aimed at it by us. With it, one worker authenticates and the rest wait and take its answer. The gate
/// is a <see cref="SemaphoreSlim"/> rather than a <c>lock</c> because the work inside it is
/// asynchronous.
/// </para>
/// <para>
/// <b>A floor on the auth rate</b> (<see cref="RcraInfoApiOptions.MinimumRefreshInterval"/>). A token
/// that arrives already expired — which is what a clock disagreement looks like from here — is due for
/// renewal the instant it is issued. Renewing it immediately produces another one in the same state,
/// forever, as fast as the network allows. The floor turns that into a bounded trickle and lets the load
/// proceed on the "expired" token, which is exactly what the <c>401</c>-retry path is for.
/// </para>
/// <para>
/// <b>What this deliberately does not do</b> is retry a rejected credential. A <c>401</c> from the auth
/// endpoint means the API ID and Key are wrong, and <see cref="ApiAuthResult.IsRetryable"/> says so; the
/// exception propagates and the run stops. AR4 recorded the same rule for the SQL login after a lockout
/// threshold of three attempts was measured on the domain — an EPA API account nobody here administers
/// deserves at least as much caution.
/// </para>
/// </remarks>
public sealed class RcraInfoTokenProvider : IApiTokenProvider, IDisposable
{
    private readonly IRcraInfoAuthClient client;
    private readonly RcraInfoApiOptions options;
    private readonly TimeProvider clock;
    private readonly string apiId;
    private readonly string apiKey;
    private readonly SemaphoreSlim gate = new(1, 1);

    private ApiToken? cached;
    private bool disposed;

    /// <summary>Creates the provider.</summary>
    /// <param name="client">The auth client.</param>
    /// <param name="credentials">
    /// The credentials the AR4 bootstrap decrypted and validated. This type does not read a file and does
    /// not decrypt anything: by the time it exists, <c>CredentialBootstrapResult.Require ()</c> has
    /// already proved the pair works.
    /// </param>
    /// <param name="options">Base address and token timings.</param>
    /// <param name="clock">The clock.</param>
    /// <exception cref="ArgumentException">
    /// The credentials carry no API pair — which is the monitoring application's credential file, not the
    /// loader's. Thrown at construction rather than on the first request, so the run fails at startup with
    /// a message naming the template to re-seed from.
    /// </exception>
    public RcraInfoTokenProvider(
        IRcraInfoAuthClient client,
        ApplicationCredentials credentials,
        IOptions<RcraInfoApiOptions> options,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(credentials.ApiId) || string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new ArgumentException(
                "The credential file carries no RCRAInfo API ID and Key, so no token can be obtained. The "
                + "console application calls EPA on every run: seed it from the loader's "
                + "secrets.Template.json rather than the monitor's.",
                nameof(credentials));
        }

        this.client = client;
        this.options = options.Value;
        this.clock = clock;
        apiId = credentials.ApiId;
        apiKey = credentials.ApiKey;
    }

    /// <summary>How many times this provider has called EPA's auth endpoint.</summary>
    /// <remarks>
    /// Exposed because it is the only observable difference between "one renewal at a time" working and
    /// not working, and a behaviour that cannot be observed cannot be tested. Also worth logging at the
    /// end of a load: a count far above the run's duration divided by 20 minutes means something is
    /// invalidating tokens, and that is a question to ask EPA with evidence rather than a suspicion.
    /// </remarks>
    public int AuthenticationCount { get; private set; }

    /// <inheritdoc/>
    public async Task<ApiToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        ApiToken? snapshot = Volatile.Read(ref cached);

        if (snapshot is not null && !IsDue(snapshot))
        {
            return snapshot;
        }

        // `rejected: null` and not `snapshot`, which is the distinction the whole floor depends on: this
        // caller found the token due by ARITHMETIC, while a 401 caller has EVIDENCE that its token does not
        // work. Passing the due token as "rejected" here would exempt every proactive renewal from
        // MinimumRefreshInterval, and the clock-skew storm the floor exists to stop would run at full speed.
        return await RefreshAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ApiToken> RefreshAsync(
        ApiToken? rejected,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ApiToken? current = Volatile.Read(ref cached);

            if (current is not null && !ReferenceEquals(current, rejected) && !IsDue(current))
            {
                // Another caller renewed while this one waited. Two conditions, not one: a token that is
                // merely different is not enough (it could be the same stale one under a race), and a token
                // that is not due is not enough either (it could be the very token the caller was rejected
                // with, whose expiry has not arrived because EPA invalidated it early).
                return current;
            }

            if (current is not null
                && clock.GetUtcNow() - current.IssuedAt < options.MinimumRefreshInterval
                && !ReferenceEquals(current, rejected))
            {
                // Issued moments ago and not the token that failed: renewing it again would be the clock-skew
                // storm. Hand it back and let the caller use it. A token that WAS rejected is always renewed,
                // however new it is, because that path has evidence rather than arithmetic.
                return current;
            }

            ApiAuthResult result = await client
                .AuthenticateAsync(apiId, apiKey, cancellationToken)
                .ConfigureAwait(false);

            AuthenticationCount++;

            if (!result.Succeeded)
            {
                // Not cached, not retried here. The classification is what a caller needs, and the retry
                // decision belongs to whoever knows whether the load can afford to wait.
                throw new RcraInfoAuthException(result);
            }

            ApiToken fresh = result.Require();

            Volatile.Write(ref cached, fresh);

            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }

    private bool IsDue(ApiToken token) =>
        clock.GetUtcNow() >= options.RefreshDueAt(token.IssuedAt, token.ExpiresAt);
}
