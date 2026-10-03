using Microsoft.Extensions.Options;

using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="ILookupRefreshWriter"/> over the real <see cref="RCRAInfoContext"/>.
/// </summary>
/// <remarks>
/// Forwarding only, for the reason <see cref="RCRAInfoContextJournalWriter"/> gives: a branch on this side
/// of the seam is untested by construction.
/// </remarks>
/// <param name="context">The data context.</param>
/// <param name="options">The data options, for the payload element limit.</param>
public sealed class RCRAInfoContextLookupWriter(
    RCRAInfoContext context,
    IOptions<RCRAInfoDataOptions> options) : ILookupRefreshWriter
{
    /// <inheritdoc />
    public int MaxElementsPerCall { get; } = options.Value.MaxPayloadElements;

    /// <inheritdoc />
    public async Task<LookupWriteCounts> RefreshAsync(
        int loadRunId,
        string lookupName,
        string mode,
        IReadOnlyCollection<LookupElement> elements,
        CancellationToken cancellationToken = default)
    {
        LookupRefreshResult result = await context
            .RefreshLookupSetAsync(loadRunId, lookupName, mode, elements, cancellationToken)
            .ConfigureAwait(false);

        return new LookupWriteCounts(result.RowsAffected, result.RetiredRows, result.ChildRows);
    }
}
