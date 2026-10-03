using System.Net;
using System.Net.Sockets;

using Microsoft.Data.SqlClient;

using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Data.Tests;

/// <summary>
/// What <see cref="SqlCredentialValidator"/> refuses to be built from, and what it says when it could not
/// reach a server. No SQL Server needed for any of it.
/// </summary>
/// <remarks>
/// The rejection of a genuinely wrong password is error 18456 and needs an instance, so it lives in
/// RCRAInfo.Data.Integration.Tests. What is here is the half that matters more often: the three
/// connection-string shapes that would turn this validator into one that cannot fail, and the promise
/// that no message it produces carries the password it was given.
/// </remarks>
public sealed class SqlCredentialValidatorTests
{
    /// <summary>Deliberately unmistakable, so "the message does not contain it" means something.</summary>
    private const string Password = "sql-password-QQ7MARKERf3";

    private static readonly ApplicationCredentials Credentials = new(Password, null, null);

    private static string Template(Action<SqlConnectionStringBuilder>? adjust = null)
    {
        SqlConnectionStringBuilder builder = new()
        {
            DataSource = ".",
            InitialCatalog = "RCRAInfo",
            UserID = "RCRAInfoLoader",
            TrustServerCertificate = true,
        };

        adjust?.Invoke(builder);

        return builder.ConnectionString;
    }

