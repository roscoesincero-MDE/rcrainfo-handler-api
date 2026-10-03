using System.Globalization;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// Decides whether this suite may touch a server, and hands out contexts when it may.
/// </summary>
/// <remarks>
/// <para>
/// Three controls, each answering a different question, and each one deliberate rather than
/// defensive:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>May these tests run at all?</b> Only when <c>RCRAINFO_INTEGRATION</c> is set. They write, and
///   <b>this database has no hard delete anywhere</b> — every row they write is permanent. That makes
///   running them a decision, not something that happens because a developer typed <c>dotnet test</c>.
///   </description></item>
///   <item><description>
///   <b>Against which server?</b> <c>RCRAINFO_INTEGRATION_SERVER</c>, defaulting to the local default
///   instance. Anything this class cannot identify as the local machine is REFUSED unless
///   <c>RCRAINFO_INTEGRATION_ALLOW_REMOTE</c> is also set. A test corpus that cannot be deleted must
///   not reach UAT because an environment variable was left over from something else.
///   </description></item>
///   <item><description>
///   <b>And if it is unreachable?</b> Then the tests FAIL. This is the one that matters: a suite that
///   skipped on a connection error would go green on a broken workstation, and a guardrail never shown
///   to fail is indistinguishable from one examining nothing. The opt-in and the reachability check are
///   separate for exactly this reason — opting in is a promise that there is a server.
///   </description></item>
/// </list>
/// <para>
/// Every row this suite writes carries the reserved identity in <see cref="TestData"/>, whose
/// <c>ZZ</c> prefix EPA can never issue.
/// </para>
/// </remarks>
public static class IntegrationServer
{
    /// <summary>Set this to any non-empty value to let the suite run.</summary>
    public const string EnableVariable = "RCRAINFO_INTEGRATION";

    /// <summary>Overrides the target instance. Defaults to the local default instance.</summary>
    public const string ServerVariable = "RCRAINFO_INTEGRATION_SERVER";

    /// <summary>Overrides the target database.</summary>
    public const string DatabaseVariable = "RCRAINFO_INTEGRATION_DATABASE";

    /// <summary>Permits a server this class cannot identify as the local machine.</summary>
    public const string AllowRemoteVariable = "RCRAINFO_INTEGRATION_ALLOW_REMOTE";

    /// <summary>
    /// The database these tests expect. Never a scratch copy: every script from 020 onward asserts it
    /// is running in a database of this name and stops if it is not, so there is no supported way to
    /// deploy this schema somewhere disposable.
    /// </summary>
    public const string DefaultDatabase = "RCRAInfo";

    private const string DefaultServer = ".";

    /// <summary>
    /// Names that mean "an instance on this machine". Anything else needs
    /// <see cref="AllowRemoteVariable"/>.
    /// </summary>
    private static readonly string[] LocalNames =
        [".", "(local)", "localhost", "127.0.0.1", "(localdb)"];

