using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RCRAInfo.Core.Credentials;

/// <summary>
/// AR4, end to end: read the credential file, prove the credentials work, and — if they were in
/// plaintext — seal them and rewrite the file, once, under an exclusive lock.
/// </summary>
/// <remarks>
/// <para>
/// This is Analysis §6.1's G5 matrix as one method. Each row is an outcome of
/// <see cref="CredentialBootstrapOutcome"/> and each is reproduced by a test:
/// </para>
/// <list type="table">
///   <item><term><c>Encrypted: false</c>, validation succeeds</term>
///         <description>seal, rewrite, continue — <see cref="CredentialBootstrapOutcome.Sealed"/></description></item>
///   <item><term><c>Encrypted: false</c>, validation fails</term>
///         <description>leave the plaintext exactly as it was, report the reason, do not continue</description></item>
///   <item><term><c>Encrypted: true</c>, decryption fails</term>
///         <description>fail fast naming the re-seed procedure; never overwrite, never fall back</description></item>
///   <item><term><c>Encrypted: true</c>, decrypts but validation fails</term>
///         <description>report the reason, do not continue — the password was rotated upstream</description></item>
///   <item><term>concurrent first run</term>
///         <description>exclusive lock, and the flag is re-read <b>inside</b> it</description></item>
/// </list>
/// <para>
/// <b>Why the whole of it happens inside one open handle.</b> The lock is not a separate lock file: it
/// is the credential file itself, opened <see cref="FileShare.None"/>. Read, decide, and rewrite all go
/// through that handle, which is what makes "re-read and re-check <c>Encrypted</c> inside the lock" true
/// rather than merely intended — there is no window between the check and the write for the other
/// application to seal the file first. Validation happens inside the lock too, which means the loser of
/// the race waits out the winner's network round trip; see
/// <see cref="CredentialBootstrapOptions.LockTimeout"/>.
/// </para>
/// <para>
/// <b>Why the rewrite is in place, and not the safer-looking temp-file swap.</b> The file's DACL is the
/// only thing standing between the co-resident web application and this application's SQL password
/// (Analysis §6.1, G18: <c>LocalMachine</c> ciphertext is decryptable by any process on the box, and
/// <c>Set-CredentialFileAcl.ps1</c> writes an explicit deny for the IIS application pool identity).
/// Measured on this project, against a file carrying that deny ACE with inheritance switched off:
/// </para>
/// <list type="table">
///   <item><term>temp file + <c>File.Replace</c></term><description>DACL preserved</description></item>
///   <item><term>temp file + <c>MoveFileEx</c> replace</term><description><b>deny ACE gone, inheritance back on</b></description></item>
///   <item><term>delete + recreate</term><description><b>deny ACE gone, inheritance back on</b></description></item>
///   <item><term>in-place truncate and write</term><description>DACL preserved</description></item>
/// </list>
/// <para>
/// Two of the four obvious ways to rewrite a file silently discard the ACE, and the two that survive
/// are not equally good here: <c>File.Replace</c> preserves the destination's DACL but the temp file it
/// needs is created with the <i>directory's</i> inherited permissions, so for the moment it exists the
/// application pool identity can read the ciphertext out of it. In-place has no second file, cannot
/// lose the DACL, and needs no ACL code of its own.
/// </para>
/// <para>
/// The cost of in-place is a crash window: a process that dies between the truncate and the flush
/// leaves a file with no recoverable password in it. That is accepted rather than engineered around,
/// because the remedy already exists and is already mandatory — the password is in the password manager
/// (plan §4.2), and re-seeding is the documented response to the far more likely machine rebuild. A
/// test asserts that no sibling file is ever created, so a later refactor to the temp-file form has to
/// argue with a failing test rather than quietly hand the web application a readable copy.
/// </para>
/// </remarks>
public sealed class CredentialBootstrapper
{
    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION. The two Win32 errors that mean "someone else
    // has it, try again", as opposed to every other IOException, which means stop.
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly ISecretProtector protector;
    private readonly CredentialBootstrapOptions options;

    /// <summary>Creates a bootstrapper for one application's credential file.</summary>
    /// <param name="protector">
    /// The protector for this application — see <see cref="ApplicationIdentity"/> for why it is
    /// per-application.
    /// </param>
    /// <param name="options">Lock timings, or <see langword="null"/> for the defaults.</param>
    public CredentialBootstrapper(ISecretProtector protector, CredentialBootstrapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(protector);

        this.protector = protector;
        this.options = options ?? CredentialBootstrapOptions.Default;
    }

