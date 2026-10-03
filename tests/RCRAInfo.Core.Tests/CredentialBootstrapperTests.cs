using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>
/// Analysis §6.1's G5 failure matrix, one test per row, plus the three things the plan's acceptance
/// criteria ask for beyond it: the concurrent first run, the file ACL, and the credential never reaching
/// a message.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests use real DPAPI.</b> Every row of the matrix that looks like it needs a substitute is
/// reachable without one. "Decryption fails" is arranged three different ways — a blob tampered with by
/// one character, a blob sealed with the other application's entropy, and the flag set to <c>true</c>
/// over a value that was never sealed at all — and all three are the genuine
/// <see cref="System.Security.Cryptography.CryptographicException"/> that a rebuilt machine produces. The
/// substitute would only have proved that the <c>catch</c> block compiles.
/// </para>
/// <para>
/// <b>The one outcome with no arrangement here is
/// <see cref="CredentialBootstrapOutcome.SealFailed"/></b>, and it is worth saying why rather than
/// leaving a gap to be discovered. It means the credentials were accepted but the write failed, and by
/// that point the file is already open for read and write through a handle this code owns — so making
/// the write fail needs a device-level fault: a full volume, a quota, a failing disk. The
/// permission-shaped cause of the same fault, which is the one an operator will actually hit, is caught
/// at open time and is covered: see
/// <see cref="AFileThisIdentityCannotWriteIsReportedBeforeAnythingIsValidated"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CredentialBootstrapperTests
{
    private static readonly CredentialBootstrapOptions Impatient = new()
    {
        LockTimeout = TimeSpan.FromMilliseconds(200),
        LockPollInterval = TimeSpan.FromMilliseconds(10),
    };

    private static readonly CredentialBootstrapOptions Patient = new()
    {
        LockTimeout = TimeSpan.FromSeconds(30),
        LockPollInterval = TimeSpan.FromMilliseconds(10),
    };

    // ----------------------------------------------------------------------------------------------
    // G5 row 1: plaintext, validation succeeds.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlaintextThatValidatesIsSealedAndTheNextRunOpensWhatWasSealed()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.LoaderFile());

        SpyProtector protector = new(ApplicationIdentity.Loader);
        RecordingValidator first = RecordingValidator.Accepts();

        CredentialBootstrapResult sealing =
            await new CredentialBootstrapper(protector).BootstrapAsync(sandbox.File, first.Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Sealed, sealing.Outcome);
        Assert.True(sealing.Succeeded);

        // Validation saw the plaintext, and it saw it BEFORE anything was sealed -- that ordering is the
        // whole of "never encrypt a password that failed validation", so it is asserted rather than
        // assumed.
        Assert.Equal(1, first.Calls);
        Assert.Equal(Secrets.SqlPassword, first.Seen!.SqlPassword);
        Assert.Equal(Secrets.ApiId, first.Seen.ApiId);
        Assert.Equal(Secrets.ApiKey, first.Seen.ApiKey);
        Assert.Equal(3, protector.Protects);

        string onDisk = sandbox.Read();

        foreach (string secret in Secrets.All)
        {
            Assert.DoesNotContain(secret, onDisk, StringComparison.Ordinal);
        }

        CredentialFile resealed = CredentialFile.Parse(onDisk);
        Assert.True(resealed.Encrypted, "the rewritten file does not record Encrypted: true.");

        // The second run is the steady state, and it is the one that proves the first run wrote
        // something openable rather than merely something different.
        SpyProtector second = new(ApplicationIdentity.Loader);
        RecordingValidator again = RecordingValidator.Accepts();

        CredentialBootstrapResult ready =
            await new CredentialBootstrapper(second).BootstrapAsync(sandbox.File, again.Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Ready, ready.Outcome);
        Assert.Equal(0, second.Protects);
        Assert.Equal(3, second.Unprotects);
        Assert.Equal(Secrets.SqlPassword, ready.Require().SqlPassword);
        Assert.Equal(Secrets.ApiId, ready.Require().ApiId);
        Assert.Equal(Secrets.ApiKey, ready.Require().ApiKey);
    }

    [Fact]
    public async Task SealingPreservesEverythingElseInTheFile()
    {
        using CredentialSandbox sandbox = new();

        // A comment and a trailing comma, because the file is hand-seeded; an unknown property, because
        // there is no backup of this file and deleting an operator's own note would be silent.
        sandbox.Write(
            $$"""
            {
              // seeded 2026-09-06 from 1Password entry "RCRAInfoLoader"
              "Encrypted": false,
              "SqlPassword": "{{Secrets.SqlPassword}}",
              "SeededBy": "rsincero",
            }
            """);

        CredentialBootstrapResult result = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Sealed, result.Outcome);
        Assert.Contains("\"SeededBy\": \"rsincero\"", sandbox.Read(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------
    // G5 row 2: plaintext, validation fails. The row with the "leave it alone" requirement.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlaintextThatFailsValidationIsLeftByteForByteAsItWas()
    {
        using CredentialSandbox sandbox = new();
        string seeded = Secrets.LoaderFile();
        sandbox.Write(seeded);

        SpyProtector protector = new(ApplicationIdentity.Loader);

        CredentialBootstrapResult result = await new CredentialBootstrapper(protector).BootstrapAsync(
            sandbox.File,
            RecordingValidator.Rejects("Login failed for user 'RCRAInfoLoader'.").Delegate);

        Assert.Equal(CredentialBootstrapOutcome.ValidationFailed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Null(result.Credentials);

        // Not "the file is still unsealed" -- the file is still the SAME FILE. An operator typo has to
        // remain correctable by editing it, and a rewrite that reformatted it while leaving it unsealed
        // would satisfy the weaker assertion and still have touched a file with no backup.
        Assert.Equal(seeded, sandbox.Read());
        Assert.Equal(0, protector.Protects);

        // The rejection reason reaches the operator; the password does not.
        Assert.Contains("Login failed for user 'RCRAInfoLoader'.", result.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------
    // G5 row 3: Encrypted: true, decryption fails. Three arrangements, all of them real DPAPI.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFlagClaimingEncryptionOverAValueThatWasNeverSealedIsRefusedAndNothingIsWritten()
    {
        using CredentialSandbox sandbox = new();
        string seeded = Secrets.LoaderFile(encrypted: true);
        sandbox.Write(seeded);

        SpyProtector protector = new(ApplicationIdentity.Loader);
        RecordingValidator validator = RecordingValidator.Accepts();

        CredentialBootstrapResult result = await new CredentialBootstrapper(protector)
            .BootstrapAsync(sandbox.File, validator.Delegate);

        Assert.Equal(CredentialBootstrapOutcome.DecryptionFailed, result.Outcome);
        Assert.Equal(seeded, sandbox.Read());
        Assert.Equal(0, protector.Protects);

        // Nothing is validated either. Handing an unopenable value to a validator would attempt a login
        // with a string that is not the password, and the log would then blame the credential.
        Assert.Equal(0, validator.Calls);

        // The message has one job beyond naming the fault: telling an operator what to do next.
        Assert.Contains("re-seed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Encrypted to false", result.Message, StringComparison.Ordinal);

        foreach (string secret in Secrets.All)
        {
            Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CiphertextAlteredByOneCharacterIsRefused()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.MonitorFile());

        await new CredentialBootstrapper(new SpyProtector(ApplicationIdentity.Monitor))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        // One character of the sealed value. DPAPI authenticates its own blobs, so this is the same
        // failure a truncating editor or a corrupted copy produces -- not a decrypt into garbage. Where
        // the character is altered is measured rather than picked; see Tamper.OneCharacterOf.
        //
        // Edited through JsonNode rather than by a substring replacement on the file text, because the
        // sealed value does not appear in the file text literally: System.Text.Json's default encoder
        // escapes Base64's '+' as a six-character unicode escape, and a 246-byte blob almost always
        // contains at least one '+' (see CredentialFileTests). A replacement
        // keyed on the decoded value therefore matched nothing and left the file intact -- which the
        // assertion below caught as a successful decrypt.
        JsonObject stored = JsonNode.Parse(sandbox.Read())!.AsObject();

        Assert.True(
            CredentialFile.Parse(sandbox.Read()).Encrypted,
            "The file should have been sealed before it is tampered with.");

        stored["SqlPassword"] = Tamper.OneCharacterOf(stored["SqlPassword"]!.GetValue<string>());
        sandbox.Write(stored.ToJsonString());

        string tampered = sandbox.Read();

        CredentialBootstrapResult result = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Monitor))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.DecryptionFailed, result.Outcome);
        Assert.Equal(tampered, sandbox.Read());
    }

    [Fact]
    public async Task OneApplicationCannotOpenTheOthersSealedFile()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.MonitorFile());

        CredentialBootstrapResult sealing = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Sealed, sealing.Outcome);

        // The claim under test is the one Analysis §6.1 makes for optionalEntropy, and it is a modest
        // one: it is not a boundary, but it does stop one application decrypting the other's blob. The
        // failure it prevents is an operator copying the loader's secrets.json into the monitor's
        // directory, where without entropy the monitor would connect to SQL Server as the loader.
        CredentialBootstrapResult crossed = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Monitor))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.DecryptionFailed, crossed.Outcome);
    }

    // ----------------------------------------------------------------------------------------------
    // G5 row 4: Encrypted: true, decrypts, validation fails.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SealedCredentialsThatAreRejectedReportRotationAndChangeNothing()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.LoaderFile());

        await new CredentialBootstrapper(new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        string sealedText = sandbox.Read();
        SpyProtector protector = new(ApplicationIdentity.Loader);

        CredentialBootstrapResult result = await new CredentialBootstrapper(protector).BootstrapAsync(
            sandbox.File,
            RecordingValidator.Rejects("Login failed for user 'RCRAInfoLoader'.").Delegate);

        Assert.Equal(CredentialBootstrapOutcome.ValidationFailed, result.Outcome);
        Assert.Null(result.Credentials);
        Assert.Equal(sealedText, sandbox.Read());
        Assert.Equal(0, protector.Protects);
        Assert.Contains("rotated", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------------------------
    // G5 row 5: the concurrent first run. G18 makes this the normal case, not an edge case.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoSimultaneousFirstRunsSealOnceAndBothEndUpWithTheCredentials()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.MonitorFile());

        // One protector shared by both runs, so the count of sealings is the count across both.
        SpyProtector protector = new(ApplicationIdentity.Loader);

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingValidator winner = RecordingValidator.AcceptsAfter(gate);
        RecordingValidator loser = RecordingValidator.Accepts();

        Task<CredentialBootstrapResult> first = new CredentialBootstrapper(protector, Patient)
            .BootstrapAsync(sandbox.File, winner.Delegate);

        // Wait until the first run is inside its validator. At that point it holds the file exclusively
        // and cannot release it until the gate opens, so which run wins is settled rather than raced.
        while (winner.Calls == 0)
        {
            await Task.Delay(5);
        }

        Task<CredentialBootstrapResult> second = new CredentialBootstrapper(protector, Patient)
            .BootstrapAsync(sandbox.File, loser.Delegate);

        // Long enough for the second run to have attempted the open and been refused many times over at
        // a 10ms poll. Its refusals are the point: LockWaits below is what distinguishes a lock that
        // worked from two runs that happened not to overlap.
        await Task.Delay(200);
        gate.SetResult();

        CredentialBootstrapResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(CredentialBootstrapOutcome.Sealed, results[0].Outcome);
        Assert.Equal(CredentialBootstrapOutcome.Ready, results[1].Outcome);

        Assert.True(
            results[1].LockWaits > 0,
            "the second run never found the file locked, so this test did not exercise the lock at "
            + "all. Both runs would have passed against a bootstrapper that took no lock.");

        // One encryption, not two. Two would mean the second run re-sealed already-sealed ciphertext,
        // which is the corrupted file the plan's acceptance criterion names.
        Assert.Equal(1, protector.Protects);

        Assert.Equal(Secrets.SqlPassword, results[0].Require().SqlPassword);
        Assert.Equal(Secrets.SqlPassword, results[1].Require().SqlPassword);
        Assert.True(CredentialFile.Parse(sandbox.Read()).Encrypted);
    }

    [Fact]
    public async Task AFileHeldByAnotherProcessIsWaitedForRatherThanRefused()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.MonitorFile());

        FileStream held = sandbox.HoldExclusively();

        Task<CredentialBootstrapResult> run = new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader), Patient)
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        await Task.Delay(150);
        await held.DisposeAsync();

        CredentialBootstrapResult result = await run;

        Assert.Equal(CredentialBootstrapOutcome.Sealed, result.Outcome);
        Assert.True(result.LockWaits > 0, "the run did not have to wait, so nothing was contended.");
    }

    [Fact]
    public async Task AFileHeldPastTheTimeoutIsReportedAndNothingIsSealed()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.MonitorFile());

        using FileStream held = sandbox.HoldExclusively();

        SpyProtector protector = new(ApplicationIdentity.Loader);
        RecordingValidator validator = RecordingValidator.Accepts();

        CredentialBootstrapResult result = await new CredentialBootstrapper(protector, Impatient)
            .BootstrapAsync(sandbox.File, validator.Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Locked, result.Outcome);
        Assert.Equal(0, protector.Protects);
        Assert.Equal(0, validator.Calls);
        Assert.True(result.LockWaits > 0);
    }

    // ----------------------------------------------------------------------------------------------
    // The states that are not the matrix's, but are what an operator actually produces.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingFileIsReportedAsAnUnseededEnvironment()
    {
        using CredentialSandbox sandbox = new();

        CredentialBootstrapResult result = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.NotSeeded, result.Outcome);
        Assert.Contains("secrets.Template.json", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.File), "an absent credential file was created by a read.");
    }

    [Fact]
    public async Task AMalformedFileIsReportedWithoutQuotingTheValueThatBrokeIt()
    {
        using CredentialSandbox sandbox = new();

        // An unquoted value: the commonest hand-edit failure, and the one where the character that
        // System.Text.Json names in its own message is a character of the password.
        sandbox.Write(
            $$"""
            {
              "Encrypted": false,
              "SqlPassword": {{Secrets.SqlPassword}}
            }
            """);

        CredentialBootstrapResult result = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Malformed, result.Outcome);
        Assert.Contains("line 2", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secrets.SqlPassword, result.Message, StringComparison.Ordinal);

        // Not merely the whole value: the first few characters of it are a disclosure too, and
        // JsonException.Message quotes exactly that much.
        Assert.DoesNotContain("sql-pass", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThisIdentityCannotWriteIsReportedBeforeAnythingIsValidated()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.MonitorFile());
        File.SetAttributes(sandbox.File, FileAttributes.ReadOnly);

        RecordingValidator validator = RecordingValidator.Accepts();

        CredentialBootstrapResult result = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, validator.Delegate);

        Assert.Equal(CredentialBootstrapOutcome.AccessDenied, result.Outcome);
        Assert.Equal(0, validator.Calls);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AValidatorThatThrowsIsARejectionAndItsMessageIsNotRepeated()
    {
        using CredentialSandbox sandbox = new();
        string seeded = Secrets.LoaderFile();
        sandbox.Write(seeded);

        SpyProtector protector = new(ApplicationIdentity.Loader);

        // This is the shape of the hazard, not an invented one: EPA's auth endpoint is
        // GET /api/v1/auth/{apiId}/{apiKey}, so an HttpRequestException from it names the API Key in the
        // URI it reports. AR8 forbids that reaching a log the monitoring application can read.
        CredentialBootstrapResult result = await new CredentialBootstrapper(protector).BootstrapAsync(
            sandbox.File,
            RecordingValidator.Throws(new HttpRequestException(
                $"No such host is known (rcrainfopreprod.epa.gov:443/api/v1/auth/{Secrets.ApiId}/{Secrets.ApiKey})"))
                .Delegate);

        Assert.Equal(CredentialBootstrapOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, protector.Protects);
        Assert.Equal(seeded, sandbox.Read());
        Assert.Contains(typeof(HttpRequestException).FullName!, result.Message, StringComparison.Ordinal);

        foreach (string secret in Secrets.All)
        {
            Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------------------------------------------------
    // The acceptance criteria that are about the file rather than about the matrix.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SealingPreservesTheDenyReadAceThatIsTheActualSecurityControl()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.LoaderFile());
        sandbox.ApplyDenyReadAcl();

        (bool Protected, bool Denied) before = sandbox.DescribeAcl();

        // A positive control, and not a formality: two of the four plausible rewrite strategies discard
        // this ACE, so a test that could not first confirm the ACE was there would pass against a
        // sandbox whose ApplyDenyReadAcl silently did nothing -- which is exactly what happened while
        // this was being measured (see CredentialSandbox.ApplyDenyReadAcl).
        Assert.True(
            before.Protected && before.Denied,
            "the sandbox did not manage to apply the ACL, so nothing below is evidence of anything.");

        CredentialBootstrapResult result = await new CredentialBootstrapper(
            new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        Assert.Equal(CredentialBootstrapOutcome.Sealed, result.Outcome);

        (bool Protected, bool Denied) after = sandbox.DescribeAcl();

        Assert.True(
            after.Protected,
            "inheritance came back on after the rewrite, so a permission granted on the parent folder "
            + "now applies to the credential file.");

        Assert.True(
            after.Denied,
            $"the explicit deny for {CredentialSandbox.DeniedIdentity} did not survive the rewrite. "
            + "DPAPI LocalMachine ciphertext is decryptable by any process on the machine, so that ACE "
            + "is the only thing separating the co-resident web application from this password.");
    }

    [Fact]
    public async Task SealingCreatesNoSecondFileForTheSecretToLiveIn()
    {
        using CredentialSandbox sandbox = new();
        sandbox.Write(Secrets.LoaderFile());

        string[] justTheCredentialFile = [CredentialFile.DefaultFileName];

        Assert.Equal(justTheCredentialFile, sandbox.Files);

        await new CredentialBootstrapper(new SpyProtector(ApplicationIdentity.Loader))
            .BootstrapAsync(sandbox.File, RecordingValidator.Accepts().Delegate);

        // A temp-file-and-replace rewrite would pass every other test in this class. Its temp file is
        // created with the DIRECTORY's inherited permissions rather than the credential file's, so for
        // as long as it exists the identity the deny ACE excludes can read the ciphertext out of it.
        Assert.Equal(justTheCredentialFile, sandbox.Files);
    }
}