    /// <summary>Whether the suite has been enabled.</summary>
    public static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable));

    /// <summary>The target instance.</summary>
    public static string Server =>
        Configured(ServerVariable) ?? DefaultServer;

    /// <summary>The target database.</summary>
    public static string Database =>
        Configured(DatabaseVariable) ?? DefaultDatabase;

    /// <summary>
    /// Why the suite is skipped, or <see langword="null"/> when it may run.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="IntegrationFactAttribute"/> at discovery. A null <c>Skip</c> means the test
    /// runs, which is xunit's own convention for the property.
    /// </remarks>
    public static string? SkipReason =>
        Enabled
            ? null
            : $"Set {EnableVariable}=1 to run the DA5 round-trip tests. They call every procedure " +
              $"against {Describe()} and WRITE rows this database can never hard-delete, so they are " +
              "opt-in. build/guardrails.py --with-database sets it.";

    /// <summary>
    /// Asserts that this suite may write where it is pointed. Fails — never skips — if it may not.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the point, and it is split across two mechanisms. "Not enabled" is a normal
    /// state, and it is handled at discovery by <see cref="IntegrationFactAttribute"/>, which reports a
    /// skip. "Enabled and aimed at a server this suite cannot recognise" is a mistake, and this method
    /// fails on it: reporting that as a skip would hide it behind a green run, and a guardrail never
    /// shown to fail is indistinguishable from one examining nothing.
    /// </remarks>
    public static void Require()
    {
        Assert.True(
            Enabled,
            $"{nameof(Require)} was reached with {EnableVariable} unset. Every test in this suite " +
            $"carries [{nameof(IntegrationFactAttribute)}] or " +
            $"[{nameof(IntegrationTheoryAttribute)}], which skips at discovery, so arriving here " +
            "means a test is missing its attribute and would have written to the database.");

        AssertTargetIsPermitted();
    }

    /// <summary>"server / database", for a message.</summary>
    public static string Describe() => $"{Server} / {Database}";

    /// <summary>A context on the target, with the same options an application would use.</summary>
    /// <returns>A scope the caller owns and must dispose.</returns>
    /// <remarks>
    /// Built through <c>AddRCRAInfoData</c> rather than by constructing the context directly, so these
    /// tests exercise the registration the console app ships — compatibility level 160, the retry
    /// strategy and the two timeouts included. A hand-built context would test a configuration nothing
    /// runs.
    /// </remarks>
    public static IntegrationScope Connect()
    {
        Require();

        ServiceProvider provider = new ServiceCollection()
            .AddRCRAInfoData(options =>
            {
                options.ConnectionString = ConnectionString;

                // Long enough that a slow first call on a cold cache is not reported as a defect,
                // short enough that a genuinely hung call does not hold the run open.
                options.CommandTimeoutSeconds = 120;
                options.BatchCommandTimeoutSeconds = 300;
            })
            .BuildServiceProvider();

        return new IntegrationScope(provider);
    }

    /// <summary>
    /// A connection to the target, for the assertions that must read the log tables or the catalog
    /// directly because no procedure exposes them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    public static async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        Require();

        SqlConnection connection = new(ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>Reads one scalar. A null and a <see cref="DBNull"/> both come back as null.</summary>
    /// <typeparam name="T">The scalar's type.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">The query. Every caller in this project passes a literal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public static async Task<T?> ScalarAsync<T>(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(connection);

#pragma warning disable CA2100 // Literal SQL only; there is no caller-supplied text on this path.
        using SqlCommand command = new(sql, connection);
#pragma warning restore CA2100

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? null
            : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads one string. A null and a <see cref="DBNull"/> both come back as null.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">The query. Every caller in this project passes a literal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Separate from <see cref="ScalarAsync{T}"/> rather than a relaxation of it: that method is
    /// constrained to a value type so that a null result and a null value stay distinguishable through
    /// <c>T?</c>, and widening the constraint to include reference types would quietly lose that. The
    /// callers here use it with <c>STRING_AGG</c>, to read a list of catalog names without a reader.
    /// </remarks>
    public static async Task<string?> TextAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

#pragma warning disable CA2100 // Literal SQL only; there is no caller-supplied text on this path.
        using SqlCommand command = new(sql, connection);
#pragma warning restore CA2100

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>Integrated security, always. This suite reads, holds and logs no password.</summary>
    private static string ConnectionString =>
        new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ApplicationName = "RCRAInfo.Data.Integration.Tests",

            // Fail fast on an unreachable server, rather than after the default 15 seconds on every
            // one of a hundred tests.
            ConnectTimeout = 5,
        }.ConnectionString;

    private static string? Configured(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void AssertTargetIsPermitted()
    {
        string server = Server;

        bool local =
            LocalNames.Any(n => server.StartsWith(n, StringComparison.OrdinalIgnoreCase))
            || server.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        bool allowRemote =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AllowRemoteVariable));

        Assert.True(
            local || allowRemote,
            $"{ServerVariable} is '{server}', which this suite cannot identify as " +
            $"{Environment.MachineName}. These tests write rows no hard delete can remove, so they " +
            "refuse an instance they cannot recognise. If it really is a scratch instance, set " +
            $"{AllowRemoteVariable}=1.");
    }
}

/// <summary>A provider and the context resolved from it, disposed together.</summary>
/// <remarks>
/// The context's lifetime belongs to the provider, so disposing the context alone would leave the
/// provider holding it. One type, one <c>await using</c>, and no test has to remember the order.
/// </remarks>
public sealed class IntegrationScope : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    internal IntegrationScope(ServiceProvider provider)
    {
        this.provider = provider;
        Context = provider.GetRequiredService<RCRAInfoContext>();
    }

    /// <summary>The context.</summary>
    public RCRAInfoContext Context { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await provider.DisposeAsync().ConfigureAwait(false);
}
