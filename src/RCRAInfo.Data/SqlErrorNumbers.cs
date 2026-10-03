using Microsoft.Data.SqlClient;

namespace RCRAInfo.Data;

/// <summary>
/// The SQL Server error numbers this project's callers act on differently, and the helpers that
/// classify one.
/// </summary>
/// <remarks>
/// <para>
/// [R12] is the reason this type exists. Every procedure in this database ends its <c>CATCH</c> with
/// a bare <c>THROW</c>, which re-raises the <i>original</i> error rather than wrapping it — so the
/// number survives the trip through the logging procedure and arrives in
/// <see cref="SqlException"/>. That is what lets the loader tell a deadlock, which it should retry,
/// from a constraint violation, which it never should: retrying a 2627 produces the same 2627 at
/// 02:00 for as many attempts as the policy allows.
/// </para>
/// <para>
/// The distinction matters because the error reaches the caller <i>whether or not it was logged</i>.
/// <c>logs.ExecutionLog</c> is a record, never the notification path — a run that failed and told
/// nobody because the log write also failed is exactly the defect the instrumentation requirement
/// was raised to prevent.
/// </para>
/// </remarks>
public static class SqlErrorNumbers
{
    /// <summary>1205 — this session was chosen as the deadlock victim. Retryable.</summary>
    public const int DeadlockVictim = 1205;

    /// <summary>1222 — lock request timed out. Retryable.</summary>
    public const int LockRequestTimeout = 1222;

    /// <summary>-2 — the client-side command timeout expired.</summary>
    /// <remarks>
    /// Not a server error at all: ADO.NET raises it and aborts the command, and the procedure's own
    /// transaction is then rolled back by the server. Seeing this from a merge or a reconciliation
    /// is a signal about
    /// <see cref="RCRAInfoDataOptions.BatchCommandTimeoutSeconds"/>, not about the data.
    /// </remarks>
    public const int CommandTimeout = -2;

    /// <summary>2627 — a PRIMARY KEY or UNIQUE constraint was violated. Not retryable.</summary>
    public const int UniqueConstraintViolation = 2627;

    /// <summary>2601 — a duplicate key for a unique index. Not retryable.</summary>
    /// <remarks>
    /// Distinct from 2627 by which object was violated — a unique <i>index</i> rather than a
    /// declared constraint — and this database's uniqueness is carried by filtered indexes
    /// (<c>WHERE IsDeleted = 0</c>), so 2601 is the more likely of the pair here.
    /// </remarks>
    public const int DuplicateKeyInUniqueIndex = 2601;

    /// <summary>547 — a FOREIGN KEY or CHECK constraint conflicted. Not retryable.</summary>
    public const int ConstraintConflict = 547;

    /// <summary>50000 — the default number for <c>THROW</c> with a message, and for
    /// <c>RAISERROR</c> with a string.</summary>
    /// <remarks>
    /// This is how every procedure in this database reports a <i>refusal</i>: a payload that is not
    /// a JSON array, a run that is not Running, a watermark rewind without
    /// <c>@AllowRewind</c>. The message is written to be read by an operator, so it is worth
    /// surfacing verbatim; the condition is deterministic, so retrying is pointless.
    /// </remarks>
    public const int ProcedureRefusal = 50000;

    /// <summary>201 — a required parameter was not supplied.</summary>
    /// <remarks>
    /// The unrecordable refusal, and the reason it is named here. SQL Server raises 201 <i>before
    /// the procedure body runs</i>, so the procedure's own <c>TRY</c>/<c>CATCH</c> never executes
    /// and no <c>logs.ExecutionLog</c> row is written. A 201 is therefore invisible in the
    /// monitoring web app by construction, and this project treats it as a caller defect — a
    /// binding bug in <see cref="RCRAInfoContext"/> — rather than as a data condition.
    /// </remarks>
    public const int MissingRequiredParameter = 201;