    /// <summary>Runs the AR4 bootstrap against one credential file.</summary>
    /// <param name="path">The credential file — see <see cref="CredentialFile.DefaultFileName"/>.</param>
    /// <param name="validate">
    /// Proves the credentials work. Called on every path that reaches usable plaintext, and always
    /// <b>before</b> anything is sealed.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// What happened, and on success the credentials. Never throws for any condition the matrix covers:
    /// an unattended 2am run needs a message and an exit code, not a stack trace.
    /// </returns>
    public async Task<CredentialBootstrapResult> BootstrapAsync(
        string path,
        CredentialValidator validate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(validate);

        LockAttempt attempt = await AcquireAsync(path, cancellationToken).ConfigureAwait(false);

        if (attempt.Stream is null)
        {
            return new CredentialBootstrapResult(
                attempt.Outcome, Credentials: null, attempt.Message, attempt.Waits);
        }

        await using FileStream file = attempt.Stream;

        CredentialFile contents;

        try
        {
            contents = CredentialFile.Parse(await ReadAllTextAsync(file, cancellationToken)
                                                .ConfigureAwait(false));
        }
        catch (CredentialFileFormatException error)
        {
            return Failure(
                CredentialBootstrapOutcome.Malformed,
                $"'{path}' cannot be read as a credential file. {error.Message}",
                attempt.Waits);
        }

        return contents.Encrypted
            ? await ContinueSealedAsync(path, contents, validate, attempt.Waits, cancellationToken)
                  .ConfigureAwait(false)
            : await SealAsync(path, file, contents, validate, attempt.Waits, cancellationToken)
                  .ConfigureAwait(false);
    }

    /// <summary>
    /// The steady state, plus G5 rows 3 and 4: the file is sealed, so open it and prove it still works.
    /// </summary>
    private async Task<CredentialBootstrapResult> ContinueSealedAsync(
        string path,
        CredentialFile contents,
        CredentialValidator validate,
        int waits,
        CancellationToken cancellationToken)
    {
        ApplicationCredentials credentials;

        try
        {
            credentials = contents.Unseal(protector);
        }
        catch (CryptographicException error)
        {
            // G5 row 3, and the one row with an explicit prohibition attached to it. Nothing is written
            // here -- not a repaired file, not a plaintext fallback, not a fresh seal of whatever the
            // file happened to contain. The file is left exactly as found so that an operator arriving
            // with the password manager open has something to compare against.
            return Failure(
                CredentialBootstrapOutcome.DecryptionFailed,
                $"'{path}' records Encrypted: true but this machine cannot decrypt it: {error.Message} "
                + "DPAPI ciphertext is bound to the machine, so the usual causes are a server rebuild "
                + "or migration, or a change of service account. Re-seed the file: replace the values "
                + "with the plaintext from the password manager, set Encrypted to false, re-apply the "
                + "ACL with Set-CredentialFileAcl.ps1, and run this application once. The file has not "
                + "been modified and will not be modified until it holds plaintext again.",
                waits);
        }

        CredentialValidation validation =
            await ValidateAsync(validate, credentials, cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            // G5 row 4. Nothing to leave in place and nothing to repair: the stored secret is sealed and
            // correct as far as this machine is concerned, and it is the far end that has changed.
            return Failure(
                CredentialBootstrapOutcome.ValidationFailed,
                $"The credentials in '{path}' decrypted but were rejected: {validation.FailureMessage} "
                + "The likely cause is a credential rotated where it is issued rather than here. "
                + "Re-seed the file with the new value.",
                waits);
        }

        return new CredentialBootstrapResult(
            CredentialBootstrapOutcome.Ready,
            credentials,
            $"'{path}' is sealed and its credentials were accepted.",
            waits);
    }

    /// <summary>G5 rows 1 and 2: the file holds plaintext, so validate it and only then seal it.</summary>
    private async Task<CredentialBootstrapResult> SealAsync(
        string path,
        FileStream file,
        CredentialFile contents,
        CredentialValidator validate,
        int waits,
        CancellationToken cancellationToken)
    {
        ApplicationCredentials credentials = contents.ReadPlaintext();

        CredentialValidation validation =
            await ValidateAsync(validate, credentials, cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            // G5 row 2, and the reason it is a row of its own: an unverified secret is not sealed. An
            // operator typo has to stay correctable by editing the file, and it stops being correctable
            // the moment the typo is encrypted into something nobody can read back.
            return Failure(
                CredentialBootstrapOutcome.ValidationFailed,
                $"The plaintext credentials in '{path}' were rejected: {validation.FailureMessage} They "
                + "have been left in the file exactly as they are, unencrypted, so the value can be "
                + "corrected by editing it. Nothing has been sealed.",
                waits);
        }

        // Every byte is produced before the handle is touched. The alternative -- truncate, then seal
        // each secret as it is written -- puts a protector call after the point where the old contents
        // are gone, and a throw there destroys a file that has no backup.
        string resealed = contents.Seal(protector);
        byte[] bytes = Encoding.UTF8.GetBytes(resealed);

        try
        {
            file.Position = 0;
            file.SetLength(0);
            await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);

            // To the disk, not merely out of the FileStream buffer. An unattended machine that loses
            // power between the seal and the flush is the crash window described in the class remarks;
            // this is what makes that window as small as the file system allows.
            file.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Failure(
                CredentialBootstrapOutcome.SealFailed,
                $"The credentials in '{path}' were accepted, but the sealed file could not be written: "
                + $"{error.Message} The secret is still valid and still in plaintext on disk, so this "
                + "run is a failure rather than a warning. Give the application's identity write access "
                + "to that one file -- Set-CredentialFileAcl.ps1 grants read and write for exactly this "
                + "reason -- and run it again.",
                waits);
        }

