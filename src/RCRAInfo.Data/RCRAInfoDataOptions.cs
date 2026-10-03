using System.ComponentModel.DataAnnotations;

namespace RCRAInfo.Data;

/// <summary>
/// How <see cref="RCRAInfoContext"/> connects and how long it waits. Bound from configuration by
/// <c>AddRCRAInfoData</c>.
/// </summary>
/// <remarks>
/// <para>
/// These live in RCRAInfo.Data rather than RCRAInfo.Core deliberately. Core is the bottom of the
/// dependency graph and nothing in it knows that SQL Server exists; a connection string and a
/// command timeout are facts about this project's data access, not about the domain.
/// </para>
/// </remarks>
public sealed class RCRAInfoDataOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "RCRAInfoData";

    /// <summary>
    /// The SQL Server connection string. Supplied by the host, which is the component that knows
    /// whether the password arrived in plaintext and needs encrypting in place (AR4). Nothing in
    /// this project decrypts anything.
    /// </summary>
    [Required (AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Seconds allowed for an ordinary call: a paged read, a watermark write, a run start or
    /// completion. Above ADO.NET's 30-second default because a paged read over 400,000 rows on a
    /// cold cache is not a fast query, and a timeout there presents to an operator as a database
    /// fault rather than as the configuration setting it is.
    /// </summary>
    [Range (1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Seconds allowed for a set-based batch: the merge, the reconciliation, the lookup refresh
    /// and the soft-delete cascade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the setting the Plan singles out, and the reason is worth keeping next to the
    /// number. Those four procedures each open their own transaction and write across a parent and
    /// eighteen child collections; the 30-second default will not survive a real batch. When it
    /// expires, ADO.NET aborts the command and the procedure's transaction rolls back — so the
    /// symptom is a failed load run with a timeout message, and the cause is this line.
    /// </para>
    /// </remarks>
    [Range (1, 7200)]
    public int BatchCommandTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// How many times EF Core retries a transient SQL failure before giving up. Zero disables
    /// retry entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retry is safe here only because of a decision made in the database, and it would not be
    /// safe without it. EF Core's execution strategy retries the whole operation, so a call that
    /// committed on the server and then lost the connection is retried against a database that has
    /// already applied it. Every procedure this project calls is idempotent by construction —
    /// script 520's header spells out why AttemptCount is SET from the payload rather than
    /// incremented, precisely so that a retried call cannot claim an attempt that never happened —
    /// and <c>logs.uspStartLoadRun</c> refuses a second concurrent run unless
    /// <c>@AllowConcurrent</c> says otherwise, so a retry cannot quietly produce two runs.
    /// </para>
    /// <para>
    /// The other half of the rule is [R12]: nothing in C# opens a transaction. EF Core throws when
    /// a retrying strategy meets <c>BeginTransaction</c>, and it throws on the first retry — which
    /// is to say in UAT, not in development.
    /// </para>
    /// </remarks>
    [Range (0, 20)]
    public int MaxRetryCount { get; set; } = 6;

    /// <summary>Longest delay between retries, in seconds.</summary>
    [Range (1, 300)]
    public int MaxRetryDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Largest number of elements this project will send in one JSON payload. A guard against
    /// building a multi-hundred-megabyte <c>NVARCHAR (MAX)</c> parameter in memory, which fails as
    /// an <see cref="OutOfMemoryException"/> in the loader rather than as anything a database
    /// message would explain.
    /// </summary>
    [Range (1, 100000)]
    public int MaxPayloadElements { get; set; } = 500;
}
