using RCRAInfo.Data;
using RCRAInfo.Data.Results;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="ILoadResumeReader"/> over the real <see cref="RCRAInfoContext"/>.
/// </summary>
/// <remarks>
/// Nothing but forwarding, deliberately: if it ever grows a branch, the branch is untested by
/// construction and belongs on the other side of the seam.
/// </remarks>
/// <param name="context">The data context.</param>
public sealed class RCRAInfoContextResumeReader(RCRAInfoContext context) : ILoadResumeReader
{
    /// <inheritdoc />
    public Task<IReadOnlyList<HandlerLoadResumeRow>> ReadAsync(
        string activityLocation,
        int? loadRunId,
        int? abandonAfterMinutes,
        int? maxAgeHours,
        CancellationToken cancellationToken = default) =>
        context.GetHandlerLoadResumeSetAsync(
            activityLocation, loadRunId, abandonAfterMinutes, maxAgeHours, cancellationToken);
}
