using Microsoft.Extensions.Options;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The rate gate, asserted through its arithmetic rather than through a stopwatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>Reserve</c> and not <c>AcquireAsync</c> for the spacing tests.</b> This suite's <c>TestClock</c>
/// overrides <c>GetUtcNow</c> only, so a <c>Task.Delay</c> driven by it falls through to a real timer, and
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> is deliberately not referenced — a package added for two
/// members is a package to keep up to date forever. So the pacer was designed to put its whole decision in
/// one method that takes an instant and returns a wait. A test can then assert two-requests-per-second
/// exactly, in microseconds of wall-clock time, where a test measuring a real 500ms delay would be slower and
/// prove less.
/// </para>
/// <para>
/// <see cref="AcquireAsync"/> is still exercised, for the two things arithmetic cannot show: that the
/// concurrency ceiling actually blocks, and that a cancelled wait does not leak the lease it had taken.
/// </para>
/// </remarks>
public class RequestPacerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstRequestWaitsForNothing()
    {
        using RequestPacer pacer = Build();

        Assert.Equal(TimeSpan.Zero, pacer.Reserve(Start));
    }

    [Fact]
    public void TwoRequestsPerSecondMeansHalfASecondApart()
    {
        // The configured default, and the number the user supplied for G21. Asserted as an interval rather
        // than as a rate because the interval is what the code actually computes.
        using RequestPacer pacer = Build();

        Assert.Equal(TimeSpan.Zero, pacer.Reserve(Start));
        Assert.Equal(TimeSpan.FromMilliseconds(500), pacer.Reserve(Start));
        Assert.Equal(TimeSpan.FromSeconds(1), pacer.Reserve(Start));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), pacer.Reserve(Start));
    }

    [Fact]
    public void AFractionalRateIsExactRatherThanRoundedToNothing()
    {
        // Half a request per second is the setting a measured 429 problem calls for, so it has to work. A rate
        // computed in whole seconds would round this to a one-second interval, twice as fast as asked.
        using RequestPacer pacer = Build(maxRequestsPerSecond: 0.5);

        Assert.Equal(TimeSpan.Zero, pacer.Reserve(Start));
        Assert.Equal(TimeSpan.FromSeconds(2), pacer.Reserve(Start));
    }

    [Fact]
    public void IdleTimeEarnsNoCreditSoAPausedRunDoesNotBurstOnResuming()
    {
        // A token bucket would hand out a full bucket here, and a sudden burst after a quiet period is the
        // traffic shape an automated abuse filter is built to look for. The baseline is max(now, next), never
        // next alone, and this is the test of that max.
        using RequestPacer pacer = Build();

        Assert.Equal(TimeSpan.Zero, pacer.Reserve(Start));

        DateTimeOffset anHourLater = Start.AddHours(1);

        Assert.Equal(TimeSpan.Zero, pacer.Reserve(anHourLater));
        Assert.Equal(TimeSpan.FromMilliseconds(500), pacer.Reserve(anHourLater));
    }

    [Fact]
    public void AWaitIsNeverNegativeWhenTheClockHasMovedPastTheReservation()
    {
        using RequestPacer pacer = Build();

        pacer.Reserve(Start);

        // Exactly one interval later: due now, not overdue by a negative amount. A negative TimeSpan handed
        // to Task.Delay throws, and it would throw inside a message handler on the load's happy path.
        Assert.Equal(TimeSpan.Zero, pacer.Reserve(Start.AddMilliseconds(500)));
        Assert.Equal(TimeSpan.Zero, pacer.Reserve(Start.AddSeconds(30)));
    }

    [Fact]
    public void ReservationsAreCountedSoTheGateCanBeShownToHaveBeenConsulted()
    {
        using RequestPacer pacer = Build();

        pacer.Reserve(Start);
        pacer.Reserve(Start);

        Assert.Equal(2, pacer.Reservations);
        Assert.Equal(Start.AddSeconds(1), pacer.NextPermittedUtc);
    }

    [Fact]
    public async Task TheConcurrencyCeilingBlocksTheRequestPastIt()
    {
        // Requests in flight, not requests started -- the other half of what MaxConcurrentRequests means. At
        // a ceiling of one, the second Acquire cannot complete until the first lease is disposed.
        using RequestPacer pacer = Build(maxRequestsPerSecond: 1000, maxConcurrentRequests: 1);

        IDisposable first = await pacer.AcquireAsync();

        Task<IDisposable> second = pacer.AcquireAsync();

        Assert.False(second.IsCompleted);

        first.Dispose();

        using IDisposable held = await second;
        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ACancelledWaitDoesNotLeakTheConcurrencySlotItHadTaken()
    {
        // A leaked lease permanently shrinks the ceiling, and the symptom -- a resumed run that crawls -- has
        // no log line and no configuration value to explain it. Cancellation is the ordinary case here: it is
        // how an interrupted overnight run ends.
        //
        // The ordering is built rather than hoped for. A caller cancelled while queued for a concurrency slot
        // holds nothing and could not leak; the leak is only possible for one already past the semaphore and
        // inside its RATE wait. So the first lease is released to free the slot, and the rate is set slow
        // enough (one request every fifty seconds) that the next caller is certainly still waiting.
        (RequestPacer pacer, TestClock clock) = BuildWithClock(
            maxRequestsPerSecond: 0.02, maxConcurrentRequests: 1);

        using (pacer)
        {
            using CancellationTokenSource source = new();

            (await pacer.AcquireAsync()).Dispose();

            Task<IDisposable> waiting = pacer.AcquireAsync(source.Token);

            Assert.False(waiting.IsCompleted);

            await source.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            // Past every reservation made so far, so this last acquire waits on the concurrency ceiling and
            // on nothing else. If the cancelled caller kept its slot, it never completes.
            clock.Advance(TimeSpan.FromMinutes(10));

            Task<IDisposable> after = pacer.AcquireAsync();
            Task first = await Task.WhenAny(after, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(after, first);
            (await after).Dispose();
        }
    }

    [Fact]
    public async Task DisposingALeaseTwiceDoesNotRaiseTheCeiling()
    {
        // A message handler wrapped in a retry pipeline is exactly the shape that produces a double dispose,
        // and the symptom would be a load that gets FASTER the more it fails.
        using RequestPacer pacer = Build(maxRequestsPerSecond: 1000, maxConcurrentRequests: 1);

        IDisposable lease = await pacer.AcquireAsync();
        lease.Dispose();
        lease.Dispose();

        using IDisposable next = await pacer.AcquireAsync();

        Task<IDisposable> beyond = pacer.AcquireAsync();

        Assert.False(beyond.IsCompleted);

        next.Dispose();
        (await beyond).Dispose();
    }

    [Fact]
    public async Task EveryAcquireIsCountedIncludingTheOneThatWaited()
    {
        using RequestPacer pacer = Build(maxRequestsPerSecond: 1000, maxConcurrentRequests: 2);

        (await pacer.AcquireAsync()).Dispose();
        (await pacer.AcquireAsync()).Dispose();

        Assert.Equal(2, pacer.Reservations);
    }

    [Fact]
    public void UnusableOptionsThrowAtConstructionRatherThanAtTheFirstRequest()
    {
        // A pacer built from unusable options would have to pick a rate on its own, and every choice is
        // wrong: a default hides that the configuration was ignored, and no limit turns a typo in
        // appsettings.json into an unthrottled overnight run against a government service.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new RequestPacer(
                Options.Create(new RcraInfoThrottleOptions { MaxRequestsPerSecond = 0 }),
                new TestClock(Start)));

        Assert.Contains("MaxRequestsPerSecond", error.Message, StringComparison.Ordinal);
    }

    private static RequestPacer Build(double maxRequestsPerSecond = 2, int maxConcurrentRequests = 2) =>
        BuildWithClock(maxRequestsPerSecond, maxConcurrentRequests).Pacer;

    private static (RequestPacer Pacer, TestClock Clock) BuildWithClock(
        double maxRequestsPerSecond,
        int maxConcurrentRequests)
    {
        TestClock clock = new(Start);

        RequestPacer pacer = new(
            Options.Create(
                new RcraInfoThrottleOptions
                {
                    MaxRequestsPerSecond = maxRequestsPerSecond,
                    MaxConcurrentRequests = maxConcurrentRequests,
                }),
            clock);

        return (pacer, clock);
    }
}