    /// <summary>3930 — the transaction is doomed and cannot continue.</summary>
    /// <remarks>
    /// The G37 boundary. Once <c>XACT_STATE ()</c> is -1, a <c>CATCH</c> cannot write a log row by
    /// any means, so the failure reaches the caller with nothing recorded. It appears when a caller
    /// owns a transaction around a procedure that raises — which is the situation [R12] forbids in
    /// C# for precisely this reason.
    /// </remarks>
    public const int TransactionDoomed = 3930;

    /// <summary>8672 — a MERGE statement matched the same target row more than once.</summary>
    /// <remarks>
    /// A duplicate natural key inside one payload. Scripts 400 and 520 check for this before the
    /// <c>MERGE</c> and raise a message naming the offending key, precisely so this number is not
    /// what an operator sees; encountering it means a duplicate slipped past that check.
    /// </remarks>
    public const int MergeMatchedMoreThanOnce = 8672;

    /// <summary>8152 and 2628 — a string or binary value would be truncated.</summary>
    /// <remarks>
    /// Worth naming because of the shape of the risk it stands in for. <c>OPENJSON … WITH</c>
    /// truncates an over-wide value <i>silently</i> (G36) rather than raising either of these, which
    /// is why scripts 400 and 520 shred wide and width-check before writing. Seeing 8152 means the
    /// truncation reached a real column, and 2628 is its message-with-the-column-name form.
    /// </remarks>
    public const int StringTruncated = 8152;

    /// <inheritdoc cref="StringTruncated"/>
    public const int StringTruncatedWithName = 2628;

    /// <summary>229, 262 and 300 — permission denied on an object, a database or a statement.</summary>
    /// <remarks>
    /// Neither application login holds DDL rights and both are denied metadata visibility, so a
    /// permission error is a deployment fault: the grant in the object's script did not run, or the
    /// login is not in the role. Retrying cannot help.
    /// </remarks>
    public const int PermissionDeniedOnObject = 229;

    /// <inheritdoc cref="PermissionDeniedOnObject"/>
    public const int PermissionDeniedOnDatabase = 262;

    /// <inheritdoc cref="PermissionDeniedOnObject"/>
    public const int PermissionDeniedOnStatement = 300;

    /// <summary>18456 — login failed for the user. On its own, the password is wrong.</summary>
    /// <remarks>
    /// <para>
    /// The only SQL Server error that is evidence about a password, and therefore the only one
    /// <see cref="SqlCredentialValidator"/> reports as a rejection of the credential itself. Everything
    /// else on the login path — an unreachable server, a missing database grant, an expired password —
    /// is a failure to <i>verify</i>, and AR4's rule is that an unverified secret is not sealed either
    /// way, so the distinction is about the message an operator reads rather than about what gets
    /// written.
    /// </para>
    /// <para>
    /// <b>"On its own" is load-bearing, and it was measured.</b> Opening a connection to a database the
    /// login cannot open produces a <see cref="SqlException"/> whose <c>Errors</c> collection holds
    /// <b>4060 and 18456, in that order</b>, and whose <c>SqlException.Message</c> is both
    /// sentences concatenated: <c>Cannot open database "…" requested by the login. The login
    /// failed.\nLogin failed for user '…'.</c> So a classifier that looks for 18456 anywhere and answers
    /// first calls an authenticated login a wrong password. See
    /// <see cref="ClassifyCredentialFailure"/>, which is ordered accordingly.
    /// </para>
    /// </remarks>
    public const int LoginFailed = 18456;

    /// <summary>4060 — the login cannot open the database it asked for.</summary>
    /// <remarks>
    /// Raised <b>after</b> authentication succeeds, so the password was accepted. Its message ends "The
    /// login failed" <i>and it arrives carrying an 18456 alongside it</i>, which is why it is named
    /// here: read as a password failure it sends an operator to rotate a credential that is perfectly
    /// good, when the fault is the database user or the role membership in
    /// <c>050_Roles_and_Users.sql</c>.
    /// </remarks>
    public const int CannotOpenDatabase = 4060;

