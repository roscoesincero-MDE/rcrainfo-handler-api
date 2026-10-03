namespace RCRAInfo.Data.Payloads;

/// <summary>
/// One element of <c>logs.uspRecordHandlerLoadAttemptSet</c>'s <c>@Elements</c> array (script 524) —
/// one HTTP call the loader made, with its outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>HandlerLoadStatusId</c> here, and that is deliberate.</b> The loader knows what
/// EPA told it — <see cref="HandlerId"/>, <see cref="SourceType"/>, <see cref="Sequence"/> — and which
/// attempt it is on; the procedure resolves those to the status row it enumerated for the same run. An
/// element naming a version the run never enumerated is <i>reported</i> rather than refused: it comes
/// back in <c>@RowsOrphaned</c>, and the resolvable elements in the same flush are still written,
/// because refusing would discard a whole buffer of good diagnostic rows.
/// </para>
/// <para>
/// The table is append-only. <c>(HandlerLoadStatusId, AttemptNumber)</c> is its natural key, so
/// re-sending a flush writes nothing the second time and the call is safe to retry — which it needs to
/// be, since the AR8 completion update runs after the commit and a committed call can still be reported
/// as failed.
/// </para>
/// <para>
/// <see cref="StartedDateUtc"/> is required even though the column has a
/// <c>SYSUTCDATETIME ()</c> default. Attempts are buffered and flushed in batches, so the row arrives
/// well after the call it describes; the default would stamp the <i>flush</i> and silently corrupt the
/// only measurement of how long EPA took.
/// </para>
/// <para>
/// <see cref="RequestPath"/>, <see cref="ApiErrorMessage"/> and <see cref="FailureMessage"/> are all
/// excluded from that procedure's <c>@KeyParameters</c> by name (AR8), and the procedure replaces any of
/// the three that names a credential-bearing path before it writes.
/// </para>
/// </remarks>
public sealed class HandlerLoadAttemptElement
{
    /// <summary>The handler's EPA identifier. Part of the status row's natural key; required.</summary>
    public string HandlerId { get; init; } = null!;

    /// <summary>The source-type code. Part of the status row's natural key; required.</summary>
    public string SourceType { get; init; } = null!;

    /// <summary>EPA's version sequence. Part of the status row's natural key; required.</summary>
    public int Sequence { get; init; }

    /// <summary>
    /// Which attempt this is, counted by the loader from 1. With the resolved status row this is the
    /// grain of the table, so two elements in one flush may not share it.
    /// </summary>
    public int AttemptNumber { get; init; }

    /// <summary>
    /// When the request went out. Required — see the class remarks on why the column's default is not
    /// good enough.
    /// </summary>
    public DateTimeOffset StartedDateUtc { get; init; }

    /// <summary>
    /// When the response arrived, or when the attempt was given up on. Null for an attempt still in
    /// flight when the buffer flushed.
    /// </summary>
    public DateTimeOffset? CompletedDateUtc { get; init; }

    /// <summary>
    /// How long the call took. Supplied from a <c>Stopwatch</c> when there is one, and preferred over the
    /// procedure's own subtraction of the two stamps, which is only accurate to
    /// <c>DATETIME2</c> arithmetic. Omit it and the procedure derives it.
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    /// How the attempt ended: <c>Succeeded</c>, <c>Failed</c>, <c>Throttled</c>, <c>TimedOut</c> or
    /// <c>Cancelled</c>. Required.
    /// </summary>
    /// <remarks>
    /// A string, not an enum, for the reason <see cref="LoadRunRequest.RunMode"/> gives: the permitted
    /// values live in the column's <c>CHECK</c> constraint and a C# enum would be a second list. The
    /// procedure refuses a value outside the five rather than writing one the constraint would reject
    /// with an engine error naming no element.
    /// </remarks>
    public string Outcome { get; init; } = null!;

    /// <summary>
    /// The HTTP status the call returned. Bounded to 100–599 by the procedure, so leave it null for a
    /// transport failure with no response rather than sending 0 — a 0 reads as a real status code.
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>
    /// The request path, and <b>the path only</b> — never a query string, never a header.
    /// </summary>
    /// <remarks>
    /// RCRAInfo credentials travel in the auth URL's own path segments and in the data client's
    /// <c>Authorization</c> header, and the monitoring web application can read this table. Scripts 320,
    /// 500 and 511 all state the contract; 524 is the first place that <i>enforces</i> it, replacing any
    /// value that is not a bare path with a notice and counting it into <c>@ValuesWithheld</c>. A
    /// non-zero count is a loader defect to fix, not a database problem.
    /// </remarks>
    public string? RequestPath { get; init; }

    /// <summary>How large the response body was. Used to spot a truncated or empty 200.</summary>
    /// <remarks>
    /// <see cref="int"/> and not <see cref="long"/>, matching the column. The obvious choice for a byte
    /// count is a <see cref="long"/>, and it would be wrong here in a way with no symptom: the procedure
    /// shreds this with <c>TRY_CAST (… AS INT)</c>, so a value past
    /// <see cref="int.MaxValue"/> would arrive as <see langword="null"/> rather than as an error. No
    /// RCRAInfo response comes close to 2 GB, and the type says so.
    /// </remarks>
    public int? ResponseBytes { get; init; }

    /// <summary>
    /// The <c>Retry-After</c> EPA sent, in seconds, when it throttled the call. The one field that makes
    /// a 429 actionable rather than merely visible (G21).
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>RCRAInfo's own error code, when it sent one.</summary>
    public string? ApiErrorCode { get; init; }

    /// <summary>
    /// RCRAInfo's error text. EPA's words, not ours, and never copied into a log message.
    /// </summary>
    public string? ApiErrorMessage { get; init; }

    /// <summary>
    /// RCRAInfo's correlation identifier for the error. Kept even when the message beside it is
    /// withheld, because the code and this identifier are what EPA support asks for.
    /// </summary>
    public string? ApiErrorId { get; init; }

    /// <summary>When RCRAInfo says the error occurred.</summary>
    public DateTimeOffset? ApiErrorDate { get; init; }

    /// <summary>
    /// This project's own description of the failure, for the failures EPA never described — a timeout, a
    /// cancellation, a response that would not parse.
    /// </summary>
    /// <remarks>
    /// Compose this message rather than passing <c>exception.ToString ()</c>. An exception message from
    /// anywhere in the HTTP stack can carry the request URI, and a URI carries a query string.
    /// </remarks>
    public string? FailureMessage { get; init; }
}
