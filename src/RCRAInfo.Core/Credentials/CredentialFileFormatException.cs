namespace RCRAInfo.Core.Credentials;

/// <summary>
/// The credential file exists but is not one this application can act on.
/// </summary>
/// <remarks>
/// Every message this is thrown with names the problem in terms of the file an operator is looking at
/// — the key, the line, what was expected — and <b>never</b> quotes a value, because on the path where
/// the flag says <c>Encrypted: true</c> over an unsealed value, the value is the password.
/// </remarks>
public sealed class CredentialFileFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CredentialFileFormatException()
        : base("The credential file is not in a form this application can read.")
    {
    }

    /// <summary>Creates the exception with a message naming the problem.</summary>
    /// <param name="message">What is wrong, for an operator. Must not quote a secret value.</param>
    public CredentialFileFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    /// <param name="message">What is wrong, for an operator. Must not quote a secret value.</param>
    /// <param name="innerException">The parse failure underneath.</param>
    public CredentialFileFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
