namespace RCRAInfo.Core.Credentials;

/// <summary>Combines validators. The one thing worth having a helper for.</summary>
public static class CredentialValidators
{
    /// <summary>
    /// Runs validators in order and returns the first rejection, or valid if every one accepts.
    /// </summary>
    /// <param name="validators">
    /// The validators, in the order they should run. Order is a decision, not a detail — the loader's
    /// SQL login goes first so that an unreachable SQL Server is not reported as a problem with an API
    /// Key nobody tried.
    /// </param>
    /// <returns>A validator that requires all of them.</returns>
    /// <remarks>
    /// <para>
    /// Short-circuiting, and not only for speed. The credential file holds several secrets and the
    /// bootstrapper seals all of them together or none, so the first rejection already decides the
    /// outcome; running the rest would add a second failure message about a credential that is fine and
    /// leave an operator guessing which one to fix.
    /// </para>
    /// <para>
    /// An empty list is refused rather than treated as valid. "Validate against nothing" is what a
    /// misconfigured host produces, and its effect would be to seal every secret in the file without
    /// checking any of them — the exact outcome AR4's validation step exists to prevent.
    /// </para>
    /// </remarks>
    public static CredentialValidator All(params CredentialValidator[] validators)
    {
        ArgumentNullException.ThrowIfNull(validators);

        if (validators.Length == 0)
        {
            throw new ArgumentException(
                "At least one validator is required. A bootstrap with no validator seals whatever is in "
                + "the file without checking it, which is the failure AR4's validation login prevents.",
                nameof(validators));
        }

        if (Array.IndexOf(validators, null) >= 0)
        {
            throw new ArgumentException("A validator must not be null.", nameof(validators));
        }

        CredentialValidator[] ordered = [.. validators];

        return async (credentials, cancellationToken) =>
        {
            foreach (CredentialValidator validator in ordered)
            {
                CredentialValidation result =
                    await validator(credentials, cancellationToken).ConfigureAwait(false);

                if (!result.IsValid)
                {
                    return result;
                }
            }

            return CredentialValidation.Valid;
        };
    }
}
