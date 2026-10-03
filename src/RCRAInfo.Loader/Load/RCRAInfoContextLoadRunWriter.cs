using Microsoft.Extensions.Options;

using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="ILoadRunWriter"/> over the real <see cref="RCRAInfoContext"/>.
/// </summary>
/// <remarks>
/// Forwarding only, with one exception that is arithmetic rather than a decision: the
/// <c>Unchanged</c> count in <see cref="MergeCounts"/> is script 400's documented subtraction, and it is
/// done here because this is the class that knows the procedure returns a row only for what it wrote.
/// Everything else lives in <see cref="LoadRun"/> where a test can see it.
/// </remarks>
/// <param name="context">The data context.</param>
/// <param name="options">The data options, for the payload element limit.</param>
public sealed class RCRAInfoContextLoadRunWriter(
    RCRAInfoContext context,
    IOptions<RCRAInfoDataOptions> options) : ILoadRunWriter
{
    /// <inheritdoc />
    public int MaxElementsPerCall { get; } = options.Value.MaxPayloadElements;

    /// <inheritdoc />
    public Task<LoadWatermark?> ReadWatermarkAsync(
        string feedName,
        string activityLocation,
        CancellationToken cancellationToken = default) =>
        context.GetLoadWatermarkAsync(feedName, activityLocation, cancellationToken);

    /// <inheritdoc />
    public Task<int> StartRunAsync(
        LoadRunRequest request,
        CancellationToken cancellationToken = default) =>
        context.StartLoadRunAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task CompleteRunAsync(
        int loadRunId,
        string status,
        string? failureMessage,
        LoadRunCounters counters,
        CancellationToken cancellationToken = default) =>
        context.CompleteLoadRunAsync(loadRunId, status, failureMessage, counters, cancellationToken);

    /// <inheritdoc />
    public async Task<MergeCounts> MergeAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        MergeBatchResult result = await context
            .MergeHandlerSourceBatchAsync(envelopes, loadRunId, cancellationToken)
            .ConfigureAwait(false);

        int inserted = 0;
        int updated = 0;

        foreach (MergeOutcomeRow row in result.Outcomes)
        {
            // Ordinal, and the two values are script 400's own literals. An outcome this code does not
            // recognise is counted as neither rather than as one of them: a wrong Inserted count in
            // logs.LoadRun is a reporting defect, and inventing a mapping would hide the divergence that
            // caused it.
            if (string.Equals(row.Outcome, "Inserted", StringComparison.Ordinal))
            {
                inserted++;
            }
            else if (string.Equals(row.Outcome, "Updated", StringComparison.Ordinal))
            {
                updated++;
            }
        }

        // The subtraction script 400 documents. Clamped at zero rather than trusted: more outcome rows than
        // envelopes sent would mean the procedure matched a version twice, and a negative Unchanged count
        // reaching logs.LoadRun would be read as a display bug rather than as that.
        int unchanged = Math.Max(0, envelopes.Count - result.Outcomes.Count);

        return new MergeCounts(inserted, updated, unchanged);
    }

    /// <inheritdoc />
    public async Task<int> SoftDeleteAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerKeyElement> keys,
        string reason,
        CancellationToken cancellationToken = default)
    {
        SoftDeleteResult result = await context
            .SoftDeleteHandlerSourceSetAsync(loadRunId, keys, reason, cancellationToken)
            .ConfigureAwait(false);

        return result.RowsAffected;
    }

    /// <inheritdoc />
    public async Task<ReconcileCounts> ReconcileAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerVersionElement> summaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        ReconcileResult result = await context
            .ReconcileCurrentRecordAsync(loadRunId, summaries, cancellationToken)
            .ConfigureAwait(false);

        // The PayloadBatch on the result stops here. It is the JSON body that was sent, and the orchestrator
        // neither needs it nor should hold it -- see ReconcileCounts.
        return new ReconcileCounts(result.RowsAffected, result.Observations);
    }

    /// <inheritdoc />
    public Task AdvanceWatermarkAsync(
        string feedName,
        string activityLocation,
        DateOnly watermarkDate,
        int loadRunId,
        CancellationToken cancellationToken = default) =>
        context.SetLoadWatermarkAsync(
            feedName,
            activityLocation,
            watermarkDate,
            loadRunId,
            cancellationToken: cancellationToken);
}