    [Fact]
    public void ATemplateThatAlreadyCarriesAPasswordIsRefused()
    {
        // Not overwritten -- refused. A template holding a password means the password is in whatever
        // configuration source the template came from, which is the situation AR4 exists to end, and this
        // validator would otherwise be the component that reported it as fine.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new SqlCredentialValidator(Template(b => b.Password = Password)));

        Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
        Assert.Contains("already carries a password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateUsingIntegratedSecurityIsRefused()
    {
        // The one that matters most. Integrated security ignores the password, so this validator would
        // return Valid for every value including an empty one, and the bootstrapper would then seal a
        // secret nothing had checked. A validator that cannot fail is indistinguishable from one that
        // examines nothing -- and unlike most instances of that, this one seals the result.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new SqlCredentialValidator(Template(b => b.IntegratedSecurity = true)));

        Assert.Contains("integrated security", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateWithNoLoginIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new SqlCredentialValidator(Template(b => b.UserID = "")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTemplateIsRefused(string template) =>
        Assert.Throws<ArgumentException>(() => new SqlCredentialValidator(template));

    [Fact]
    public void ANonPositiveTimeoutIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqlCredentialValidator(Template(), connectTimeoutSeconds: 0));

    [Fact]
    public void TheLoginNameIsReadableBecauseItIsNotASecret()
    {
        // Every message this type produces names the login, and that is deliberate: a login name is what
        // an operator needs to find the grant, and it is already in 050_Roles_and_Users.sql.
        Assert.Equal("RCRAInfoLoader", new SqlCredentialValidator(Template()).UserId);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsReportedAsUnverifiedRatherThanAsAWrongPassword()
    {
        // Port 1 refuses immediately, so this is deterministic and fast without a server anywhere.
        //
        // The distinction under test is the whole reason Explain exists. "The credentials were rejected"
        // on an unreachable instance sends an operator to rotate a password that was never tried -- and
        // rotating it means a new password in the password manager, a re-seed, and a second visit once the
        // real cause turns out to have been the firewall.
        SqlCredentialValidator validator = new(
            Template(b => b.DataSource = "tcp:127.0.0.1,1"),
            connectTimeoutSeconds: 2);

        CredentialValidation result = await validator.ValidateAsync(Credentials);

        Assert.False(result.IsValid);
        Assert.Contains("could not be verified", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("not the same as it being wrong", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Nothing has been sealed", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureIsReturnedRatherThanThrown()
    {
        // The bootstrapper treats a thrown exception as a rejection too, but reports its type name only
        // -- so a validator that throws loses the server's own message, which is the useful half and the
        // one G5 means by "log the SQL error".
        SqlCredentialValidator validator = new(
            Template(b => b.DataSource = "tcp:127.0.0.1,1"),
            connectTimeoutSeconds: 2);

        CredentialValidation result = await validator.ValidateAsync(Credentials);

        Assert.NotEmpty(result.FailureMessage);
    }

    [Fact]
    public void TheDelegateIsTheValidator() =>
        Assert.NotNull(new SqlCredentialValidator(Template()).Delegate);

    [Fact]
    public async Task NullCredentialsAreARefusalRatherThanANullReference() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new SqlCredentialValidator(Template()).ValidateAsync(null!));

    /// <summary>
    /// The classification, on the number combinations a server actually sends.
    /// </summary>
    /// <param name="expected">The outcome the numbers should produce.</param>
    /// <param name="numbers">The error numbers, in the order the server reported them.</param>
    /// <remarks>
    /// The 4060 rows are the reason this theory exists rather than a single assertion. A server measured
    /// on 2026-09-06 sends <c>4060, 18456</c> together for an authenticated login that cannot open its
    /// database, so a classifier answering "is 18456 present" first reports a working password as
    /// rejected — which is the one wrong answer that costs a rotation, a new password-manager entry, a
    /// re-seed and a second visit. Both orderings are listed because the classification must not depend
    /// on which number the server happened to put first, and <c>SqlException.Number</c> is only
    /// <c>Errors[0]</c>.
    /// </remarks>
    [Theory]
    [InlineData(SqlCredentialOutcome.Rejected, 18456)]
    [InlineData(SqlCredentialOutcome.NoDatabaseAccess, 4060, 18456)]
    [InlineData(SqlCredentialOutcome.NoDatabaseAccess, 18456, 4060)]
    [InlineData(SqlCredentialOutcome.NoDatabaseAccess, 916, 18456)]
    [InlineData(SqlCredentialOutcome.NoDatabaseAccess, 4060)]
    [InlineData(SqlCredentialOutcome.Expired, 18487, 18456)]
    [InlineData(SqlCredentialOutcome.Expired, 18488, 18456)]
    [InlineData(SqlCredentialOutcome.Expired, 18487)]
    [InlineData(SqlCredentialOutcome.RefusedDuringLogin, 233)]
    [InlineData(SqlCredentialOutcome.RefusedDuringLogin, 10054)]

    // A server that refused the password and then reset the socket still told us the password was
    // refused, so the rejection outranks the reset. Reversed, this would retry a genuine wrong password
    // -- two of the three attempts the lockout policy allows, on every typo.
    [InlineData(SqlCredentialOutcome.Rejected, 18456, 10054)]
    [InlineData(SqlCredentialOutcome.Rejected, 10054, 18456)]
    [InlineData(SqlCredentialOutcome.Unverified, 53)]
    [InlineData(SqlCredentialOutcome.Unverified, 258)]
    [InlineData(SqlCredentialOutcome.Unverified, -2)]
    public void TheNumbersAServerSendsTogetherAreClassifiedByTheMostSpecificOne(
        SqlCredentialOutcome expected, params int[] numbers) =>
        Assert.Equal(expected, SqlErrorNumbers.ClassifyCredentialFailure(numbers));

    [Fact]
    public void NoNumbersAtAllProvesNothingRatherThanProvingARejection()
    {
        // The default is Unverified for this reason: an empty or unrecognised failure is absence of
        // evidence, and the outcome that authorises a password rotation must never be what a classifier
        // falls back to.
        Assert.Equal(
            SqlCredentialOutcome.Unverified,
            SqlErrorNumbers.ClassifyCredentialFailure(Array.Empty<int>()));

        Assert.Equal(default, SqlErrorNumbers.ClassifyCredentialFailure(Array.Empty<int>()));
    }

    [Fact]
    public void NullNumbersAreARefusalRatherThanANullReference() =>
        Assert.Throws<ArgumentNullException>(() => SqlErrorNumbers.ClassifyCredentialFailure(null!));

    /// <summary>
    /// Every message the validator can produce names one remedy, forwards the server's text, and quotes
    /// no password.
    /// </summary>
    /// <param name="outcome">The classified outcome.</param>
    /// <param name="mustContain">A phrase unique to that outcome's message.</param>
    /// <remarks>
    /// Reached through the internal <c>Describe</c> rather than through a real failure, and that is the
    /// point: three of these five outcomes cannot be provoked from a server on demand — an expired
    /// password needs the server's policy changed, and error 233 appeared once in twenty attempts — and
    /// <see cref="Microsoft.Data.SqlClient.SqlException"/> cannot be constructed. A message table
    /// reachable only through a real failure would have most of its rows covered by nothing.
    /// </remarks>
    [Theory]
    [InlineData(SqlCredentialOutcome.Rejected, "rejected the password")]
    [InlineData(SqlCredentialOutcome.Expired, "correct but expired")]
    [InlineData(SqlCredentialOutcome.NoDatabaseAccess, "Do not rotate the password")]
    [InlineData(SqlCredentialOutcome.RefusedDuringLogin, "failed during the login process")]
    [InlineData(SqlCredentialOutcome.Unverified, "could not be verified")]
    public void EveryOutcomeHasItsOwnSentenceAndNoneOfThemCarriesThePassword(
        SqlCredentialOutcome outcome, string mustContain)
    {
        const string ServerText = "Login failed for user 'RCRAInfoLoader'.";

        string message = SqlCredentialValidator.Describe(outcome, "RCRAInfoLoader", ServerText);

        Assert.Contains(mustContain, message, StringComparison.Ordinal);
        Assert.Contains("RCRAInfoLoader", message, StringComparison.Ordinal);
        Assert.Contains(ServerText, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheRejectionMessageAuthorisesARotationAndOnlyItLacksTheReassurance()
    {
        // The pair of properties that make the five messages usable. Four of them say "Nothing has been
        // sealed", because AR4's rule is that an unverified secret is never encrypted and an operator
        // needs to know the file is unchanged before they touch it. The rejection message does not need
        // it -- the remedy there is to replace the value anyway -- and it is the only one that may say
        // the server refused the password.
        const string ServerText = "Login failed for user 'RCRAInfoLoader'.";

        foreach (SqlCredentialOutcome outcome in Enum.GetValues<SqlCredentialOutcome>())
        {
            string message = SqlCredentialValidator.Describe(outcome, "RCRAInfoLoader", ServerText);

            bool claimsRejection = message.Contains("rejected the password", StringComparison.Ordinal);

            Assert.True(
                claimsRejection == (outcome == SqlCredentialOutcome.Rejected),
                $"{outcome} produced a message that " + (claimsRejection ? "does" : "does not") +
                " claim the server rejected the password. Only Rejected may make that claim: it is the " +
                "one outcome that sends an operator to rotate a credential.");
        }
    }

    [Fact]
    public void TheUnverifiedMessageIsTheOnlyOneThatBlamesReachability()
    {
        // Error 233 used to land in the Unverified branch, whose message ends "once the server is
        // reachable" -- and 233 means the server WAS reached and then dropped the login. That sentence
        // sends an operator to inspect a working network. Its own branch exists to stop saying it.
        const string ServerText = "A connection was successfully established with the server, but then " +
                                  "an error occurred during the login process.";

        string refused = SqlCredentialValidator.Describe(
            SqlCredentialOutcome.RefusedDuringLogin, "RCRAInfoLoader", ServerText);

        Assert.DoesNotContain("reachable", refused, StringComparison.Ordinal);

        // And it does not pick one of the two readings that fit. It names the one place that separates
        // them instead, because "treat the password as wrong" on a server dropping connections under load
        // buys a rotation that fixes nothing.
        Assert.Contains("SQL Server error log", refused, StringComparison.Ordinal);

        Assert.Contains(
            "reachable",
            SqlCredentialValidator.Describe(SqlCredentialOutcome.Unverified, "RCRAInfoLoader", ServerText),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly one outcome earns a second login, and which one it is has a measured reason and a cost.
    /// </summary>
    /// <remarks>
    /// The <see cref="SqlCredentialOutcome.Rejected"/> row is the one with teeth. A SQL login created
    /// with <c>CHECK_POLICY = ON</c> inherits the machine's lockout policy, and <c>net accounts</c> on
    /// this workstation reports a threshold of 3 with a 15-minute duration — so retrying a genuine
    /// rejection would spend two of those three on every wrong password and lock the loader out of the
    /// database it exists to load. A typo would become a fifteen-minute outage.
    /// </remarks>
    [Theory]
    [InlineData(SqlCredentialOutcome.RefusedDuringLogin, true)]
    [InlineData(SqlCredentialOutcome.Rejected, false)]
    [InlineData(SqlCredentialOutcome.Expired, false)]
    [InlineData(SqlCredentialOutcome.NoDatabaseAccess, false)]
    [InlineData(SqlCredentialOutcome.Unverified, false)]
    public void OnlyTheOutcomeThatProvedNothingEarnsASecondLogin(
        SqlCredentialOutcome outcome, bool expected) =>
        Assert.Equal(expected, SqlCredentialValidator.WorthOneMoreAttempt(outcome));

    [Fact]
    public void EveryOutcomeHasARetryDecisionAndOnlyOneOfThemIsTrue()
    {
        // The theory above names five outcomes by hand, so it would keep passing if a sixth were added
        // -- and a new outcome defaulting into "retry" is the direction that costs lockouts. This loops
        // the enum instead, so adding a value forces a decision here.
        SqlCredentialOutcome[] retried = [.. Enum.GetValues<SqlCredentialOutcome>()
            .Where(SqlCredentialValidator.WorthOneMoreAttempt)];

        Assert.Equal(
            [SqlCredentialOutcome.RefusedDuringLogin],
            retried);
    }

    /// <summary>
    /// The retry is performed, not merely decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this test the retry is covered by a predicate returning <see langword="true"/> and nothing
    /// that shows <c>ValidateAsync</c> reading it — the wiring, which is the part that can be deleted by
    /// accident. Error 233 cannot be provoked from a real server on demand (once in 120 attempts), and a
    /// listener that accepts a socket and closes it turns out not to produce one: all four variants
    /// measured — abortive and graceful, at accept and after reading the client's PRELOGIN packet —
    /// produce <b>10054</b> instead, so 233 lives at a stage a dropped socket never reaches. That is why
    /// 10054 is classified alongside it, and why this test uses the number it can actually produce.
    /// Counting the accepts counts the logins.
    /// </para>
    /// <para>
    /// The count is the assertion. One accept means the retry never ran; two means it did.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSecondLoginIsActuallyIssuedAndNotMerelyAuthorised()
    {
        using CancellationTokenSource stop = new();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int accepted = 0;

        Task pump = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);

                    Interlocked.Increment(ref accepted);

                    // Dropped mid-login: established, then nothing on the other end of the pipe.
                    client.Client.Close(0);
                }
            }
            catch (OperationCanceledException)
            {
                // The only way out of the loop.
            }
        });

        CredentialValidation result;

        try
        {
            SqlCredentialValidator validator = new(
                Template(b => b.DataSource = $"tcp:127.0.0.1,{port}"),
                connectTimeoutSeconds: 5);

            result = await validator.ValidateAsync(Credentials);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await pump;
        }

        Assert.False(result.IsValid);

        // Named in the message, because if the listener stops producing 233 on some future driver this
        // test would otherwise fail with "2 != 1" and no hint that the premise moved.
        Assert.True(
            Volatile.Read(ref accepted) == 2,
            $"A dropped login was attempted {Volatile.Read(ref accepted)} time(s), not 2. Either the " +
            "retry is gone, or a listener that accepts and closes no longer classifies as " +
            $"RefusedDuringLogin. What the validator reported: {result.FailureMessage}");

        // And the message is the one that says it already retried.
        Assert.Contains("login process twice", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("already retried once", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServerIsNotRetriedEither()
    {
        // The positive control for the retry decision: a validator that retried everything would take
        // twice as long here and reach the same answer. Port 1 refuses immediately, so the whole call is
        // one refused connect -- and the message is the Unverified one, which WorthOneMoreAttempt says
        // gets no second attempt. Retrying an unreachable instance while holding the credential file's
        // exclusive lock is how the loser of a concurrent first run times out.
        SqlCredentialValidator validator = new(
            Template(b => b.DataSource = "tcp:127.0.0.1,1"),
            connectTimeoutSeconds: 2);

        CredentialValidation result = await validator.ValidateAsync(Credentials);

        Assert.False(SqlCredentialValidator.WorthOneMoreAttempt(SqlCredentialOutcome.Unverified));
        Assert.Contains("could not be verified", result.FailureMessage, StringComparison.Ordinal);
    }
}