    /// <summary>916 — the login has no access to the database in the current security context.</summary>
    /// <inheritdoc cref="CannotOpenDatabase"/>
    public const int NoAccessToDatabase = 916;

    /// <summary>18487 — the password has expired.</summary>
    /// <remarks>
    /// The password is correct and the account is not usable, which is a third outcome again: the
    /// remedy is to change it in SQL Server and in the password manager, then re-seed. Named so the
    /// message can say that instead of "login failed" — and classified ahead of 18456 for the same
    /// reason 4060 is, since the server sends the pair together.
    /// </remarks>
    public const int PasswordExpired = 18487;

    /// <summary>18488 — the password must be changed before the login can be used.</summary>
    /// <inheritdoc cref="PasswordExpired"/>
    public const int PasswordMustChange = 18488;

    /// <summary>
    /// 233 — the connection reached the server and then failed during the login process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named because of a measured flake, not a theory: <c>A connection was successfully established with
    /// the server, but then an error occurred during the login process. (provider: Shared Memory
    /// Provider, error: 0 - No process is on the other end of the pipe.)</c> — a login failure arriving
    /// with no number that says anything about the password.
    /// </para>
    /// <para>
    /// <b>How rare, and the reading that was wrong.</b> A first sample of twenty wrong-password logins
    /// against the local default instance saw it once over Shared Memory and never over Named Pipes,
    /// which looked like a protocol difference and was written down as one. A second sample of 120 —
    /// thirty each over Named Pipes and Shared Memory, with and without an application name — saw it
    /// <b>once, over Named Pipes, on the first attempt of its batch</b>, with thirty clean 18456s over
    /// Shared Memory in the same run. So the honest figure is roughly one in a hundred, on any
    /// same-machine protocol, and neither the protocol nor the application name predicts it. A
    /// hypothesis that it was the first connection of a process was tested separately and refuted: ten
    /// consecutive first-attempts produced ten 18456s.
    /// </para>
    /// <para>
    /// It is deliberately <b>not</b> classified as a rejection. "Established, then failed during login"
    /// is also what a server out of connections looks like, and authorising a password rotation on that
    /// evidence is the mistake 4060 already stands for. What it earns instead is a second attempt —
    /// <see cref="SqlCredentialValidator.WorthOneMoreAttempt"/>, the only outcome that gets one — and its
    /// own sentence, because the generic "could not be verified" branch ends "once the server is
    /// reachable" and the server was reachable, so that wording sends an operator to inspect a working
    /// network.
    /// </para>
    /// </remarks>
    public const int ConnectionResetDuringLogin = 233;

    /// <summary>
    /// 10054 — the connection was established and then forcibly closed by the remote host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Winsock reset, <c>WSAECONNRESET</c>, wrapped by the driver as <c>The client was unable to
    /// establish a connection because of an error during connection initialization process before login.
    /// … (provider: TCP Provider, error: 0 - An existing connection was forcibly closed by the remote
    /// host.)</c> Classified with <see cref="ConnectionResetDuringLogin"/>, because it is the same
    /// evidence: the TCP connect succeeded, so the instance is there and listening, and then the exchange
    /// ended before anything checked a password.
    /// </para>
    /// <para>
    /// <b>Measured, and it is the reason this constant exists.</b> A listener that accepts a connection
    /// and closes it — at accept, or after reading the client's PRELOGIN packet, abortively or gracefully,
    /// all four — produces <b>10054, never 233</b>. So 233 belongs to a later stage than a dropped socket
    /// reaches, and 10054 is the one of the pair that can be reproduced offline in milliseconds. That is
    /// what lets the retry in <see cref="SqlCredentialValidator.WorthOneMoreAttempt"/> be shown to happen
    /// rather than merely be shown to be authorised, which for a branch nobody can provoke on demand is
    /// the difference between a tested path and an argued one.
    /// </para>
    /// <para>
    /// In production this is a server dropping connections under load, or something between the loader and
    /// the instance resetting them. Reading it as a wrong password is the same category of mistake as
    /// reading 4060 that way, and it costs the same rotation.
    /// </para>
    /// </remarks>
    public const int ConnectionForciblyClosed = 10054;

