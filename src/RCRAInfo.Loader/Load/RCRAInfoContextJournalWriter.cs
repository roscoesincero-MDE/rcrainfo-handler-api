using Microsoft.Extensions.Options;

using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="ILoadJournalWriter"/> over the real <see cref="RCRAInfoContext"/>.
/// </summary>
/// <remarks>
/// Nothing but forwarding, and that is the point: every decision the journal makes is in
/// <see cref="LoadJournal"/> where a test can see it, and this class holds only the part that needs a
/// database. If it ever grows a branch, the branch is untested by construction and belongs on the other
/// side of the seam.
/// </remarks>
/// <param name="context">The data context.</param>
/// <param name="options">The data options, for the payload element limit.</param>
public sealed class RCRAInfoContextJournalWriter(
    RCRAInfoContext context,
    IOptions<RCRAInfoDataOptions> options) : ILoadJournalWriter
{
    /// <inheritdoc />
    public int MaxElementsPerCall { get; } = options.Value.MaxPayloadElements;

    /// <inheritdoc />
    public async Task<int> UpsertStatusAsync(
        int loadRunId,
        string mode,
        IReadOnlyCollection<HandlerLoadStatusElement> elements,
        CancellationToken cancellationToken = default)
    {
        UpsertStatusResult result = await context
            .UpsertHandlerLoadStatusSetAsync(loadRunId, mode, elements, cancellationToken)
            .ConfigureAwait(false);

        return result.RowsAffected;
    }

    /// <inheritdoc />
    public async Task<AttemptWriteCounts> RecordAttemptsAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerLoadAttemptElement> elements,
        CancellationToken cancellationToken = default)
    {
        AttemptRecordResult result = await context
            .RecordHandlerLoadAttemptSetAsync(loadRunId, elements, cancellationToken)
            .ConfigureAwait(false);

        return new AttemptWriteCounts(result.RowsAffected, result.RowsOrphaned, result.ValuesWithheld);
    }
}
