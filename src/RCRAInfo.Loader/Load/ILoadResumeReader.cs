using RCRAInfo.Data.Results;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// The one database call the resume needs: <c>logs.uspGetHandlerLoadResumeSet</c> (script 525).
/// </summary>
/// <remarks>
/// A seam for the same reason <see cref="ILoadJournalWriter"/> is one — every decision the resume makes
/// is in <see cref="LoadResume"/> where a test can see it, and this interface holds only the part that
/// needs a database.
/// </remarks>
public interface ILoadResumeReader
{
    /// <summary>Reads the resume set for one activity location.</summary>
    /// <param name="activityLocation">The population being loaded.</param>
    /// <param name="loadRunId">A run to resume from, or <see langword="null"/> to let 525 choose.</param>
    /// <param name="abandonAfterMinutes">
    /// How long a run may sit at <c>Running</c> before it counts as killed. <b>The same value the run
    /// start is given</b> — see <see cref="LoadRunOptions.AbandonAfterMinutes"/>, which is why it is one
    /// setting.
    /// </param>
    /// <param name="maxAgeHours">
    /// How old a success may be and still be trusted. <see langword="null"/> for no limit.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One row per version the candidate run dealt with, in key order. <b>Empty means resume from
    /// nothing</b>, which is a normal answer and not an error.
    /// </returns>
    Task<IReadOnlyList<HandlerLoadResumeRow>> ReadAsync(
        string activityLocation,
        int? loadRunId,
        int? abandonAfterMinutes,
        int? maxAgeHours,
        CancellationToken cancellationToken = default);
}
