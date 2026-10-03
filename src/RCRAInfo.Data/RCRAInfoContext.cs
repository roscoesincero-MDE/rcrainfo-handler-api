using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using RCRAInfo.Data.Results;

namespace RCRAInfo.Data;

/// <summary>
/// The whole of this project's data access: a connection, a parameter binder, and a result
/// materialiser. One method per stored procedure. <b>Nothing else in the solution writes SQL.</b>
/// </summary>
/// <remarks>
/// <para>
/// EF Core is present for one purpose (Revision 6): calling set-based stored procedures. There is no
/// change tracker write path, no migrations, and no model-first mapping — <c>BannedSymbols.txt</c>
/// turns each of those into a build error rather than a code review comment. What is left is thin on
/// purpose, and the four rules that keep it thin are worth stating where they are implemented.
/// </para>
/// <para>
/// <b>1. No transaction is opened here. Ever.</b> [R12]. Every procedure in this database opens its
/// own, and T-SQL has no nested rollback: the procedure's <c>CATCH</c> rolls back to
/// <c>@@TRANCOUNT = 0</c>, discarding an outer transaction this code would still believe it owned,
/// which then fails on commit with a <i>different</i> error than the one that actually happened. A
/// genuine multi-procedure unit of work needs one procedure wrapping the others, not a transaction
/// here. It is also what makes <see cref="RCRAInfoDataOptions.MaxRetryCount"/> usable at all, since
/// EF Core throws when a retrying execution strategy meets an explicit transaction — on the first
/// retry, which is to say in UAT rather than in development.
/// </para>
/// <para>
/// <b>2. No exception is caught here.</b> Every procedure ends its <c>CATCH</c> with a bare
/// <c>THROW</c>, which preserves the original error number, and <see cref="SqlErrorNumbers"/> exists
/// so a caller can tell a deadlock from a constraint violation. Translating or wrapping the failure
/// in this layer would destroy the only information that distinguishes "retry this" from "this will
/// fail identically forever". The error reaches the caller whether or not it was logged:
/// <c>logs.ExecutionLog</c> is a record, never the notification path.
/// </para>
/// <para>
/// <b>3. Every parameter is bound with an explicit type and an explicit width.</b> Not for injection
/// safety — the SQL text here contains no interpolated data at all — but for the plan cache. An
/// inferred <c>NVARCHAR</c> size is the length of the value, so a search for "acme" and a search for
/// "acme corp" compile as different statements and each takes its own plan. The widths below match
/// the procedures' declarations.
/// </para>
/// <para>
/// <b>4. Every payload goes through <see cref="PayloadJson"/>.</b> One serializer configuration for
/// the whole solution, because <c>OPENJSON</c> matches property names case-sensitively and a
/// camelCase-versus-PascalCase difference silently blanks every column in the batch.
/// </para>
/// </remarks>
public sealed partial class RCRAInfoContext : DbContext
{
    private readonly RCRAInfoDataOptions options;

