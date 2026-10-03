namespace RCRAInfo.Loader.Api;

/// <summary>
/// Thrown when a bearer token was needed and could not be obtained. Carries the classified
/// <see cref="ApiAuthResult"/> so a caller can tell "re-seed the credential" from "wait and try again"
/// without re-parsing anything.
/// </summary>
/// <remarks>
/// <b>The message is the result's <c>Diagnostic</c>, which is credential-free by construction.</b> That
/// matters more here than for a normal exception type: this one is thrown on the path where the API Key
/// is in play, its message reaches <c>logs.ExecutionLog</c>, and the monitoring web application can read
/// that table (AR8).
/// </remarks>
public sealed class RcraInfoAuthException : Exception
{
    /// <summary>Creates an exception describing a failed auth call.</summary>
    /// <param name="result">The classified failure.</param>
    public RcraInfoAuthException(ApiAuthResult result)
        : base(Describe(result))
    {
        Outcome = result?.Outcome ?? ApiAuthOutcome.Unexpected;
        StatusCode = result?.StatusCode;
        ErrorCode = result?.ErrorCode;
        IsRetryable = result?.IsRetryable ?? false;
    }

    /// <summary>Creates an exception with the given message.</summary>
    /// <param name="message">The message. Must contain no credential.</param>
    public RcraInfoAuthException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with the given message and cause.</summary>
    /// <param name="message">The message. Must contain no credential.</param>
    /// <param name="innerException">The cause.</param>
    public RcraInfoAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with no detail. Present because CA1032 requires it.</summary>
    public RcraInfoAuthException()
    {
    }

    /// <summary>What happened.</summary>
    public ApiAuthOutcome Outcome { get; } = ApiAuthOutcome.Unexpected;

    /// <summary>EPA's HTTP status, when there was one.</summary>
    public int? StatusCode { get; }

    /// <summary>EPA's <c>ApiError.code</c>, when one could be parsed.</summary>
    public string? ErrorCode { get; }

    /// <summary>Whether trying again could produce a different answer.</summary>
    public bool IsRetryable { get; }

    private static string Describe(ApiAuthResult result) =>
        result is null
            ? "The RCRAInfo auth call failed."
            : $"{result.Outcome}: {result.Diagnostic}";
}