        return new CredentialBootstrapResult(
            CredentialBootstrapOutcome.Sealed,
            credentials,
            $"The credentials in '{path}' were accepted and the file has been rewritten with "
            + "Encrypted: true. This happens once per application per machine; a later run reporting it "
            + "again means the file was replaced or the machine was rebuilt.",
            waits);
    }

    /// <summary>
    /// Calls the validator, and turns a thrown exception into a rejection.
    /// </summary>
    /// <remarks>
    /// <b>The exception's message is deliberately not reported.</b> A validator is expected to
    /// <i>return</i> a rejection carrying a message it has judged safe — that is the sanctioned channel,
    /// and it is the one G5's "log the SQL error" means. A thrown exception is the unsanctioned path and
    /// gets its type only, because of what the API validator throws: EPA's auth endpoint is
    /// <c>GET /api/v1/auth/{apiId}/{apiKey}</c>, so the credentials are in the URI <b>path</b>, and an
    /// <c>HttpRequestException</c> from that call carries the API Key in the request URI it names. AR8
    /// forbids that reaching a log the monitoring web application can read, and this is the one place in
    /// the credential code where it could arrive without anyone writing it down.
    /// </remarks>
    private static async Task<CredentialValidation> ValidateAsync(
        CredentialValidator validate,
        ApplicationCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            return await validate(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled run is not a rejected credential, and reporting it as one would seal nothing
            // and blame the password.
            throw;
        }
        catch (Exception error)
        {
            return CredentialValidation.Invalid(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the validation attempt threw {0}. Its message is not reproduced here: the RCRAInfo "
                    + "auth endpoint carries the API ID and Key in the request path, so an exception "
                    + "from the HTTP stack names the credential. Look in this application's own log for "
                    + "the full exception, which is written where the monitoring application cannot "
                    + "read it.",
                    error.GetType().FullName));
        }
    }

    /// <summary>
    /// Opens the credential file exclusively, waiting out the other application if it got there first.
    /// </summary>
    private async Task<LockAttempt> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int waits = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                FileStream stream = new(
                    path,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous,
                    });

                return new LockAttempt(stream, CredentialBootstrapOutcome.Ready, string.Empty, waits);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return new LockAttempt(
                    null,
                    CredentialBootstrapOutcome.NotSeeded,
                    $"There is no credential file at '{path}'. This environment has not been seeded: "
                    + "copy secrets.Template.json to that path, fill in the values from the password "
                    + "manager, leave Encrypted as false, apply the ACL with Set-CredentialFileAcl.ps1, "
                    + "and run this application once (plan §4.2).",
                    waits);
            }
            catch (UnauthorizedAccessException error)
            {
                return new LockAttempt(
                    null,
                    CredentialBootstrapOutcome.AccessDenied,
                    $"'{path}' could not be opened for read and write: {error.Message} This application "
                    + "needs both on that one file, because it rewrites it on first run. Check that the "
                    + "ACL names the identity this application actually runs as, and that the file is "
                    + "not marked read-only.",
                    waits);
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                TimeSpan waited = Stopwatch.GetElapsedTime(started);

                if (waited >= options.LockTimeout)
                {
                    return new LockAttempt(
                        null,
                        CredentialBootstrapOutcome.Locked,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "'{0}' was held by another process for the whole {1:0.#}s this application "
                            + "waited for it ({2} attempt(s)). Under G18 the console and web "
                            + "applications share a machine, so both starting at once is expected and "
                            + "is waited out; a wait this long instead means the other process is stuck "
                            + "in its own validation, or a tool has the file open.",
                            path,
                            options.LockTimeout.TotalSeconds,
                            waits + 1),
                        waits + 1);
                }

                waits++;
                await Task.Delay(options.LockPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private static async Task<string> ReadAllTextAsync(FileStream file, CancellationToken cancellationToken)
    {
        file.Position = 0;

        // detectEncodingFromByteOrderMarks, because a file seeded with Notepad has a UTF-8 BOM on it and
        // JsonNode.Parse rejects one as an invalid start of value -- which would report a hand-seeded
        // file as malformed JSON at line 0.
        using StreamReader reader = new(
            file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CredentialBootstrapResult Failure(
        CredentialBootstrapOutcome outcome, string message, int waits) =>
        new(outcome, Credentials: null, message, waits);

    private sealed record LockAttempt(
        FileStream? Stream, CredentialBootstrapOutcome Outcome, string Message, int Waits);
}