    /// <summary>Creates the context.</summary>
    /// <param name="contextOptions">EF Core's own options, configured by
    /// <c>AddRCRAInfoData</c>.</param>
    /// <param name="dataOptions">This project's options: timeouts and payload limits.</param>
    public RCRAInfoContext (
        DbContextOptions<RCRAInfoContext> contextOptions,
        IOptions<RCRAInfoDataOptions> dataOptions)
        : base (contextOptions)
    {
        ArgumentNullException.ThrowIfNull (dataOptions);
        this.options = dataOptions.Value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Every instant in these result shapes is a <see cref="DateTimeOffset"/> and every SQL column
    /// behind one is <c>DATETIME2</c>, which has no offset. This converter is what closes that gap in
    /// the reading direction, and it is the mirror of what
    /// <see cref="PayloadJson"/> and <c>DateTime2</c> do in the writing direction — the same
    /// normalisation, stated once per direction rather than once per column.
    /// </para>
    /// <para>
    /// Without it the alternative is a plain <see cref="DateTime"/>, which is what a mechanical
    /// mapping from <c>DATETIME2</c> produces and what this project deliberately does not use. A
    /// <c>DATETIME2</c> read into a <see cref="DateTime"/> arrives as
    /// <see cref="DateTimeKind.Unspecified"/>, so the monitoring app calling
    /// <c>ToLocalTime ()</c> on a column named <c>StartedDateUtc</c> would shift a value that was
    /// already UTC and display a run as having started at the wrong time — with nothing failing and
    /// no way to notice but arithmetic. The type carrying its own offset removes the question.
    /// </para>
    /// <para>
    /// Applied as a convention rather than per property because there are twenty-three of them across
    /// ten shapes, and the twenty-fourth would be the one somebody forgot.
    /// </para>
    /// </remarks>
    /// <param name="configurationBuilder">EF Core's convention configuration.</param>
    protected override void ConfigureConventions (ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull (configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset> ()
            .HaveConversion<UtcDateTimeOffsetConverter> ();
    }

    /// <inheritdoc/>
    protected override void OnModelCreating (ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull (modelBuilder);

        // Every result shape, registered so Set<T>().FromSqlRaw can materialise it. They are
        // [Keyless] and are reached only through Set<T> inside this class -- there is deliberately no
        // public DbSet property, because one would let a caller write LINQ against a table that has
        // no DbSet mapping and get an exception naming an object that does not exist.
        modelBuilder.Entity<HandlerSourceGridRow> ();
        modelBuilder.Entity<HandlerSourceHistoryRow> ();
        modelBuilder.Entity<HandlerSourceSearchRow> ();
        modelBuilder.Entity<HandlerSourceDetail> ();
        modelBuilder.Entity<LoadRunRow> ();
        modelBuilder.Entity<HandlerLoadStatusRow> ();
        modelBuilder.Entity<HandlerLoadResumeRow> ();
        modelBuilder.Entity<LoadRunSummary> ();
        modelBuilder.Entity<LoadWatermark> ();
        modelBuilder.Entity<MergeOutcomeRow> ();

        // The audit columns are the one place where this database's naming and C#'s disagree: the
        // convention is PascalCase for every object and field EXCEPT the standard audit columns,
        // which are auditCreatedBy, auditCreatedDateUtc, auditModifiedBy and auditModifiedDateUtc.
        // A C# property named auditModifiedDateUtc would be a style violation the analyzers reject,
        // so the properties are PascalCase and the column name is set here.
        //
        // A rule rather than thirty-two HasColumnName calls, and mapped explicitly rather than left
        // to case-insensitive column matching, so that the mapping does not depend on a driver
        // behaviour nobody documented. RCRAInfoContextTests asserts that no property outside this
        // pattern starts with "Audit", which is what stops the rule from quietly catching something
        // it was not written for.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes ())
        {
            foreach (var property in entityType.GetProperties ())
            {
                if (property.Name.StartsWith ("Audit", StringComparison.Ordinal))
                {
                    property.SetColumnName (
                        string.Concat ("audit", property.Name.AsSpan ("Audit".Length)));
                }
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // The two helpers every method in this class goes through.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the <c>EXEC</c> statement for a procedure from the parameters being bound to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place in the solution that composes SQL text, and it composes only two kinds
    /// of token: a procedure name and a parameter name, both of which are compile-time string
    /// literals in this file. <b>No value, and nothing derived from a value, is ever concatenated
    /// into the statement.</b> It is the C# counterpart of the <c>@SortBy</c> rule — a whitelist
    /// validated before interpolation, or no interpolation at all, and this takes the second option.
    /// </para>
    /// <para>
    /// Naming each <see cref="SqlParameter"/> exactly as the procedure names it is what makes
    /// <c>@Skip = @Skip</c> read oddly and be right: the left side is the procedure's parameter, the
    /// right side is the one <c>sp_executesql</c> declares. It also means a mismatch is a build-time
    /// typo in one visible place rather than an error 201 at run time — and a 201 is raised
    /// <i>before</i> the procedure body runs, so it is the one refusal in this database that writes
    /// no log row at all.
    /// </para>
    /// </remarks>
    /// <param name="procedure">Schema-qualified procedure name; a literal.</param>
    /// <param name="parameters">The parameters, named as the procedure names them.</param>
    /// <returns>The <c>EXEC</c> statement.</returns>
    private static string Exec (string procedure, SqlParameter[] parameters)
    {
        var assignments = new string[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            SqlParameter parameter = parameters[i];
            string name = parameter.ParameterName;

            // OUTPUT has to appear in the statement TEXT. Setting ParameterDirection.Output on the
            // SqlParameter is necessary but not sufficient: it tells the driver to declare the
            // sp_executesql variable as OUTPUT and to read a value back afterwards, while the
            // keyword here is what tells the procedure call to assign to that variable. Omit it and
            // the call succeeds, the procedure does its work, and the parameter comes back with its
            // value untouched -- which is why this was invisible until something read one.
            // ReadInt turns that into an exception rather than a zero, and that is how it surfaced.
            assignments[i] = parameter.Direction is ParameterDirection.Output
                or ParameterDirection.InputOutput
                ? string.Concat ("@", name, " = @", name, " OUTPUT")
                : string.Concat ("@", name, " = @", name);
        }

        return string.Concat ("EXEC ", procedure, " ", string.Join (", ", assignments));
    }

    /// <summary>Runs a procedure that returns rows, and materialises them.</summary>
    /// <typeparam name="T">A registered keyless result type.</typeparam>
    /// <param name="procedure">Schema-qualified procedure name.</param>
    /// <param name="parameters">Bound parameters.</param>
    /// <param name="timeoutSeconds">Command timeout for this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, in the order the procedure returned them.</returns>
    private async Task<List<T>> QueryAsync<T> (
        string procedure,
        SqlParameter[] parameters,
        int timeoutSeconds,
        CancellationToken cancellationToken)
        where T : class
    {
        // Set per call rather than once in configuration: a paged read and a 500-element merge want
        // very different limits, and one context instance serves both.
        this.Database.SetCommandTimeout (timeoutSeconds);

        // No composition after FromSqlRaw -- no Where, no OrderBy, no Skip/Take. Every one of these
        // procedures pages and orders internally, and composing on top would wrap the EXEC in a
        // subquery, which SQL Server does not allow for a procedure call. It would also re-sort a
        // page that was already the right page, which is the defect that makes paging across a
        // non-unique sort key return the same row twice.
        return await this.Set<T> ()
            .FromSqlRaw (Exec (procedure, parameters), parameters)
            .ToListAsync (cancellationToken)
            .ConfigureAwait (false);
    }

    /// <summary>Runs a procedure that returns no rows.</summary>
    /// <param name="procedure">Schema-qualified procedure name.</param>
    /// <param name="parameters">Bound parameters, which may include output parameters.</param>
    /// <param name="timeoutSeconds">Command timeout for this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Correct for every write in this project because none of them returns a result set — verified
    /// against <c>sys.dm_exec_describe_first_result_set</c> for all seven, and it matters: this call
    /// discards any rows a procedure returned, and for a procedure with both rows and output
    /// parameters the outputs are not populated until the rows have been consumed. So a procedure
    /// that grew a result set would start reporting zeroes from its output parameters here.
    /// <c>build/check_result_shapes.py</c> fails if one does.
    /// </remarks>
    private async Task ExecuteAsync (
        string procedure,
        SqlParameter[] parameters,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        this.Database.SetCommandTimeout (timeoutSeconds);

        await this.Database
            .ExecuteSqlRawAsync (Exec (procedure, parameters), parameters, cancellationToken)
            .ConfigureAwait (false);
    }

    /// <summary>Wraps rows and their repeated total into a <see cref="Page{T}"/>.</summary>
    private static Page<T> ToPage<T> (
        List<T> rows, Func<T, int?> totalRows, int skip, int take) =>
        rows.Count == 0
            ? Page<T>.Empty (skip, take)
            : new Page<T> (rows, totalRows (rows[0]) ?? 0, skip, take);

    // -------------------------------------------------------------------------------------------
    // Parameter binding. Explicit type, explicit width, explicit null.
    // -------------------------------------------------------------------------------------------

    /// <summary>Binds an <c>NVARCHAR (size)</c> parameter.</summary>
    private static SqlParameter Text (string name, string? value, int size) =>
        new (name, SqlDbType.NVarChar, size) { Value = value ?? (object) DBNull.Value };

    /// <summary>Binds an <c>NVARCHAR (MAX)</c> parameter — a JSON payload.</summary>
    /// <remarks>
    /// Size -1 is what makes it <c>MAX</c>. Left to inference, a payload under 4000 characters would
    /// be sent as <c>nvarchar(&lt;its length&gt;)</c>, so the same procedure would compile a fresh
    /// plan for every distinct batch size — and a batch over 4000 characters would get a different
    /// plan again.
    /// </remarks>
    private static SqlParameter Payload (string name, string json) =>
        new (name, SqlDbType.NVarChar, -1) { Value = json };

    /// <summary>Binds an <c>INT</c> parameter.</summary>
    private static SqlParameter Int (string name, int? value) =>
        new (name, SqlDbType.Int) { Value = value.HasValue ? value.Value : DBNull.Value };

    /// <summary>Binds a <c>BIGINT</c> parameter.</summary>
    private static SqlParameter BigInt (string name, long? value) =>
        new (name, SqlDbType.BigInt) { Value = value.HasValue ? value.Value : DBNull.Value };

    /// <summary>Binds a <c>BIT</c> parameter.</summary>
    private static SqlParameter Bit (string name, bool? value) =>
        new (name, SqlDbType.Bit) { Value = value.HasValue ? value.Value : DBNull.Value };

    /// <summary>Binds a <c>DATE</c> parameter.</summary>
    /// <remarks>
    /// The <see cref="DateOnly"/> is converted rather than passed through. The driver does support
    /// <see cref="DateOnly"/> directly, but <see cref="SqlDbType.Date"/> with a
    /// <see cref="DateTime"/> has worked on every version of it, carries no time component into the
    /// engine, and removes a driver-version dependency from a code path that runs unattended.
    /// </remarks>
    private static SqlParameter Date (string name, DateOnly? value) =>
        new (name, SqlDbType.Date)
        {
            Value = value.HasValue
                ? value.Value.ToDateTime (TimeOnly.MinValue)
                : DBNull.Value,
        };

    /// <summary>Binds a <c>DATETIME2</c> parameter, normalised to UTC.</summary>
    /// <remarks>
    /// <para>
    /// <c>DATETIME2</c> has no offset, and it does not <i>apply</i> one it is given — it discards it.
    /// So a <see cref="DateTimeOffset"/> at <c>-05:00</c> bound without conversion would land in a
    /// column named <c>…DateUtc</c> as a local wall-clock reading five hours wrong, with nothing
    /// failing.
    /// </para>
    /// <para>
    /// This is deliberately the same normalisation
    /// <see cref="PayloadJson"/> applies to a date inside a payload. The two paths must agree, or
    /// the same instant sent as a parameter and sent as a payload element would be stored as two
    /// different times.
    /// </para>
    /// </remarks>
    private static SqlParameter DateTime2 (string name, DateTimeOffset? value) =>
        new (name, SqlDbType.DateTime2)
        {
            Value = value.HasValue ? value.Value.UtcDateTime : DBNull.Value,
        };

    /// <summary>Declares an <c>INT</c> output parameter.</summary>
    private static SqlParameter OutInt (string name) =>
        new (name, SqlDbType.Int) { Direction = ParameterDirection.Output };

    /// <summary>
    /// Maps a <see cref="DateTimeOffset"/> property onto a <c>DATETIME2</c> column, in both
    /// directions, treating the stored value as UTC.
    /// </summary>
    /// <remarks>
    /// Writing takes <see cref="DateTimeOffset.UtcDateTime"/>, so an offset is <i>applied</i> here
    /// rather than left for SQL Server to discard. Reading pairs the stored value with a zero offset,
    /// which is a statement of what the column means rather than a guess: every one of these columns
    /// is named <c>…DateUtc</c> and every write path in this project normalises before sending.
    /// </remarks>
    private sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTime>
    {
        public UtcDateTimeOffsetConverter ()
            : base (
                value => value.UtcDateTime,
                value => new DateTimeOffset (value, TimeSpan.Zero))
        {
        }
    }

    /// <summary>Reads an output parameter that the procedure is contracted to set.</summary>
    /// <remarks>
    /// Throws rather than defaulting to zero. Every output parameter in this database is a count, and
    /// a count silently read as zero would report "nothing was affected" for a batch that wrote
    /// thousands of rows — which the monitoring web app would then display as a run that did nothing.
    /// An unset output parameter is a defect in the procedure, and this is where it becomes visible.
    /// </remarks>
    /// <param name="parameter">The output parameter, after the call has returned.</param>
    /// <returns>The value the procedure set.</returns>
    private static int ReadInt (SqlParameter parameter) =>
        parameter.Value is int value
            ? value
            : throw new InvalidOperationException (
                string.Format (
                    CultureInfo.InvariantCulture,
                    "The procedure returned no value for its output parameter @{0}. Every output " +
                    "parameter in this database is a count that the procedure sets before it " +
                    "returns, so this is a defect in the procedure rather than a data condition; " +
                    "reading it as zero would report that nothing was affected.",
                    parameter.ParameterName));
}
