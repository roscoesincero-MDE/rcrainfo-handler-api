using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RCRAInfo.Loader.Load;

/// <summary>Creates the journal for a run, once that run has an identifier.</summary>
/// <remarks>
/// A factory rather than a registration, because <c>logs.uspStartLoadRun</c> is what mints the
/// <c>LoadRunId</c> and it has to be called first — it is also the call that refuses a second concurrent
/// run. The alternative shapes are worse in the same way: a journal whose run identifier is set after
/// construction has a window in which it will buffer rows for run zero, and one that takes the identifier
/// on every method invites two runs sharing a buffer.
/// </remarks>
public interface ILoadJournalFactory
{
    /// <summary>Creates a journal for one run.</summary>
    /// <param name="loadRunId">The identifier <c>logs.uspStartLoadRun</c> returned.</param>
    /// <returns>The journal. The caller owns it and must dispose it.</returns>
    ILoadJournal Create(int loadRunId);
}

/// <summary>The real factory.</summary>
/// <param name="writer">The database seam.</param>
/// <param name="options">Flush thresholds, validated at startup.</param>
/// <param name="clock">The clock.</param>
/// <param name="logger">The journal's logger.</param>
public sealed class LoadJournalFactory(
    ILoadJournalWriter writer,
    IOptions<LoadJournalOptions> options,
    TimeProvider clock,
    ILogger<LoadJournal> logger) : ILoadJournalFactory
{
    /// <inheritdoc />
    public ILoadJournal Create(int loadRunId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(loadRunId, 1);

        return new LoadJournal(loadRunId, writer, options.Value, clock, logger);
    }
}
