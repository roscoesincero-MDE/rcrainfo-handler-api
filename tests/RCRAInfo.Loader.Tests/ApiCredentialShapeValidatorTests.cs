using System.Globalization;

using RCRAInfo.Core.Credentials;
using RCRAInfo.Loader.Credentials;

namespace RCRAInfo.Loader.Tests;

/// <summary>
/// The paste mistakes <see cref="ApiCredentialShapeValidator"/> catches, the ones it deliberately does
/// not, and the promise that no message it produces reproduces the credential it was given.
/// </summary>
/// <remarks>
/// <para>
/// What is NOT tested here, because it is not implemented and must not be: that a well-formed key is a
/// working key. Plan §4.3 validates the pair by calling <c>GET /api/v1/auth/{apiId}/{apiKey}</c>, G1 is
/// still open, and this validator says so in its own name. A test asserting that a made-up key is
/// rejected would pass only if this class had invented a length or character-class rule that EPA never
/// published — so the absence of that test is the same decision as the absence of that rule.
/// </para>
/// <para>
/// The marker values below are deliberately unmistakable and deliberately not shaped like anything: the
/// assertions that a message does NOT contain them are the load-bearing half of most of these tests,
/// and a realistic-looking value would make a leak harder to see when one of them fails.
/// </para>
/// </remarks>
public sealed class ApiCredentialShapeValidatorTests
{
    private const string SqlPassword = "sql-password-QQ7MARKERf3";
    private const string ApiId = "api-id-ZZ4MARKERb1";
    private const string ApiKey = "api-key-WW9MARKERd7";

    private static async Task<CredentialValidation> Check(string? apiId, string? apiKey) =>
        await ApiCredentialShapeValidator.ValidateAsync(new ApplicationCredentials(SqlPassword, apiId, apiKey));

    [Fact]
    public async Task AWellFormedPairIsAccepted()
    {
        CredentialValidation result = await Check(ApiId, ApiKey);

        Assert.True(result.IsValid);
        Assert.Empty(result.FailureMessage);
    }

    [Fact]
    public async Task AnAbsentPairNamesTheWrongTemplateRatherThanABadValue()
    {
        // CredentialFile already refuses HALF a pair, so absent here means both are missing, which means
        // the file was seeded from the monitor's secrets.Template.json. That template omits the API
        // credential on purpose, so "invalid API Key" would be a true statement pointing at the wrong
        // file; the remedy is a different template, not a different value.
        CredentialValidation result = await Check(null, null);

        Assert.False(result.IsValid);
        Assert.Contains("secrets.Template.json", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("loader's", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <param name="field">The field expected to be named in the refusal.</param>
    /// <param name="apiId">The API ID to inspect.</param>
    /// <param name="apiKey">The API Key to inspect.</param>
    /// <param name="position">The offset the refusal should report.</param>
    [Theory]
    [InlineData("ApiId", "\"" + ApiId, ApiKey, 0)]
    [InlineData("ApiId", ApiId + "\"", ApiKey, 18)]
    [InlineData("ApiId", ApiId + "\n", ApiKey, 18)]
    [InlineData("ApiId", ApiId + "\r\n", ApiKey, 18)]
    [InlineData("ApiKey", ApiId, ApiKey + " ", 19)]
    [InlineData("ApiKey", ApiId, "'" + ApiKey + "'", 0)]
    [InlineData("ApiKey", ApiId, ApiKey + "\t", 19)]
    public async Task APasteThatTookMoreThanTheValueIsRefusedByFieldAndPositionOnly(
        string field, string apiId, string apiKey, int position)
    {
        CredentialValidation result = await Check(apiId, apiKey);

        Assert.False(result.IsValid);
        Assert.Contains($"'{field}'", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(
            "position " + position.ToString(CultureInfo.InvariantCulture),
            result.FailureMessage,
            StringComparison.Ordinal);

        // The position is the whole point of the message: it is what lets an operator find the stray
        // character without the message quoting the secret back at them.
        Assert.DoesNotContain(ApiId, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(SqlPassword, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIdIsInspectedBeforeTheKeySoOneMessageNamesOneField()
    {
        // Both halves are wrong here. Reporting both would be two sentences about two secrets for a
        // single re-seed, and the operator re-copies both anyway; reporting the FIRST keeps the message
        // to one field and one position.
        CredentialValidation result = await Check("\"" + ApiId, ApiKey + "\n");

        Assert.False(result.IsValid);
        Assert.Contains("'ApiId'", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("'ApiKey'", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIdAndKeyHoldingTheSameValueAreRefused()
    {
        // The mistake this catches is one clipboard paste landing in both fields. Left alone it is sealed
        // and then produces an EPA auth failure whose message says nothing whatsoever about copy and
        // paste -- which is a diagnosis nobody reaches from the symptom.
        CredentialValidation result = await Check(ApiKey, ApiKey);

        Assert.False(result.IsValid);
        Assert.Contains("same value", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdAndKeyDifferingOnlyByCaseAreNotTreatedAsTheSameValue()
    {
        // Ordinal comparison, deliberately. An API credential is opaque bytes to this project, so two
        // values differing by case are two different values and refusing the pair would reject a
        // legitimate one -- the failure mode this class's remarks rule out.
        Assert.True((await Check(ApiKey.ToUpperInvariant(), ApiKey)).IsValid);
    }

    [Fact]
    public async Task NoLengthOrCharacterClassRuleIsInvented()
    {
        // The recorded knowledge is that a developer logs in to preProd and copies two values (G1). A
        // one-character key and a key full of punctuation are both accepted, because nothing published
        // says they cannot be, and rejecting them at 2am would assert something this project never knew.
        Assert.True((await Check("a", "b")).IsValid);
        Assert.True((await Check("{[<:;>]}|\\/?&=%#@!~`^*+-_.,", "0123456789")).IsValid);
    }

    // The test that asserted the stand-in notice is gone with the notice itself. It existed to keep a run
    // disclosing that its API credential had never been tried; D1 built ApiCredentialValidator, the console
    // application now proves the credential against EPA, and a constant still saying it was "checked for
    // shape only" would be a false disclosure rather than a stale one.

    [Fact]
    public async Task TheDelegateIsTheSameCheck()
    {
        CredentialValidation result =
            await ApiCredentialShapeValidator.Delegate(
                new ApplicationCredentials(SqlPassword, ApiKey, ApiKey), default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task NullCredentialsAreARefusalRatherThanANullReference() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ApiCredentialShapeValidator.ValidateAsync(null!));

    [Fact]
    public async Task CancellationIsObserved() =>
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ApiCredentialShapeValidator.ValidateAsync(
                new ApplicationCredentials(SqlPassword, ApiId, ApiKey),
                new CancellationToken(canceled: true)));
}
