using Microsoft.Extensions.Logging;

using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Buffers <c>logs.HandlerLoadStatus</c> and <c>logs.HandlerLoadAttempt</c> rows and writes them as
/// sets, on whichever comes first — a row count or a time interval.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why buffer at all.</b> Both tables are written per handler version and there are several hundred
/// thousand of them. Script 520 and script 524 are set-based procedures precisely so that a run does not
/// make one round-trip per handler; writing through them one element at a time would keep every design
/// decision in the database and throw away the reason for it.
/// </para>
/// <para>
/// <b>The flush order is fixed and it is load-bearing: Enumerate, Attempt, Fail, Skip, then the attempt
/// rows.</b> Three separate reasons, any one of which would be enough.
/// </para>
/// <list type="number">
/// <item>
/// Script 524 resolves each attempt element to a status row <i>of the same run</i> and counts the ones it
/// cannot as <c>@RowsOrphaned</c>. Writing the attempt rows before the <c>Enumerate</c> rows that name
/// their versions would report a defect for a load that was working.
/// </item>
/// <item>
/// A version can be in both the <c>Attempt</c> buffer and the <c>Fail</c> buffer in one flush — attempt
/// one failed and the caller gave up. <c>Attempt</c> leaves it <c>InProgress</c>, <c>Fail</c> leaves it
/// <c>Failed</c>; applied the other way round the row ends the run reading <c>InProgress</c> with a
/// failure nobody can see.
/// </item>
/// <item>
/// The same version can be enumerated and attempted in one flush, and <c>Enumerate</c> is the mode that
/// creates the row the other three update.
/// </item>
/// </list>
/// <para>
/// <b>There is no background timer, and that is a decision rather than an omission.</b>
/// <see cref="LoadJournalOptions.FlushInterval"/> is checked when a row is buffered. A timer would fire a
/// flush concurrently with the fetch loop, which leaves two choices — hold the buffer lock across a
/// database round-trip, blocking every fetch, or let two flushes race and reorder the modes, which
/// defeats the paragraph above. What the checked-on-buffer form gives up is bounded staleness while the
/// run is genuinely idle: a long <c>Retry-After</c> can leave rows buffered for the length of the wait.
/// Nothing is lost by that — <see cref="DisposeAsync"/> flushes unconditionally — and the staleness is
/// bounded by the wait, which the run summary records anyway.
/// </para>
/// <para>
/// <b>A flush triggered by the row count blocks the caller, deliberately.</b> That is backpressure: if
/// the database cannot keep up with the fetches, the right response is fewer fetches, not an unbounded
/// buffer of rows describing work whose record may never be written.
/// </para>
/// </remarks>
/// <param name="loadRunId">The run being journalled, from <c>logs.uspStartLoadRun</c>.</param>
/// <param name="writer">The two database calls.</param>
/// <param name="options">Flush thresholds.</param>
/// <param name="clock">The clock, injected so the interval is testable without waiting.</param>
/// <param name="logger">
/// Counts and identifiers only. Nothing here logs a payload, a path or a query string (AR8).
/// </param>
public sealed class LoadJournal(
    int loadRunId,
    ILoadJournalWriter writer,
    LoadJournalOptions options,
    TimeProvider clock,
    ILogger<LoadJournal> logger) : ILoadJournal
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim flushGate = new(1, 1);

    private readonly Buffer<HandlerVersion, HandlerLoadStatusElement> enumerated = new();
    private readonly Buffer<HandlerVersion, HandlerLoadStatusElement> attempted = new();
    private readonly Buffer<HandlerVersion, HandlerLoadStatusElement> failed = new();
    private readonly Buffer<HandlerVersion, HandlerLoadStatusElement> skipped = new();
    private readonly Buffer<(HandlerVersion Version, int Attempt), HandlerLoadAttemptElement> attempts = new();

    private DateTimeOffset lastFlushUtc = clock.GetUtcNow();
    private LoadJournalFlush total;
    private bool disposed;

    /// <inheritdoc />
    public int PendingRows
    {
        get
        {
            lock (gate)
            {
                return enumerated.Count + attempted.Count + failed.Count + skipped.Count + attempts.Count;
            }
        }
    }

    /// <inheritdoc />
    public LoadJournalFlush Total
    {
        get
        {
            lock (gate)
            {
                return total;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask EnumerateAsync(
        IEnumerable<HandlerLoadStatusElement> versions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);

        lock (gate)
        {
            foreach (HandlerLoadStatusElement element in versions)
            {
                enumerated.Set(KeyOf(element), element);
            }
        }

        await FlushIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordAttemptAsync(
        ApiFetchResult result,
        HandlerVersion version,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

        HandlerLoadAttemptElement attempt = result.ToAttemptElement(
            version.HandlerId, version.SourceType, version.Sequence, attemptNumber);

        // Attempt mode SETS AttemptCount rather than incrementing it, so the buffered status row carries
        // the number and a re-sent flush cannot inflate it. Nothing else about the call belongs here: the
        // status row is the current state, and the detail of this particular call is the attempt row.
        HandlerLoadStatusElement status = new()
        {
            HandlerId = version.HandlerId,
            SourceType = version.SourceType,
            Sequence = version.Sequence,
            AttemptNumber = attemptNumber,
        };

        lock (gate)
        {
            attempts.Set((version, attemptNumber), attempt);
            attempted.Set(version, status);
        }

        await FlushIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ConcludeAsync(
        ApiFetchResult result,
        HandlerVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        string status = result.Outcome.ToStatus();

        if (status == "Succeeded")
        {
            // Script 520 has four modes and none of them is a success. dbo.uspMergeHandlerSourceBatch
            // writes Succeeded/Inserted|Updated|Unchanged inside the transaction that committed the
            // version; dbo.uspSoftDeleteHandlerSourceSet writes Succeeded/SoftDeleted inside the
            // transaction that deleted it. Both are claims that are only true if the thing they name
            // committed, which is exactly what a buffered write-behind cannot promise. So the journal
            // declines, and says so, and the caller still owes this version one of those two calls.
            return false;
        }

        HandlerLoadStatusElement element = ToFailureElement(result, version);

        lock (gate)
        {
            if (status == "Skipped")
            {
                skipped.Set(version, element);
            }
            else
            {
                failed.Set(version, element);
            }
        }

        await FlushIfDueAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async ValueTask SkipAsync(
        IEnumerable<HandlerVersion> versions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);

        lock (gate)
        {
            foreach (HandlerVersion version in versions)
            {
                // The natural key and nothing else. Script 520's Skip mode has no reason column, and
                // there is no error to describe -- see the interface, where the alternative (routing a
                // synthesised ApiFetchResult through ConcludeAsync) and what it would write are set out.
                skipped.Set(
                    version,
                    new HandlerLoadStatusElement
                    {
                        HandlerId = version.HandlerId,
                        SourceType = version.SourceType,
                        Sequence = version.Sequence,
                    });
            }
        }

        await FlushIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LoadJournalFlush> FlushAsync(CancellationToken cancellationToken = default)
    {
        // Serialised rather than reentrant: two concurrent flushes would interleave the modes and undo
        // the ordering this class exists to guarantee. The buffers are drained after the gate is taken,
        // so a caller that waited here writes whatever accumulated while it waited -- or nothing.
        await flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Pending pending;

            lock (gate)
            {
                pending = new Pending(
                    enumerated.Drain(),
                    attempted.Drain(),
                    failed.Drain(),
                    skipped.Drain(),
                    attempts.Drain());
            }

            LoadJournalFlush flush = await WriteAsync(pending, cancellationToken).ConfigureAwait(false);

            lock (gate)
            {
                lastFlushUtc = clock.GetUtcNow();
                total = total.Add(flush);
            }

            if (flush.HasDefects)
            {
                // Neither procedure fails the flush over these -- both write what they can and report what
                // they could not, because refusing would lose the rows for every other handler in the same
                // buffer. Which makes surfacing the report the only signal there is.
                LoadJournalLog.AttemptLogDefects(
                    logger, loadRunId, flush.RowsOrphaned, flush.ValuesWithheld);
            }

            return flush;
        }
        finally
        {
            flushGate.Release();
        }
    }

    /// <summary>Flushes whatever is buffered, and does not let the attempt end the run.</summary>
    /// <returns>A task that completes when the flush has been attempted.</returns>
    /// <remarks>
    /// <para>
    /// <b>The flush is unconditional and its failure is swallowed, and those are two separate
    /// decisions.</b> Unconditional, and with <see cref="CancellationToken.None"/>, because the run that
    /// most needs its journal written is the one being shut down: a buffered status set lost on exit is
    /// invisible data loss in the one table that exists to make loss visible, and a cancelled run's
    /// buffer is the record of which versions still need fetching.
    /// </para>
    /// <para>
    /// Swallowed because a throw from <c>DisposeAsync</c> while the stack is unwinding from another
    /// exception <i>replaces</i> it — so the failure that ended the run would be overwritten by the
    /// failure to write about it, which is the less useful of the two by a wide margin. The orchestrator
    /// is expected to call <see cref="FlushAsync"/> on both its own exit paths, where the error is
    /// visible; this is the net under that.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        int pending = PendingRows;

        try
        {
            if (pending > 0)
            {
                LoadJournalFlush flush = await FlushAsync(CancellationToken.None).ConfigureAwait(false);

                LoadJournalLog.FlushedAtShutdown(logger, loadRunId, pending, flush.ToString());
            }
        }
#pragma warning disable CA1031 // Deliberate: see the remarks. A throw here replaces the run's real failure.
        catch (Exception error)
        {
            LoadJournalLog.FinalFlushFailed(logger, error, loadRunId, pending);
        }
#pragma warning restore CA1031
        finally
        {
            flushGate.Dispose();
        }
    }

    /// <summary>
    /// Builds the <c>Fail</c>/<c>Skip</c> element, and guarantees script 520's one-of-three requirement.
    /// </summary>
    /// <remarks>
    /// <b>The synthetic <c>ApiErrorCode</c> is the interesting line.</b> Script 520 refuses a <c>Fail</c>
    /// element carrying none of <c>httpStatusCode</c>, <c>apiErrorCode</c> or <c>apiErrorMessage</c>, and
    /// a transport failure has none of the three: no response, so no status code, and no RCRAInfo error
    /// document to read a code out of. Left alone that would throw from inside the flush and take the
    /// rows for every other handler in the buffer with it — a connectivity blip turning into lost
    /// journal. The prefix is there so nothing reads it as one of EPA's codes; the outcome name is the
    /// diagnosis, and <c>logs.HandlerLoadAttempt.FailureMessage</c> carries the detail.
    /// </remarks>
    private static HandlerLoadStatusElement ToFailureElement(ApiFetchResult result, HandlerVersion version)
    {
        bool hasDetail = result.HttpStatusCode is not null
            || result.ApiErrorCode is not null
            || result.ApiErrorMessage is not null;

        return new HandlerLoadStatusElement
        {
            HandlerId = version.HandlerId,
            SourceType = version.SourceType,
            Sequence = version.Sequence,
            HttpStatusCode = result.HttpStatusCode,
            ApiErrorCode = hasDetail ? result.ApiErrorCode : $"LOADER-{result.Outcome}",
            ApiErrorMessage = result.ApiErrorMessage,
            ApiErrorId = result.ApiErrorId,
            ApiErrorDate = result.ApiErrorDate,
        };
    }

    private static HandlerVersion KeyOf(HandlerLoadStatusElement element) =>
        new(element.HandlerId, element.SourceType, element.Sequence);

    private ValueTask FlushIfDueAsync(CancellationToken cancellationToken)
    {
        bool due;

        lock (gate)
        {
            int pending = enumerated.Count + attempted.Count + failed.Count + skipped.Count + attempts.Count;

            due = pending >= options.FlushRowCount
                || clock.GetUtcNow() - lastFlushUtc >= options.FlushInterval;
        }

        return due ? new ValueTask(FlushAsync(cancellationToken)) : ValueTask.CompletedTask;
    }

    private async Task<LoadJournalFlush> WriteAsync(Pending pending, CancellationToken cancellationToken)
    {
        LoadJournalFlush flush = LoadJournalFlush.Empty;

        // The order below is the whole point of this method. See the remarks on the class.
        (int rows, int calls) = await UpsertAsync("Enumerate", pending.Enumerated, cancellationToken)
            .ConfigureAwait(false);
        flush = flush with { Enumerated = rows, Calls = flush.Calls + calls };

        (rows, calls) = await UpsertAsync("Attempt", pending.Attempted, cancellationToken)
            .ConfigureAwait(false);
        flush = flush with { Attempted = rows, Calls = flush.Calls + calls };

        (rows, calls) = await UpsertAsync("Fail", pending.Failed, cancellationToken).ConfigureAwait(false);
        flush = flush with { Failed = rows, Calls = flush.Calls + calls };

        (rows, calls) = await UpsertAsync("Skip", pending.Skipped, cancellationToken).ConfigureAwait(false);
        flush = flush with { Skipped = rows, Calls = flush.Calls + calls };

        foreach (HandlerLoadAttemptElement[] chunk in Chunk(pending.Attempts))
        {
            AttemptWriteCounts counts = await writer
                .RecordAttemptsAsync(loadRunId, chunk, cancellationToken)
                .ConfigureAwait(false);

            flush = flush with
            {
                AttemptRows = flush.AttemptRows + counts.RowsWritten,
                RowsOrphaned = flush.RowsOrphaned + counts.RowsOrphaned,
                ValuesWithheld = flush.ValuesWithheld + counts.ValuesWithheld,
                Calls = flush.Calls + 1,
            };
        }

        return flush;
    }

    private async Task<(int Rows, int Calls)> UpsertAsync(
        string mode,
        HandlerLoadStatusElement[] elements,
        CancellationToken cancellationToken)
    {
        int rows = 0;
        int calls = 0;

        foreach (HandlerLoadStatusElement[] chunk in Chunk(elements))
        {
            rows += await writer
                .UpsertStatusAsync(loadRunId, mode, chunk, cancellationToken)
                .ConfigureAwait(false);

            calls++;
        }

        return (rows, calls);
    }

    /// <summary>Splits a buffer into calls no wider than the writer accepts.</summary>
    /// <remarks>
    /// <para>
    /// <b>Chunking is not redundant with <see cref="LoadJournalOptions.FlushRowCount"/>, which is why it
    /// is unconditional.</b> Two things arrive larger than the threshold. One
    /// <see cref="EnumerateAsync"/> call carries every version a summaries page named, which is a page
    /// size and not a flush size; and <see cref="LoadJournalOptions.FlushRowCount"/> is configuration, so it can be set
    /// above the writer's limit. Above that limit <c>PayloadJson.Serialize</c> throws
    /// <c>ArgumentOutOfRangeException</c> — a refusal, not a truncation, but still a whole flush lost to
    /// a setting.
    /// </para>
    /// <para>
    /// An empty buffer yields nothing, so a flush with nothing to write makes no call at all rather than
    /// sending script 520 an empty array to no-op on.
    /// </para>
    /// </remarks>
    private IEnumerable<T[]> Chunk<T>(T[] elements) =>
        elements.Length == 0
            ? []
            : elements.Chunk(writer.MaxElementsPerCall);

    private readonly record struct Pending(
        HandlerLoadStatusElement[] Enumerated,
        HandlerLoadStatusElement[] Attempted,
        HandlerLoadStatusElement[] Failed,
        HandlerLoadStatusElement[] Skipped,
        HandlerLoadAttemptElement[] Attempts);

    /// <summary>
    /// An insertion-ordered, last-write-wins buffer keyed by a handler version.
    /// </summary>
    /// <remarks>
    /// <b>A plain list would be wrong, not merely untidy.</b> Script 520 <i>throws</i> when one payload
    /// names the same <c>(HandlerId, SourceType, Sequence)</c> twice — deliberately, because two rows for
    /// one key would make the outcome depend on which the engine applied last — and buffering attempt 1
    /// and then attempt 2 of the same version between two flushes produces exactly that. Replacing in
    /// place is also the right semantics: this table holds current state, so attempt 2 supersedes attempt
    /// 1 rather than accompanying it. Every call that happened is still kept, in the attempt buffer,
    /// whose key includes the attempt number.
    /// </remarks>
    private sealed class Buffer<TKey, TValue>
        where TKey : notnull
    {
        private readonly List<TValue> items = [];
        private readonly Dictionary<TKey, int> index = [];

        public int Count => items.Count;

        public void Set(TKey key, TValue value)
        {
            if (index.TryGetValue(key, out int at))
            {
                items[at] = value;
                return;
            }

            index[key] = items.Count;
            items.Add(value);
        }

        public TValue[] Drain()
        {
            TValue[] drained = [.. items];

            items.Clear();
            index.Clear();

            return drained;
        }
    }
}
