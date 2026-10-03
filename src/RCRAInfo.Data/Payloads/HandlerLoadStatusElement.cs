namespace RCRAInfo.Data.Payloads;

/// <summary>
/// One element of <c>logs.uspUpsertHandlerLoadStatusSet</c>'s <c>@Elements</c> array (script 520).
/// </summary>
/// <remarks>
/// <para>
/// One type serves all four of that procedure's modes, because the procedure takes one payload shape
/// for all four. Which properties matter depends on the mode: <c>Enumerate</c> needs only the
/// natural key, <c>Attempt</c> adds <see cref="AttemptNumber"/>, and <c>Fail</c> requires at least
/// one of <see cref="HttpStatusCode"/>, <see cref="ApiErrorCode"/> or
/// <see cref="ApiErrorMessage"/> — a failed row carrying none of the three tells an operator only
/// that something went wrong, which is already visible from the status.
/// </para>
/// <para>
/// <see cref="AttemptNumber"/> is the loader's own count and is <i>assigned</i> by the procedure
/// rather than incremented, deliberately: the completion update runs after the commit, so a caller
/// can be made to retry a call that already committed, and an incrementing counter would then claim
/// an attempt that never happened in the one column an operator uses to judge whether a handler is
/// stuck.
/// </para>
/// <para>
/// <see cref="ApiErrorMessage"/> is EPA's text and is excluded from that procedure's
/// <c>@KeyParameters</c> by name (AR8). Nothing in this project copies it into a log message.
/// </para>
/// </remarks>
public sealed class HandlerLoadStatusElement
{
    /// <summary>The handler's EPA identifier. Part of the natural key; required.</summary>
    public string HandlerId { get; init; } = null!;

    /// <summary>The state whose activity this is — <c>MD</c> in scope (G2).</summary>
    public string? ActivityLocation { get; init; }

    /// <summary>The source-type code. Part of the natural key; required.</summary>
    public string SourceType { get; init; } = null!;

    /// <summary>EPA's version sequence. Part of the natural key; required.</summary>
    public int Sequence { get; init; }

    /// <summary>Which attempt this is, counted by the loader. Used by <c>Attempt</c> mode.</summary>
    public int? AttemptNumber { get; init; }

    /// <summary>
    /// The HTTP status the request returned. Bounded to 100–599 by the procedure so that a
    /// transport-level failure with no response does not arrive as 0 and read as a real status code;
    /// leave it null in that case instead.
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>RCRAInfo's own error code, when it sent one.</summary>
    public string? ApiErrorCode { get; init; }

    /// <summary>
    /// RCRAInfo's error text. Held to 4000 characters by the column; a longer value shreds to null
    /// rather than being clipped, because losing the text is a loss where a clip would be a lie.
    /// </summary>
    public string? ApiErrorMessage { get; init; }

    /// <summary>RCRAInfo's correlation identifier for the error, when it sent one.</summary>
    public string? ApiErrorId { get; init; }

    /// <summary>When RCRAInfo says the error occurred.</summary>
    public DateTimeOffset? ApiErrorDate { get; init; }
}
