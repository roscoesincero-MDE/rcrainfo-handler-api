using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>
/// What the credential file will and will not accept, and what survives being sealed.
/// </summary>
/// <remarks>
/// The file is typed by a person under time pressure, once per environment, and there is no second copy
/// of it anywhere by design. So the parser is tolerant of formatting and intolerant of ambiguity, and
/// these tests are mostly about which is which.
/// </remarks>
public sealed class CredentialFileTests
{
    private static string Wrap(string body) => $"{{{Environment.NewLine}{body}{Environment.NewLine}}}";

    [Fact]
    public void ThePropertiesMayBeSpelledInAnyCase()
    {
        // An operator types "encrypted". Refusing that would be pedantry; the ambiguity below is not.
        CredentialFile file = CredentialFile.Parse(
            Wrap($"""  "encrypted": false, "sqlpassword": "{Secrets.SqlPassword}" """));

        Assert.False(file.Encrypted);
        Assert.Equal(Secrets.SqlPassword, file.ReadPlaintext().SqlPassword);
    }

    [Fact]
    public void APropertySpelledTwoWaysIsRefusedRatherThanPicked()
    {
        CredentialFileFormatException error = Assert.Throws<CredentialFileFormatException>(
            () => CredentialFile.Parse(
                Wrap($"""  "Encrypted": true, "encrypted": false, "SqlPassword": "{Secrets.SqlPassword}" """)));

        // Naming both spellings is the whole value of the message: the file looks correct at a glance,
        // and which one is stale is not something the reader can guess either.
        Assert.Contains("'Encrypted'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'encrypted'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingCommasAndCommentsAreAccepted()
    {
        CredentialFile file = CredentialFile.Parse(
            $$"""
            {
              // 1Password entry: RCRAInfoLoader
              "Encrypted": false,
              "SqlPassword": "{{Secrets.SqlPassword}}",
            }
            """);

        Assert.Equal(Secrets.SqlPassword, file.ReadPlaintext().SqlPassword);
    }

    [Fact]
    public void AMissingEncryptedFlagIsRefusedRatherThanDefaulted()
    {
        // Defaulting it to false would hand ciphertext to a validator on a file that had been sealed and
        // then edited -- the login fails, and the log blames the password.
        CredentialFileFormatException error = Assert.Throws<CredentialFileFormatException>(
            () => CredentialFile.Parse(Wrap($"""  "SqlPassword": "{Secrets.SqlPassword}" """)));

        Assert.Contains("'Encrypted'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"TRUE\"", true)]
    public void TheFlagMayBeQuotedBecauseThatIsWhatPeopleType(string literal, bool expected)
    {
        CredentialFile file = CredentialFile.Parse(
            Wrap($"""  "Encrypted": {literal}, "SqlPassword": "{Secrets.SqlPassword}" """));

        Assert.Equal(expected, file.Encrypted);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("null")]
    public void AFlagThatIsNotABooleanIsRefused(string literal)
    {
        // "yes" is the interesting one. Read as false it would re-seal ciphertext; read as true it would
        // refuse to start on a file that is genuinely plaintext. There is no safe guess, so there is no
        // guess.
        Assert.Throws<CredentialFileFormatException>(
            () => CredentialFile.Parse(
                Wrap($"""  "Encrypted": {literal}, "SqlPassword": "{Secrets.SqlPassword}" """)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptySqlPasswordIsAnIncompletelySeededFile(string value)
    {
        CredentialFileFormatException error = Assert.Throws<CredentialFileFormatException>(
            () => CredentialFile.Parse(Wrap($"""  "Encrypted": false, "SqlPassword": "{value}" """)));

        Assert.Contains("SqlPassword", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretWrittenAsANumberIsAQuotingMistakeAndIsRefused()
    {
        Assert.Throws<CredentialFileFormatException>(
            () => CredentialFile.Parse(Wrap("""  "Encrypted": false, "SqlPassword": 1234 """)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HalfOfTheApiCredentialIsRefused(bool withId, bool withKey)
    {
        string body = $"""  "Encrypted": false, "SqlPassword": "{Secrets.SqlPassword}" """
                      + (withId ? $""", "ApiId": "{Secrets.ApiId}" """ : string.Empty)
                      + (withKey ? $""", "ApiKey": "{Secrets.ApiKey}" """ : string.Empty);

        CredentialFileFormatException error =
            Assert.Throws<CredentialFileFormatException>(() => CredentialFile.Parse(Wrap(body)));

        Assert.Contains("one credential", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherHalfOfTheApiCredentialIsTheMonitoringApplicationAndIsFine()
    {
        CredentialFile file = CredentialFile.Parse(Secrets.MonitorFile());

        Assert.False(file.HasApiCredentials);
        Assert.Null(file.ReadPlaintext().ApiId);
        Assert.Null(file.ReadPlaintext().ApiKey);
    }

    [Fact]
    public void AnEmptyFileNamesTheInterruptedSealRatherThanTheJsonGrammar()
    {
        // The state a crash between the truncate and the flush leaves behind. It is the one malformed
        // file whose cause is this application rather than an editor, so it says so.
        CredentialFileFormatException error =
            Assert.Throws<CredentialFileFormatException>(() => CredentialFile.Parse("   "));

        Assert.Contains("interrupted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsingTheWrongAccessorForTheFilesStateIsAProgrammingErrorAndSaysSo()
    {
        CredentialFile plaintext = CredentialFile.Parse(Secrets.MonitorFile());
        CredentialFile encrypted = CredentialFile.Parse(Secrets.MonitorFile(encrypted: true));

        Assert.Throws<InvalidOperationException>(() => plaintext.Unseal(new ThrowingProtector()));
        Assert.Throws<InvalidOperationException>(() => encrypted.ReadPlaintext());
        Assert.Throws<InvalidOperationException>(() => encrypted.Seal(new ThrowingProtector()));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SealingWritesBackTheKeysAsTheFileSpelledThem()
    {
        // Sealing has to write to the spelling that is there, not to the canonical one. Adding
        // "SqlPassword" beside an existing "sqlpassword" would leave the plaintext in the file next to
        // its own ciphertext -- and the parser would then refuse the file it had just written.
        string resealed = CredentialFile
            .Parse(Wrap($"""  "encrypted": false, "sqlpassword": "{Secrets.SqlPassword}" """))
            .Seal(new SpyProtector(ApplicationIdentity.Loader));

        JsonObject written = JsonNode.Parse(resealed)!.AsObject();

        Assert.Equal(2, written.Count);
        Assert.True(written["encrypted"]!.GetValue<bool>());
        Assert.NotEqual(Secrets.SqlPassword, written["sqlpassword"]!.GetValue<string>());

        // And the result is a file this same parser accepts, which is not automatic: it is the assertion
        // that the seal did not produce something only the writer understands.
        Assert.True(CredentialFile.Parse(resealed).Encrypted);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ASealedValueDoesNotAppearInTheFileTextLiterally()
    {
        // A gotcha with consequences outside this class. System.Text.Json's default encoder escapes '+'
        // — and Base64, which is what every sealed value is, produces '+' about once every 64
        // characters. So the text on disk is not the string the parser hands back, and anything that
        // searches the file for a value it already holds will find nothing.
        //
        // This cost one debugging round already: the bootstrapper's tamper test replaced a substring of
        // the file text with the decoded value as its needle, matched nothing, left the file untouched,
        // and reported that a corrupted blob had decrypted cleanly. Asserted here on a value chosen to
        // contain a '+' rather than on a DPAPI blob, so the test is deterministic rather than true 99%
        // of the time.
        string resealed = CredentialFile
            .Parse(
                $$"""
                {
                  "Encrypted": false,
                  "SqlPassword": "{{Secrets.SqlPassword}}",
                  "Note": "a+b"
                }
                """)
            .Seal(new SpyProtector(ApplicationIdentity.Loader));

        Assert.DoesNotContain("a+b", resealed, StringComparison.Ordinal);
        Assert.Equal("a+b", JsonNode.Parse(resealed)!.AsObject()["Note"]!.GetValue<string>());
    }

    /// <summary>A protector that must never be reached, for the accessors that should refuse first.</summary>
    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string plaintext) =>
            throw new InvalidOperationException("Protect should not have been reached.");

        public string Unprotect(string ciphertext) =>
            throw new InvalidOperationException("Unprotect should not have been reached.");
    }
}
