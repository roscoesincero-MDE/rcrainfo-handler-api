using System.Globalization;

using Microsoft.Data.SqlClient;

using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Data;

/// <summary>
/// AR4's validation login: proves a SQL password works by using it, before anything encrypts it.
/// </summary>
/// <remarks>
/// <para>
/// This is the <see cref="CredentialValidator"/> the console application and the web application hand to
/// <c>CredentialBootstrapper</c>. It lives here rather than in RCRAInfo.Core because it is entirely about
/// SQL Server, and Core knows nothing about SQL Server — which is also what lets the whole G5 failure
/// matrix be tested offline against a delegate.
/// </para>
/// <para>
/// <b>Only a lone error 18456 is reported as a rejection of the credential.</b> Every other way the
/// open can fail — an unreachable instance, a missing database user, an expired password — means the
/// password was not <i>checked</i>, and saying "the credentials were rejected" there sends an operator to
/// rotate a working password at 2am. All of them still return a failure, because AR4's rule is that an
/// unverified secret is never sealed; the difference is the sentence the operator reads and the remedy it
/// names.
/// </para>
/// <para>
/// <b>"Lone" is the correction, and it was measured against a server.</b> A login that authenticates and
/// then cannot open its database raises <b>4060 and 18456 together</b>, so the first version of this
/// class — which asked "is 18456 present" before anything else — reported a perfectly good password as
/// rejected for the one failure whose remedy is a grant. The classification now lives in
/// <see cref="SqlErrorNumbers.ClassifyCredentialFailure"/>, where the order of the tests is the content,
/// and every message it can produce is checked by name in the unit tests.
/// </para>
/// <para>
/// <b>One outcome is retried exactly once</b>, and only one: the connection reached the server and then
/// died before anything checked a password (errors 233 and 10054). Error 233 was measured at one
/// occurrence in 120 wrong-password logins, so a single handshake is not a reliable way to learn what a
/// password is — see <see cref="WorthOneMoreAttempt"/>, which also records why a rejection must
/// <i>never</i> be retried.
/// </para>
/// <para>
/// <b>What this type never does:</b> it never puts a connection string in a message. The string holds the
/// password by construction. The template is parsed in the constructor, before any password exists, so an
/// exception from the parse cannot carry one; the password is set on a builder that is used once and
/// never rendered. <see cref="SqlException"/> messages name the login, the server and the database, and
/// none of them names the password — which is what AR8 means by "log the SQL error".
/// </para>
/// </remarks>
public sealed class SqlCredentialValidator
{
    private readonly string template;
    private readonly int connectTimeoutSeconds;

    /// <summary>
    /// Builds a validator for one SQL login.
    /// </summary>
    /// <param name="connectionStringWithoutPassword">
    /// The connection string for this application's login, carrying the server, the database and the
    /// user id, and <b>no password</b>.
    /// </param>
    /// <param name="connectTimeoutSeconds">
    /// Seconds to wait for one login. Fifteen is ADO.NET's own default and is deliberately not raised:
    /// the bootstrapper holds an exclusive lock on the credential file across this call, and
    /// <c>CredentialBootstrapOptions.LockTimeout</c> is set to four times this number so that the loser
    /// of a concurrent first run outlasts the winner's round trip. <b>Four times, not two</b>, is what
    /// leaves room for the second attempt <see cref="WorthOneMoreAttempt"/> can ask for: two logins at
    /// this timeout still fit inside the lock, with the same margin again to spare.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The connection string is empty, already carries a password, or uses integrated security.
    /// </exception>
    public SqlCredentialValidator (
        string connectionStringWithoutPassword,
        int connectTimeoutSeconds = 15)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace (connectionStringWithoutPassword);
        ArgumentOutOfRangeException.ThrowIfLessThan (connectTimeoutSeconds, 1);

        SqlConnectionStringBuilder builder = new (connectionStringWithoutPassword);

        // Refused, not overwritten. A template carrying a password means the password is sitting in
        // whatever configuration source the template came from, which is the situation AR4 exists to
        // end -- and this validator would then be the thing that reported it as fine.
        if (!string.IsNullOrEmpty (builder.Password))
        {
            throw new ArgumentException (
                "The connection string template already carries a password. The password comes from the "
                + "credential file, and a template holding one means it is also in a configuration file. "
                + "Remove it from the template.",
                nameof (connectionStringWithoutPassword));
        }

        // The important one. Integrated security ignores the password entirely, so this validator would
        // return Valid for every value including an empty one -- and the bootstrapper would then seal a
        // password nothing had checked. A validator that cannot fail is indistinguishable from one that
        // examines nothing.
        if (builder.IntegratedSecurity)
        {
            throw new ArgumentException (
                "The connection string template uses integrated security, which ignores the password. "
                + "Validating against it would report every password as valid, including a wrong one. "
                + "AR4's validation login is a SQL Server login with a User ID and a password.",
                nameof (connectionStringWithoutPassword));
        }

