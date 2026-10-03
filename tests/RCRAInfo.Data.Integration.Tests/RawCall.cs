using System.Data;

using Microsoft.Data.SqlClient;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// Calls a procedure without going through <see cref="RCRAInfoContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because some of the procedures' refusals are unreachable from the data layer.</b>
/// <c>RCRAInfo.Data</c> serialises every payload with <c>PayloadJson</c> and then calls
/// <c>PayloadJson.AssertJsonArray</c>, so malformed JSON throws in C# before a command is ever built.
/// That is correct — the cheapest place to reject a bad payload is the caller — but it also means the
/// procedure's own <c>ISJSON</c> gate is never exercised through the ordinary path, and a gate nothing
/// exercises is indistinguishable from one that is not there. The loader is not the only thing that
/// will ever call these procedures: they are the granted surface for two logins, and a hand-typed
/// <c>EXEC</c> in a support session reaches them with no C# in front of it at all.
/// </para>
/// <para>
/// <c>CommandType.StoredProcedure</c> and real <see cref="SqlParameter"/>s, never a composed
/// <c>EXEC</c> string. The values these tests send are deliberately malformed, and building a statement
/// out of them would make the test itself the injection it is checking for.
/// </para>
/// </remarks>
internal static class RawCall
{
    /// <summary>Calls a procedure on its own connection and expects it to succeed.</summary>
    /// <param name="procedure">The two-part procedure name.</param>
    /// <param name="bind">Adds the parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task ExecuteAsync(
        string procedure,
        Action<SqlParameterCollection> bind,
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection =
            await IntegrationServer.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, procedure, bind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls a procedure on a caller-owned connection and expects it to succeed.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="procedure">The two-part procedure name.</param>
    /// <param name="bind">Adds the parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The connection is a parameter so that a test which has already changed a session setting — a
    /// <c>SET LOCK_TIMEOUT</c>, say — can call the procedure under it. Session settings do not travel
    /// with a pooled connection.
    /// </remarks>
    public static async Task ExecuteAsync(
        SqlConnection connection,
        string procedure,
        Action<SqlParameterCollection> bind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bind);

        using SqlCommand command = new(procedure, connection) { CommandType = CommandType.StoredProcedure };
        bind(command.Parameters);

        // ExecuteNonQuery rather than ExecuteReader: several of these procedures return a result set on
        // the success path and the callers here never read it, but a refusal has to surface either way.
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls a procedure and asserts that it refused.</summary>
    /// <param name="procedure">The two-part procedure name.</param>
    /// <param name="bind">Adds the parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exception, for the caller to assert on.</returns>
    public static async Task<SqlException> ExpectFailureAsync(
        string procedure,
        Action<SqlParameterCollection> bind,
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection =
            await IntegrationServer.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ExpectFailureAsync(connection, procedure, bind, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Calls a procedure on a caller-owned connection and asserts that it refused.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="procedure">The two-part procedure name.</param>
    /// <param name="bind">Adds the parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exception, for the caller to assert on.</returns>
    public static async Task<SqlException> ExpectFailureAsync(
        SqlConnection connection,
        string procedure,
        Action<SqlParameterCollection> bind,
        CancellationToken cancellationToken = default) =>
        await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync(connection, procedure, bind, cancellationToken)).ConfigureAwait(false);

    /// <summary>An <c>NVARCHAR (MAX)</c> payload parameter, bound the way the data layer binds one.</summary>
    /// <param name="name">The parameter name, without the <c>@</c>.</param>
    /// <param name="json">The value, malformed or not.</param>
    /// <returns>The parameter.</returns>
    /// <remarks>
    /// <c>Size = -1</c> matters. Left at its default the driver infers the width from the value, which
    /// for a short string means <c>NVARCHAR (n)</c> — and a payload parameter declared narrower than
    /// <c>MAX</c> is a different call from the one the loader makes.
    /// </remarks>
    public static SqlParameter Payload(string name, string? json) =>
        new(name, SqlDbType.NVarChar, -1) { Value = json ?? (object)DBNull.Value };

    /// <summary>An <c>NVARCHAR (n)</c> parameter.</summary>
    /// <param name="name">The parameter name, without the <c>@</c>.</param>
    /// <param name="value">The value.</param>
    /// <param name="size">The declared width, from <c>sys.parameters</c>.</param>
    /// <returns>The parameter.</returns>
    public static SqlParameter Text(string name, string? value, int size) =>
        new(name, SqlDbType.NVarChar, size) { Value = value ?? (object)DBNull.Value };

    /// <summary>An <c>INT</c> parameter.</summary>
    /// <param name="name">The parameter name, without the <c>@</c>.</param>
    /// <param name="value">The value.</param>
    /// <returns>The parameter.</returns>
    public static SqlParameter Int(string name, int? value) =>
        new(name, SqlDbType.Int) { Value = value ?? (object)DBNull.Value };

    /// <summary>A <c>DATE</c> parameter.</summary>
    /// <param name="name">The parameter name, without the <c>@</c>.</param>
    /// <param name="value">The value.</param>
    /// <returns>The parameter.</returns>
    /// <remarks>
    /// <see cref="SqlDbType.Date"/> and a <see cref="DateOnly"/>, never <see cref="SqlDbType.DateTime"/>
    /// and a <see cref="DateTime"/> at midnight. The columns behind these parameters are <c>date</c>, and
    /// binding a wider type is how a value acquires a time component and a time zone it was never meant
    /// to have.
    /// </remarks>
    public static SqlParameter Date(string name, DateOnly? value) =>
        new(name, SqlDbType.Date) { Value = value ?? (object)DBNull.Value };
}
