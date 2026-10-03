using RCRAInfo.Core.Credentials;
using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Credentials;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// Plan §4.3's validator. The rule under test is "seal only on proof": a token means valid, and every
/// other answer — including EPA being unreachable — means the bootstrapper must not encrypt.
/// </summary>
public class ApiCredentialValidatorTests
{
    private const string ApiId = "MDTESTAPIID00001";
    private const string ApiKey = "MDTESTAPIKEY-0123456789";

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ATokenMeansValid()
    {
        CredentialValidation result = await ValidateAsync(
            new ApiAuthResult(
                ApiAuthOutcome.Succeeded,
                new ApiToken("t", Now, Now.AddMinutes(20)),
                "EPA issued a bearer token.",
                200));

        Assert.True(result.IsValid);
        Assert.Empty(result.FailureMessage);
    }

    [Fact]
    public async Task A401MeansRejectedAndTheMessageSaysReseed()
    {
        CredentialValidation result = await ValidateAsync(
            new ApiAuthResult(
                ApiAuthOutcome.InvalidCredentials,
                null,
                "EPA rejected the RCRAInfo API ID and Key (401). Generate a new API ID and Key and re-seed.",
                401,
                ApiError.InvalidCredentialsCode));

        Assert.False(result.IsValid);
        Assert.Contains("EPA rejected", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApiAuthOutcome.ServiceFailure)]
    [InlineData(ApiAuthOutcome.Unreachable)]
    [InlineData(ApiAuthOutcome.Unexpected)]
    public async Task AnUnprovedCredentialIsRejectedAndSaidToBeUnproved(ApiAuthOutcome outcome)
    {
        CredentialValidation result = await ValidateAsync(
            new ApiAuthResult(outcome, null, "EPA could not be reached.", null));

        // Rejected, so nothing is sealed -- the same rule SqlCredentialValidator follows for a SQL Server it
        // cannot reach. A sealed credential cannot be read back and corrected, so sealing an unverified one
        // trades a re-paste today for a re-seed from the password manager later.
        Assert.False(result.IsValid);

        // And the message must not read as "your key is wrong", which would send an operator to regenerate a
        // key that was never tried.
        Assert.Contains("could NOT BE VERIFIED", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("unchanged", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A403IsRejectedBecauseTheSpecDoesNotDocumentOneHere()
    {
        CredentialValidation result = await ValidateAsync(
            new ApiAuthResult(ApiAuthOutcome.AccessDenied, null, "Scope problem.", 403));

        Assert.False(result.IsValid);
        Assert.Contains("has not been sealed", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingApiPairIsRejectedWithoutCallingEpa()
    {
        ScriptedAuthClient client = new(_ => throw new InvalidOperationException("must not be called"));
        using ApiCredentialValidator validator = new(client);

        CredentialValidation result = await validator.ValidateAsync(
            new ApplicationCredentials("sql-password", null, null));

        Assert.False(result.IsValid);
        Assert.Contains("secrets.Template.json", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task TheDelegateIsTheSameCheck()
    {
        ScriptedAuthClient client = new(_ => new ApiAuthResult(
            ApiAuthOutcome.InvalidCredentials, null, "EPA rejected it.", 401));

        using ApiCredentialValidator validator = new(client);

        CredentialValidation result = await validator.Delegate(
            new ApplicationCredentials("sql-password", ApiId, ApiKey), default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CreateRefusesAnUnconfiguredBaseAddress()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ApiCredentialValidator.Create(new RcraInfoApiOptions()));

        Assert.Contains("rcranodepreprod.epa.gov", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBuildsAValidatorWithItsOwnClient()
    {
        // It runs before there is a host, so it cannot come from the container -- and building the client here
        // is also what guarantees no logger is attached to the one call that carries the credential in its URI.
        using ApiCredentialValidator validator = ApiCredentialValidator.Create(
            new RcraInfoApiOptions
            {
                BaseAddress = "https://rcranodepreprod.epa.gov/rcra-api/rest",
            });

        Assert.NotNull(validator.Delegate);
    }

    [Fact]
    public async Task TheOrderedCompositionStopsAtTheShapeCheckSoNothingIsSentToEpa()
    {
        // The composition Program.cs builds, minus the SQL half. A key pasted with its surrounding quotation
        // marks must be reported as a paste error and must never reach EPA, whose answer would be "401" and
        // whose remedy would be to regenerate a key that was never wrong.
        ScriptedAuthClient client = new(_ => throw new InvalidOperationException("must not be called"));
        using ApiCredentialValidator validator = new(client);

        CredentialValidator all = CredentialValidators.All(
            ApiCredentialShapeValidator.Delegate,
            validator.Delegate);

        CredentialValidation result = await all(
            new ApplicationCredentials("sql-password", ApiId, $"\"{ApiKey}\""),
            default);

        Assert.False(result.IsValid);
        Assert.Contains("position 0", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    private static async Task<CredentialValidation> ValidateAsync(ApiAuthResult answer)
    {
        using ApiCredentialValidator validator = new(new ScriptedAuthClient(_ => answer));

        return await validator.ValidateAsync(new ApplicationCredentials("sql-password", ApiId, ApiKey));
    }
}