        if (string.IsNullOrWhiteSpace (builder.UserID))
        {
            throw new ArgumentException (
                "The connection string template names no User ID, so there is no login for the password "
                + "to belong to.",
                nameof (connectionStringWithoutPassword));
        }

        this.connectTimeoutSeconds = connectTimeoutSeconds;
        template = builder.ConnectionString;
    }

    /// <summary>The login this validator tries. Safe to log; it is a login name, not a secret.</summary>
    public string UserId => new SqlConnectionStringBuilder (template).UserID;

    /// <summary>
    /// This validator as the delegate <c>CredentialBootstrapper</c> takes.
    /// </summary>
    public CredentialValidator Delegate => ValidateAsync;

    /// <summary>
    /// Opens a connection with the supplied password and reports what happened.
    /// </summary>
    /// <param name="credentials">The credentials to try. Only <c>SqlPassword</c> is used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the password was accepted.</returns>
    public async Task<CredentialValidation> ValidateAsync (
        ApplicationCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (credentials);

        SqlConnectionStringBuilder builder = new (template)
        {
            Password = credentials.SqlPassword,
            ConnectTimeout = connectTimeoutSeconds,

            // No pooling, for two reasons that point the same way. A pooled connection outlives this
            // call, so a successful validation would leave an authenticated connection and its
            // password-bearing connection string alive in a pool group for the lifetime of the process
            // -- for a check that is over in a second and happens once. And a pool keyed on the string
            // would hand a later validation of the SAME password a connection that was never
            // re-authenticated, which is a validator reporting a cached answer.
            Pooling = false,

            ApplicationName = "RCRAInfo credential validation",
        };

        string connectionString = builder.ConnectionString;

        SqlException failure;

        try
        {
            return await AttemptAsync (connectionString, cancellationToken).ConfigureAwait (false);
        }
        catch (SqlException error)
        {
            failure = error;
        }
        catch (InvalidOperationException error)
        {
            // A malformed value reaching the driver -- the password containing something the connection
            // string grammar cannot carry, which SqlConnectionStringBuilder normally quotes for us. The
            // exception's own message is not repeated, because this is the one exception on this path
            // whose text can quote the value it choked on.
            return CredentialValidation.Invalid (
                string.Format (
                    CultureInfo.InvariantCulture,
                    "The password for '{0}' could not be used to build a connection ({1}). Nothing has "
                    + "been sealed. Re-copy the value from the password manager into the credential "
                    + "file; the value itself is not reported here because it is a secret.",
                    builder.UserID,
                    error.GetType ().FullName));
        }

        SqlCredentialOutcome outcome =
            SqlErrorNumbers.ClassifyCredentialFailure (SqlErrorNumbers.Numbers (failure));
        string serverMessage = failure.Message;

        if (WorthOneMoreAttempt (outcome))
        {
            try
            {
                return await AttemptAsync (connectionString, cancellationToken).ConfigureAwait (false);
            }
            catch (SqlException again)
            {
                outcome = SqlErrorNumbers.ClassifyCredentialFailure (SqlErrorNumbers.Numbers (again));
                serverMessage = again.Message;
            }

            // No InvalidOperationException catch here on purpose: that exception comes from the driver
            // parsing the connection string, the string is byte-for-byte the one the first attempt
            // already got past, and swallowing it here would report a driver defect as a credential
            // failure. It reaches the bootstrapper, which names the type and seals nothing.
        }

        return CredentialValidation.Invalid (Describe (outcome, builder.UserID, serverMessage));
    }

    private static async Task<CredentialValidation> AttemptAsync (
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new (connectionString);

        await connection.OpenAsync (cancellationToken).ConfigureAwait (false);

        return CredentialValidation.Valid;
    }

    /// <summary>
    /// Whether a classified failure earns one immediate second login.
    /// </summary>
    /// <param name="outcome">What the first attempt proved about the password.</param>
    /// <returns>
    /// <see langword="true"/> only for <see cref="SqlCredentialOutcome.RefusedDuringLogin"/> — the one
    /// outcome that proved nothing while the server was demonstrably there.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Measured, and the measurement corrected an earlier one.</b> Error 233 — "established with the
    /// server, but then an error occurred during the login process" — appeared <b>once in 120</b>
    /// consecutive wrong-password logins against the local default instance on 2026-09-06, and it
    /// appeared on <b>the first attempt of its batch</b>. An earlier 20-attempt sample had it once over
    /// Shared Memory and never over Named Pipes, which looked like a protocol difference; the larger
    /// sample put the sole occurrence on Named Pipes, with 30 clean 18456s over Shared Memory in the
    /// same run. So it is neither protocol-dependent nor tied to the application name, and a single
    /// login is not a reliable way to learn what a password is.
    /// </para>
    /// <para>
    /// Retrying it is not a convenience. This branch's own remedy is "repeat the run", and the run here
    /// is one TDS handshake — so the validator can simply do it, and then the outcome means the failure
    /// survived a repeat rather than that a handshake flaked. Without the retry, roughly one bootstrap in
    /// a hundred reports a correct password as unverifiable and exits non-zero, which for a scheduled
    /// console application is a missed load with a message blaming nothing.
    /// </para>
    /// <para>
    /// <b>That the retry is issued, and not merely authorised here, is tested</b> — on
    /// <see cref="SqlErrorNumbers.ConnectionForciblyClosed"/> rather than on 233, because a listener that
    /// accepts a socket and closes it produces 10054 offline in milliseconds and 233 turned out to belong
    /// to a later stage than a dropped socket reaches. A predicate returning <see langword="true"/> with
    /// nothing showing that <c>ValidateAsync</c> reads it would leave the wiring — the deletable part —
    /// covered by nothing.
    /// </para>
    /// <para>
    /// <b>Nothing else may be retried, and <see cref="SqlCredentialOutcome.Rejected"/> least of all.</b>
    /// A SQL login created with <c>CHECK_POLICY = ON</c> inherits the machine's lockout policy, and
    /// <c>net accounts</c> on this workstation reports a <b>threshold of 3</b> with a 15-minute
    /// duration. Retrying a genuine rejection would spend two of those three on every wrong password and
    /// lock the loader out of the database it exists to load — turning a fixable typo into a fifteen
    /// minute outage. The other three outcomes are all deterministic: the same call reproduces them
    /// exactly, so a second attempt buys a second identical answer.
    /// </para>
    /// </remarks>
    internal static bool WorthOneMoreAttempt (SqlCredentialOutcome outcome) =>
        outcome == SqlCredentialOutcome.RefusedDuringLogin;

    /// <summary>
    /// The sentence an operator reads for one classified failure.
    /// </summary>
    /// <param name="outcome">What the failure proved about the password.</param>
    /// <param name="userId">The login. Safe to name; it is not a secret.</param>
    /// <param name="serverMessage">
    /// The server's own text, forwarded verbatim. It names the login, the instance and the database and
    /// never the password, which is what AR8 means by "log the SQL error".
    /// </param>
    /// <returns>The failure message.</returns>
    /// <remarks>
    /// <para>
    /// Split out from the <c>catch</c> and made <c>internal</c> so every branch can be tested. Three of
    /// the five outcomes cannot be provoked from a server on demand — an expired password needs the
    /// server's policy changed, a 233 appeared once in 120 attempts — and a
    /// <see cref="SqlException"/> cannot be constructed, so a message table reachable only through a
    /// real failure would have most of its rows covered by nothing.
    /// </para>
    /// <para>
    /// Each message names <b>one</b> remedy, and the four wordings are deliberately disjoint: an
    /// operator who reads two diagnoses has been told to do two things, one of which is wrong, and the
    /// wrong one here costs a password rotation.
    /// </para>
    /// </remarks>
    internal static string Describe (SqlCredentialOutcome outcome, string userId, string serverMessage) =>
        outcome switch
        {
            SqlCredentialOutcome.Rejected => string.Format (
                CultureInfo.InvariantCulture,
                "SQL Server rejected the password for login '{0}': {1}",
                userId,
                serverMessage),

            SqlCredentialOutcome.Expired => string.Format (
                CultureInfo.InvariantCulture,
                "The password for login '{0}' is correct but expired: {1} Change it in SQL Server and "
                + "in the password manager, then re-seed the credential file. Nothing has been sealed.",
                userId,
                serverMessage),

            SqlCredentialOutcome.NoDatabaseAccess => string.Format (
                CultureInfo.InvariantCulture,
                "Login '{0}' authenticated, so the password is right, but it cannot open the database: "
                + "{1} This is a deployment fault rather than a credential one -- the database user or "
                + "the role membership from 050_Roles_and_Users.sql is missing. Do not rotate the "
                + "password. Nothing has been sealed.",
                userId,
                serverMessage),

            // Note what this one does NOT say: "once the server is reachable". The server was reached.
            // And it says "twice" because ValidateAsync has already retried -- WorthOneMoreAttempt is
            // the only route to this outcome, so the two must be changed together.
            SqlCredentialOutcome.RefusedDuringLogin => string.Format (
                CultureInfo.InvariantCulture,
                "The login for '{0}' reached the server and then failed during the login process twice, "
                + "with no error number that says anything about the password: {1} The validation was "
                + "already retried once and failed the same way, so this is not the momentary handshake "
                + "fault this error usually is. Two readings fit and the failure itself cannot tell them "
                + "apart: the instance is dropping connections -- under load, or through something "
                + "between it and this machine -- or the password is wrong and its 18456 never arrived. "
                + "The SQL Server error log separates them: a failed login recorded for '{0}' at this "
                + "time means the password reached authentication and was refused, and no entry at all "
                + "means nothing got that far. Nothing has been sealed.",
                userId,
                serverMessage),

            _ => string.Format (
                CultureInfo.InvariantCulture,
                "The password for login '{0}' could not be verified, which is not the same as it being "
                + "wrong: {1} Nothing has been sealed, so the credential file is unchanged and this run "
                + "can simply be repeated once the server is reachable.",
                userId,
                serverMessage),
        };
}
