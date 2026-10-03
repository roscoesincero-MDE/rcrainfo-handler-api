using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>
/// The one member of <see cref="ApplicationCredentials"/> that can leak, and the fact that it does not.
/// </summary>
/// <remarks>
/// This looks like a test of a <c>ToString</c> override, which is normally not worth writing. It is
/// really a test of a deletion: a positional record generates a <c>ToString</c> that prints every member,
/// so removing the override — or reordering the record into a shape where someone re-adds a member and
/// forgets it — puts the SQL password and the API Key into <c>logs.ExecutionLog</c>, which the monitoring
/// web application can read (AR8). Nobody calls <c>ToString</c> on purpose; every path here is one that
/// calls it implicitly.
/// </remarks>
public sealed class ApplicationCredentialsTests
{
    private static readonly ApplicationCredentials Loader =
        new(Secrets.SqlPassword, Secrets.ApiId, Secrets.ApiKey);

    private static readonly ApplicationCredentials Monitor = new(Secrets.SqlPassword, null, null);

    /// <summary>
    /// Every way a credential object turns into text, labelled — the label only so that the six cases get
    /// six test names, since four of them produce byte-identical output and xUnit deduplicates theory
    /// cases by their arguments.
    /// </summary>
    /// <remarks>
    /// That they produce identical output is the finding, not a redundancy to trim: it means there is one
    /// funnel, <c>ToString</c>, and no second rendering path to audit separately.
    /// </remarks>
    public static TheoryData<string, string> EveryWayAStringIsProduced =>
        new()
        {
            { "ToString", Loader.ToString() },
            { "interpolation", $"{Loader}" },
            {
                "string.Format",
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", Loader)
            },
            // The shape of an ILogger call, which is where this actually happens: the structured
            // formatter resolves a non-primitive argument through ToString.
            { "concatenation into a message", string.Concat("credentials: ", Loader) },
            { "ToString, no API credentials", Monitor.ToString() },
            { "interpolation, no API credentials", $"{Monitor}" },
        };

    [Theory]
    [MemberData(nameof(EveryWayAStringIsProduced))]
    public void NoRenderingOfCredentialsContainsACredential(string path, string rendered)
    {
        Assert.NotEmpty(path);

        foreach (string secret in Secrets.All)
        {
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        }

        // A positive control. Every assertion above would also pass against an empty string, and an
        // empty string is what a future ToString returning string.Empty would produce -- which would
        // pass this test while making every log line useless.
        Assert.Contains(nameof(ApplicationCredentials), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionDistinguishesAnAbsentApiCredentialFromARedactedOne()
    {
        // The distinction is the entire diagnostic value of the description. "ApiKey = <absent>" says
        // the monitoring application is running as designed; "<redacted>" on a run that then fails
        // authentication says the credential is present and wrong. Collapsing both to "<redacted>"
        // would be safe and useless.
        Assert.Contains("ApiKey = <redacted>", Loader.ToString(), StringComparison.Ordinal);
        Assert.Contains("ApiId = <redacted>", Loader.ToString(), StringComparison.Ordinal);
        Assert.Contains("ApiKey = <absent>", Monitor.ToString(), StringComparison.Ordinal);
        Assert.Contains("ApiId = <absent>", Monitor.ToString(), StringComparison.Ordinal);

        // The SQL password is never absent -- an absent one is a malformed file, refused before a
        // credential object exists -- so it has one form.
        Assert.Contains("SqlPassword = <redacted>", Monitor.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheValuesAreStillReachableByACallerThatAsksForThem()
    {
        // Redaction is about accidental rendering, not about hiding the values from the code that has to
        // use them. If this ever stops compiling, the redaction has gone too far and the loader cannot
        // connect.
        (string password, string? apiId, string? apiKey) = Loader;

        Assert.Equal(Secrets.SqlPassword, password);
        Assert.Equal(Secrets.ApiId, apiId);
        Assert.Equal(Secrets.ApiKey, apiKey);
        Assert.Equal(Secrets.SqlPassword, Loader.SqlPassword);
    }

    [Fact]
    public void CredentialsAreComparedByValue()
    {
        Assert.Equal(Loader, new ApplicationCredentials(Secrets.SqlPassword, Secrets.ApiId, Secrets.ApiKey));
        Assert.NotEqual(Loader, Monitor);
    }

    [Fact]
    public void AValidationResultCarriesNoMessageWhenItSucceeded()
    {
        Assert.True(CredentialValidation.Valid.IsValid);
        Assert.Empty(CredentialValidation.Valid.FailureMessage);
    }

    [Fact]
    public void AValidationFailureCarriesTheReasonItWasGiven()
    {
        // The sanctioned channel for "log the SQL error" in the G5 matrix. The caller supplies the text
        // and the caller is responsible for it holding no credential -- which is why the SQL validator
        // passes the server's own message and the HTTP one passes only an exception type name, EPA
        // having put the API Key in the request URI.
        CredentialValidation validation = CredentialValidation.Invalid("Login failed for user 'RCRAInfoLoader'.");

        Assert.False(validation.IsValid);
        Assert.Equal("Login failed for user 'RCRAInfoLoader'.", validation.FailureMessage);
    }
}
