using Microsoft.Extensions.Options;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Bounds how many requests this process has open at EPA, and how closely together it starts them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reserve a slot, then wait for it.</b> A caller takes a concurrency lease first, then reserves the next
/// permitted send time under a short lock, then waits out its own reservation with the lock released. The
/// obvious alternative — hold the lock across the wait — spaces requests just as well and serialises the
/// waiting, so at two concurrent requests the second one's wait would begin only after the first one's ended
/// and the effective rate would be half of what was configured. Reserving makes each caller's wait
/// independent and the spacing exact.
/// </para>
/// <para>
/// <b>A reservation is spent whether or not it is used.</b> If a caller is cancelled between reserving and
/// sending, the slot goes unused and the next request starts a beat late. That is the correct direction to
/// fail: an unused slot is a gap, and the alternative — returning slots to the pool — is the bookkeeping that
/// produces a burst at exactly the moment a run is being interrupted.
/// </para>
/// <para>
/// <b>No credit accrues for idle time.</b> The clock is only ever pushed <i>forward</i> from now, so an hour
/// of quiet buys nothing. See <see cref="RcraInfoThrottleOptions.MaxRequestsPerSecond"/>: a saved-up burst
/// after a pause is the traffic shape most likely to be read as abuse at the far end, and it is also the one
/// a token bucket produces by design.
/// </para>
/// <para>
/// <b>Testable without a fake timer.</b> The arithmetic is in <see cref="Reserve"/>, which takes the current
/// instant, returns the wait it computed and moves <see cref="NextPermittedUtc"/> — so the spacing this class
/// produces is assertable by calling it, with no wall-clock time and no timer double. That matters because
/// the suite's <c>TestClock</c> overrides <c>GetUtcNow</c> only; a test that went through
/// <see cref="AcquireAsync"/> to check spacing would be measuring a real timer.
/// </para>
/// <para>
/// <b>Registered as a singleton, and it has to be.</b> Two pacers are two independent rate limits, and EPA
/// sees their sum. This is also why the gate is enforced in a message handler rather than in the fetch loop —
/// see <see cref="RequestPacingHandler"/>.
/// </para>
/// </remarks>
public sealed class RequestPacer : IDisposable
{
    private readonly RcraInfoThrottleOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim concurrency;

    /// <summary>Guards <see cref="nextPermittedUtc"/> only. Never held across a wait.</summary>
    private readonly object gate = new();

    private DateTimeOffset nextPermittedUtc;
    private long reservations;
    private bool disposed;

    /// <summary>Creates the pacer.</summary>
    /// <param name="options">The configured rate and concurrency ceiling.</param>
    /// <param name="clock">The time source. <c>TimeProvider.System</c> outside tests.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="InvalidOperationException">The throttle options are unusable.</exception>
    public RequestPacer(IOptions<RcraInfoThrottleOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        this.options = options.Value ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock;

        IReadOnlyList<string> problems = this.options.Validate();

        if (problems.Count > 0)
        {
            // Thrown at construction rather than tolerated at the first request. A pacer built from unusable
            // options would have to pick a rate on its own, and every plausible choice is wrong: falling back
            // to a default hides that the configuration was ignored, and falling back to no limit turns a
            // typo in appsettings.json into an unthrottled overnight run against a government service.
            throw new InvalidOperationException(
                "The RCRAInfoApi:Throttle configuration is not usable, so no request rate can be honoured: "
                + string.Join(" ", problems));
        }

        this.concurrency = new SemaphoreSlim(
            this.options.MaxConcurrentRequests, this.options.MaxConcurrentRequests);
        this.nextPermittedUtc = clock.GetUtcNow();
    }

    /// <summary>The earliest instant at which the next reservation may send.</summary>
    /// <remarks>Exposed to make <see cref="Reserve"/>'s effect assertable. Not for callers to act on.</remarks>
    public DateTimeOffset NextPermittedUtc
    {
        get
        {
            lock (this.gate)
            {
                return this.nextPermittedUtc;
            }
        }
    }

    /// <summary>How many slots have been handed out since the process started.</summary>
    /// <remarks>
    /// A count of requests this loader <i>decided</i> to send, which is deliberately not the same as the
    /// count of HTTP requests EPA answered: a reservation is spent even when the caller is then cancelled.
    /// Useful for a run summary and for a test that wants to know the gate was actually consulted.
    /// </remarks>
    public long Reservations => Interlocked.Read(ref this.reservations);

    /// <summary>Waits until this caller may send, then holds a concurrency slot until disposed.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The lease. Dispose it once the response — including its body — has been read.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled while waiting.</exception>
    /// <remarks>
    /// Concurrency is taken <i>before</i> the rate reservation, so a queue of waiting callers holds no
    /// reservations. The other order would let a hundred queued callers each book a send time, and the
    /// hundredth would then be waiting out fifty seconds of other callers' reservations rather than waiting
    /// for a free slot — a wait that looks like a hung request and is really a booking made too early.
    /// </remarks>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        Lease lease = new(this.concurrency);

        try
        {
            TimeSpan wait = this.Reserve(this.clock.GetUtcNow());

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, this.clock, cancellationToken).ConfigureAwait(false);
            }

            return lease;
        }
        catch
        {
            // The lease must not leak on a cancelled wait, or a cancelled run would permanently shrink the
            // concurrency ceiling and a resumed run would crawl for reasons nothing logs.
            lease.Dispose();

            throw;
        }
    }

    /// <summary>Books the next send slot and reports how long the caller must wait for it.</summary>
    /// <param name="now">The current instant, from the caller's clock.</param>
    /// <returns>The wait, never negative.</returns>
    /// <remarks>
    /// <para>
    /// The whole rate limiter, in five lines and with no timer in sight — which is the point. Call it twice
    /// with the same <paramref name="now"/> and the second call returns one interval; call it after the
    /// interval has elapsed and it returns zero, because the baseline is <c>max(now, nextPermitted)</c> and
    /// never <c>nextPermitted</c> alone. That <c>max</c> is what refuses to accumulate credit for idle time.
    /// </para>
    /// <para>
    /// Internal rather than private so the spacing can be asserted directly; it is not part of the pacing
    /// contract and callers use <see cref="AcquireAsync"/>.
    /// </para>
    /// </remarks>
    internal TimeSpan Reserve(DateTimeOffset now)
    {
        Interlocked.Increment(ref this.reservations);

        lock (this.gate)
        {
            DateTimeOffset sendAt = this.nextPermittedUtc > now ? this.nextPermittedUtc : now;

            this.nextPermittedUtc = sendAt + this.options.RequestInterval;

            TimeSpan wait = sendAt - now;

            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.concurrency.Dispose();
    }

    /// <summary>One caller's hold on a concurrency slot.</summary>
    /// <remarks>
    /// Releases at most once. A double dispose — which a message handler wrapped in a retry pipeline is
    /// exactly the shape to produce — would otherwise <i>raise</i> the concurrency ceiling every time it
    /// happened, and the symptom would be a load that gets faster the more it fails.
    /// </remarks>
    private sealed class Lease(SemaphoreSlim concurrency) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                concurrency.Release();
            }
        }
    }
}