    /// <summary>
    /// Whether the server rejected the credential itself, as opposed to failing to check it.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>
    /// <see langword="true"/> only when the failure classifies as
    /// <see cref="SqlCredentialOutcome.Rejected"/> — which an 18456 arriving alongside a 4060 does not.
    /// </returns>
    public static bool IsCredentialRejection (SqlException exception) =>
        ClassifyCredentialFailure (Numbers (exception)) == SqlCredentialOutcome.Rejected;

    /// <summary>
    /// What a failed validation login proves about the password, from the numbers the server sent.
    /// </summary>
    /// <param name="errorNumbers">
    /// Every number the failure carried — <see cref="Numbers"/>, not <see cref="SqlException.Number"/>.
    /// </param>
    /// <returns>The outcome, which is what decides the remedy the operator is given.</returns>
    /// <remarks>
    /// <para>
    /// <b>The order of these tests is the whole content of this method.</b> SQL Server sends 18456
    /// <i>together with</i> 4060 when an authenticated login cannot open its database, and the same
    /// pairing is documented for the expired-password numbers, so "is 18456 present" is not a question
    /// with a useful answer. The specific diagnoses are therefore tested first and 18456 is what is left
    /// when none of them applies.
    /// </para>
    /// <para>
    /// Takes numbers rather than a <see cref="SqlException"/> so that every branch can be tested. A
    /// <see cref="SqlException"/> cannot be constructed, and three of these outcomes cannot be provoked
    /// from a server on demand — an expired password needs a policy change and a 233 appeared once in
    /// 120 attempts — so a classifier that only accepted the real type would have most of its
    /// branches covered by nothing at all.
    /// </para>
    /// </remarks>
    public static SqlCredentialOutcome ClassifyCredentialFailure (IEnumerable<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull (errorNumbers);

        var numbers = errorNumbers as IReadOnlyCollection<int> ?? [.. errorNumbers];

        // First, because it means the password was ACCEPTED. It arrives with an 18456 attached.
        if (numbers.Contains (CannotOpenDatabase) || numbers.Contains (NoAccessToDatabase))
        {
            return SqlCredentialOutcome.NoDatabaseAccess;
        }

        // Second, because these also mean the password matched -- and they too travel with an 18456.
        if (numbers.Contains (PasswordExpired) || numbers.Contains (PasswordMustChange))
        {
            return SqlCredentialOutcome.Expired;
        }

        if (numbers.Contains (LoginFailed))
        {
            return SqlCredentialOutcome.Rejected;
        }

        // After 18456, not before: a server that sends a real login failure and then resets the socket
        // has still told us the password was refused, and that is the more specific fact.
        if (numbers.Contains (ConnectionResetDuringLogin) || numbers.Contains (ConnectionForciblyClosed))
        {
            return SqlCredentialOutcome.RefusedDuringLogin;
        }

        return SqlCredentialOutcome.Unverified;
    }

    /// <summary>
    /// Every error number carried by an exception, not merely the first.
    /// </summary>
    /// <remarks>
    /// <see cref="SqlException.Number"/> is <c>Errors[0].Number</c>, and a procedure in this
    /// database can raise more than one error in a single failure — the logging procedures' outer
    /// <c>CATCH</c> deliberately swallows nothing on the way out, and a batch can surface an
    /// informational message ahead of the one that matters. Classifying on
    /// <see cref="SqlException.Number"/> alone therefore reads the wrong error sometimes, and does
    /// so nondeterministically, which is the worst way for a retry decision to be wrong.
    /// </remarks>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns>The numbers, in the order the server reported them.</returns>
    public static IReadOnlyList<int> Numbers (SqlException exception)
    {
        ArgumentNullException.ThrowIfNull (exception);

        var numbers = new List<int> (exception.Errors.Count);
        foreach (SqlError error in exception.Errors)
        {
            numbers.Add (error.Number);
        }

        // An exception with an empty Errors collection would otherwise classify as nothing at all.
        if (numbers.Count == 0)
        {
            numbers.Add (exception.Number);
        }

        return numbers;
    }

