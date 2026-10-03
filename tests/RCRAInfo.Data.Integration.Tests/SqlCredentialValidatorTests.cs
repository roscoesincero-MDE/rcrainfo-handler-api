using Microsoft.Data.SqlClient;

using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// <see cref="SqlCredentialValidator"/> against a real instance: the part of AR4's classification that
/// only a server can produce, and the pairing of error numbers a server is the only way to discover.
/// </summary>
/// <remarks>
/// <para>
/// The offline sibling in RCRAInfo.Data.Tests covers the three connection-string shapes that would turn
/// this validator into one that cannot fail, and every one of the five messages it can produce. What only
/// a server can supply is which <b>numbers actually arrive together</b>, and that turned out to be the
/// thing the classification got wrong.
/// </para>
/// <para>
/// <b>What this class found on 2026-09-06.</b> Opening a connection to a database the login cannot open
/// produces a <see cref="SqlException"/> carrying <c>4060, 18456</c> — both, in that order — with
/// <c>SqlException.Message</c> holding both sentences concatenated, the second of which is
/// <c>Login failed for user '…'</c>. The first version of the classification asked "is 18456 anywhere?"
/// before anything else, so the single failure whose remedy is "fix the grant, do not rotate the
/// password" was reported as "SQL Server rejected the password".
/// <see cref="AnAuthenticatedLoginThatCannotOpenItsDatabaseIsNotCalledARejection"/> is the test that
/// keeps the order right, and it uses a real 4060 from a real server because a fabricated one would have
/// been fabricated from the same wrong belief.
/// </para>
/// <para>
/// <b>This class writes no rows</b>, which makes it the one exception in this suite to the reason the
/// suite is opt-in. It is still opt-in, for a different reason: it needs a reachable instance, and the
/// suite's own rule is that an unreachable one is a failure rather than a skip. It does leave failed
/// login attempts in the SQL Server error log, which is the intended trace and is why the login it uses
/// is named the way it is.
/// </para>
/// <para>
/// <b>The login is deliberately one that does not exist, and that is not merely tidy.</b> Sending a wrong
/// password for <c>RCRAInfoLoader</c> would reach the same branch, but a SQL login created with
/// <c>CHECK_POLICY = ON</c> inherits the machine's lockout policy — and <c>net accounts</c> on this
/// workstation reports a <b>lockout threshold of 3</b> with a 15-minute duration. Two runs of this file
/// would lock the console application's own login out of the database it exists to load. A login SQL
/// Server does not have cannot be locked out, and SQL Server returns 18456 for it either way, on purpose,
/// so that the error does not disclose whether a login exists.
/// </para>
/// <para>
/// <b>What is still not covered here, and why each is a gap rather than an omission:</b> 18487/18488
/// (correct but expired) needs a login whose password has actually expired, which cannot be arranged
/// without changing the server's password policy. Error 233 arrives about once in 120 wrong-password
/// logins and cannot be summoned on demand. Both are covered offline, which is why the classifier and
/// the message table take numbers rather than a <see cref="SqlException"/>.
/// </para>
/// <para>
/// <b>Error 233 is also why this file no longer pins a protocol, and the story is worth keeping.</b>
/// <see cref="ARefusedLoginIsClassifiedAsARejectionAndForwardsTheServersOwnSentence"/> failed once on a
/// 233, and the first fix was to route it over Named Pipes, on a 20-attempt sample in which Named Pipes
/// had produced 18456 twenty times out of twenty. It failed again over Named Pipes. A 120-attempt sample
/// then put the sole 233 on <i>Named Pipes</i>, with thirty clean 18456s over Shared Memory in the same
/// run — the protocol had never been the variable, and pinning it was a test made to look deterministic
/// by a coincidence. The real fix belonged in the validator: a 233 proved nothing about the password
/// while proving the server was there, so
/// <c>SqlCredentialValidator.WorthOneMoreAttempt</c> retries it exactly once. That is what makes
/// this file deterministic, and it removes a one-in-a-hundred false failure from the product rather than
/// from the test.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class SqlCredentialValidatorTests
{
    /// <summary>
    /// A login SQL Server will not have. Nothing in this project creates it and the name is not one
    /// 040_Logins.sql could produce.
    /// </summary>
    private const string AbsentLogin = "RCRAInfoZZNoSuchLogin";

    /// <summary>Deliberately unmistakable, so "the message does not contain it" means something.</summary>
    private const string WrongPassword = "not-the-password-QQ7MARKERf3";

    /// <summary>A database name no deployment script in this project can create.</summary>
    private const string AbsentDatabase = "RCRAInfoZzNoSuchDatabase";

    [IntegrationFact]
    public async Task ARefusedLoginIsClassifiedAsARejectionAndForwardsTheServersOwnSentence()
    {
        // The load-bearing test in this file. Every other SqlException on this path means the password
        // was not checked, and only this one means it was checked and refused -- so only this one may
        // send an operator to rotate a credential. Getting it wrong the other way is worse: "could not
        // be verified" on a genuinely wrong password tells an operator to retry the run, which they
        // will, all night.
        CredentialValidation result = await Validate(IntegrationServer.Server, AbsentLogin, WrongPassword);

        Assert.False(result.IsValid);
        Assert.Contains("rejected the password", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(AbsentLogin, result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Login failed for user", result.FailureMessage, StringComparison.Ordinal);

        // Not the wording of any other branch. A message carrying two diagnoses names no remedy.
        Assert.DoesNotContain("could not be verified", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not rotate", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("correct but expired", result.FailureMessage, StringComparison.Ordinal);

        // G5 says "log the SQL error", and the server's sentence is the useful half: it names the login
        // and the instance. AR8 says that sentence is written where the monitoring web application can
        // read it, so what must not travel with it is the value that was tried.
        Assert.DoesNotContain(WrongPassword, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pwd=", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationFact]
    public async Task AnAuthenticatedLoginThatCannotOpenItsDatabaseIsNotCalledARejection()
    {
        // The test that exists because the classification was wrong. Arranged with integrated security,
        // which is the only way to reach a real 4060 without the password 050_Roles_and_Users.sql
        // generated: the current Windows identity authenticates, and then cannot open a database that
        // does not exist. That is the same server response the loader's SQL login would get from a
        // missing database user, which is the failure the message must not blame on a password.
        //
        // SqlCredentialValidator itself refuses an integrated-security template -- correctly, since it
        // would report every password as valid -- so the classifier is exercised on the exception
        // directly rather than through ValidateAsync.
        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => OpenAsync(Integrated(AbsentDatabase)));

        IReadOnlyList<int> numbers = SqlErrorNumbers.Numbers(error);

        Assert.Contains(SqlErrorNumbers.CannotOpenDatabase, numbers);

        // The finding itself, pinned: the server sends 18456 alongside the 4060, and the concatenated
        // message says "Login failed for user" in so many words.
        Assert.Contains(SqlErrorNumbers.LoginFailed, numbers);
        Assert.Contains("Login failed for user", error.Message, StringComparison.Ordinal);

        // And the classification is nevertheless "the password is right".
        Assert.Equal(
            SqlCredentialOutcome.NoDatabaseAccess,
            SqlErrorNumbers.ClassifyCredentialFailure(numbers));

        Assert.False(SqlErrorNumbers.IsCredentialRejection(error));
    }

    [IntegrationFact]
    public async Task ARejectionIsReportedIdenticallyOnASecondAttempt()
    {
        // Pooling is off on this path, and this is what that decision buys. A pool keyed on the
        // connection string would hand a repeat validation of the same password a connection nobody
        // re-authenticated -- a validator returning a cached answer, which for a password rotated
        // upstream means reporting a dead credential as good.
        string first = (await Validate(IntegrationServer.Server, AbsentLogin, WrongPassword)).FailureMessage;
        string second = (await Validate(IntegrationServer.Server, AbsentLogin, WrongPassword)).FailureMessage;

        Assert.Contains("rejected the password", first, StringComparison.Ordinal);
        Assert.Equal(first, second, StringComparer.Ordinal);
    }

    [LoaderPasswordFact]
    public async Task TheLoadersRealPasswordIsAccepted()
    {
        // The only accepting direction in this file, and the reason it is worth an environment variable:
        // a validator only ever shown to reject would pass every test above while refusing the correct
        // password too, and the bootstrapper's answer to a refusal is to seal nothing and exit non-zero.
        // That defect surfaces as a console application that never runs, with a message blaming a
        // password the operator can see is right.
        string password = Environment.GetEnvironmentVariable(LoaderPasswordFactAttribute.Variable)!;

        CredentialValidation result = await Validate(IntegrationServer.Server, "RCRAInfoLoader", password);

        Assert.True(
            result.IsValid,
            $"The password in {LoaderPasswordFactAttribute.Variable} was not accepted for " +
            $"RCRAInfoLoader: {result.FailureMessage}");
    }

    [IntegrationFact]
    public void TheValidatorRefusesTheIntegratedSecurityStringThisSuiteItselfUses()
    {
        // Worth a test here rather than only offline, because this suite's own connection string is the
        // nearest plausible thing for someone to hand the validator: it is right there, it works, and it
        // would make the validator report every password as valid.
        Assert.Throws<ArgumentException>(
            () => new SqlCredentialValidator(Integrated(IntegrationServer.Database)));
    }

    private static string Integrated(string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = IntegrationServer.Server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = false,
        }.ConnectionString;

    private static async Task OpenAsync(string connectionString)
    {
        IntegrationServer.Require();

        await using SqlConnection connection = new(connectionString);

        await connection.OpenAsync();
    }

    private static async Task<CredentialValidation> Validate(string server, string login, string password)
    {
        IntegrationServer.Require();

        SqlConnectionStringBuilder builder = new()
        {
            DataSource = server,
            InitialCatalog = IntegrationServer.Database,
            UserID = login,
            TrustServerCertificate = true,
        };

        SqlCredentialValidator validator = new(builder.ConnectionString, connectTimeoutSeconds: 5);

        return await validator.ValidateAsync(new ApplicationCredentials(password, null, null));
    }
}