    /// <summary>Whether the exception carries a given error number anywhere in its errors.</summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <param name="number">The error number to look for.</param>
    /// <returns><see langword="true"/> if present.</returns>
    public static bool Has (SqlException exception, int number) =>
        Numbers (exception).Contains (number);

    /// <summary>
    /// Whether the same call, issued again unchanged, could reasonably succeed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF Core's execution strategy has already retried a transient failure
    /// <see cref="RCRAInfoDataOptions.MaxRetryCount"/> times before the exception gets here, so a
    /// <see langword="true"/> from this method means those retries were exhausted — it is a signal
    /// to back off and try the handler again in a later run, not to loop immediately.
    /// </para>
    /// <para>
    /// <see cref="System.Data.Common.DbException.IsTransient"/> is deliberately consulted as well rather than
    /// replaced. It is the driver's own list, it is maintained with the driver, and the numbers
    /// named here are the ones this project's procedures can produce that it does not cover.
    /// </para>
    /// </remarks>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> when retrying is worth doing.</returns>
    public static bool IsRetryable (SqlException exception)
    {
        ArgumentNullException.ThrowIfNull (exception);

        if (exception.IsTransient)
        {
            return true;
        }

        foreach (int number in Numbers (exception))
        {
            if (number is DeadlockVictim or LockRequestTimeout or CommandTimeout)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the failure is a constraint violation — a defect in the data or in the payload,
    /// which the identical call will reproduce exactly.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> for a constraint or duplicate-key failure.</returns>
    public static bool IsConstraintViolation (SqlException exception)
    {
        ArgumentNullException.ThrowIfNull (exception);

        foreach (int number in Numbers (exception))
        {
            if (number is UniqueConstraintViolation or DuplicateKeyInUniqueIndex
                       or ConstraintConflict or MergeMatchedMoreThanOnce)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the procedure refused the call deliberately, with a message written for an operator.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> for error 50000.</returns>
    public static bool IsProcedureRefusal (SqlException exception) =>
        Has (exception, ProcedureRefusal);
}

/// <summary>
/// What a failed validation login proved about the password — which is what decides the remedy AR4's
/// failure message names.
/// </summary>
/// <remarks>
/// Every value here still means the secret is <b>not sealed</b>: AR4's rule is that an unverified
/// credential is never encrypted, and four of these five are failures to verify rather than evidence of
/// a wrong value. What differs is the sentence an operator reads at 02:00, and the cost of getting it
/// wrong is asymmetric — "rejected" on a working password buys a rotation, a new password-manager entry,
/// a re-seed and a second visit, while "could not be verified" on a wrong one buys a night of retries.
/// </remarks>
public enum SqlCredentialOutcome
{
    /// <summary>
    /// The login could not be attempted or its failure said nothing about the password. The default,
    /// deliberately: absent evidence proves nothing, and this is the value a number nobody classified
    /// lands on.
    /// </summary>
    Unverified = 0,

    /// <summary>The server refused the password. The one outcome that authorises a rotation.</summary>
    Rejected = 1,

    /// <summary>
    /// The password matched and has expired or must be changed. Change it in SQL Server and in the
    /// password manager, then re-seed.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// The login authenticated — so the password is right — and cannot open its database. A deployment
    /// fault: the database user or the role membership from <c>050_Roles_and_Users.sql</c> is missing.
    /// </summary>
    NoDatabaseAccess = 3,

    /// <summary>
    /// The connection reached the server and then failed during login without an error number saying
    /// why. Usually a wrong password on a same-machine connection; see
    /// <see cref="SqlErrorNumbers.ConnectionResetDuringLogin"/> for the measurement.
    /// </summary>
    RefusedDuringLogin = 4,
}
