namespace RCRAInfo.Data;

/// <summary>
/// Filters for <c>dbo.uspGetHandlerSourcePage</c> (script 503) — the handler grid.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than twelve method arguments, because twelve positional arguments of which nine
/// are nullable is a call site nobody can read and one nobody can safely reorder.
/// </para>
/// <para>
/// <see cref="SortBy"/> is a plain string and is <b>not</b> validated here, deliberately. The
/// procedure validates it against a fixed whitelist and refuses an unknown value with a message
/// naming the permitted set; duplicating that list in C# would create a second source of truth for
/// the one rule where disagreement is a security question rather than a cosmetic one. The same
/// reasoning applies to the closed-set code filters, which
/// <c>build/check_closed_set_filters.py</c> holds against the <c>CHECK</c> constraints they copy.
/// </para>
/// <para>
/// <b>Why <see cref="SortBy"/> carries a default here rather than relying on the procedure's.</b> The
/// four read procedures each declare a visible default — <c>@SortBy NVARCHAR (50) = N'HandlerId'</c>
/// and its three counterparts — and each refuses a value outside its whitelist rather than falling
/// through to one, because a fall-through and a typo look identical to a caller. Binding the
/// parameter explicitly means the procedure's default can never fire: an unset
/// <see cref="SortBy"/> arrived as <c>NULL</c>, which the whitelist correctly refused, so the read
/// threw for a caller who had simply not expressed a preference. Repeating the default here is the
/// smaller of two evils — it duplicates a string, which
/// <c>build/check_sort_defaults.py</c> holds against the procedures' declared defaults, whereas the
/// alternative of omitting the parameter from the <c>EXEC</c> would make "unset" and "explicitly
/// null" indistinguishable for every other parameter too.
/// </para>
/// </remarks>
public sealed record HandlerSourcePageQuery
{
    /// <summary>The procedure's own default sort column. Held against script 503 by the build.</summary>
    public const string DefaultSortBy = "HandlerId";

    /// <summary>Rows to skip. Zero for the first page.</summary>
    public int Skip { get; init; }

    /// <summary>Page size.</summary>
    public int Take { get; init; } = 25;

    /// <summary>Which column to order by. Defaults to <see cref="DefaultSortBy"/>.</summary>
    public string SortBy { get; init; } = DefaultSortBy;

    /// <summary>Whether to order descending.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Restrict to one handler.</summary>
    public string? HandlerId { get; init; }

    /// <summary>Restrict to one state. <c>MD</c> is the only value in scope (G2).</summary>
    public string? ActivityLocation { get; init; }

    /// <summary>Restrict to one source-type code.</summary>
    public string? SourceType { get; init; }

    /// <summary>Restrict to one federal generator category code.</summary>
    public string? FederalGeneratorCategory { get; init; }

    /// <summary>Earliest EPA received date, inclusive.</summary>
    public DateOnly? ReceivedFromDate { get; init; }

    /// <summary>Latest EPA received date, inclusive.</summary>
    public DateOnly? ReceivedToDate { get; init; }

    /// <summary>Earliest EPA update date, inclusive.</summary>
    public DateOnly? SrcUpdatedFromDate { get; init; }

    /// <summary>Latest EPA update date, inclusive.</summary>
    public DateOnly? SrcUpdatedToDate { get; init; }
}

/// <summary>
/// Filters for <c>dbo.uspGetHandlerSourceHistoryPage</c> (script 504) — every version of one
/// handler.
/// </summary>
public sealed record HandlerSourceHistoryQuery
{
    /// <summary>The procedure's own default sort column. Held against script 504 by the build.</summary>
    public const string DefaultSortBy = "ReceivedDate";

    /// <summary>
    /// The handler whose versions to list. Required by the procedure: without it the read would page
    /// the whole table under a heading that says it is one handler's history.
    /// </summary>
    public required string HandlerId { get; init; }

    /// <summary>Rows to skip.</summary>
    public int Skip { get; init; }

    /// <summary>Page size.</summary>
    public int Take { get; init; } = 25;

    /// <summary>Which column to order by. Defaults to <see cref="DefaultSortBy"/>.</summary>
    public string SortBy { get; init; } = DefaultSortBy;

    /// <summary>Whether to order descending.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Restrict to one source-type code.</summary>
    public string? SourceType { get; init; }
}

/// <summary>
/// Filters for <c>logs.uspGetLoadRunPage</c> (script 500) — the load-run history.
/// </summary>
public sealed record LoadRunPageQuery
{
    /// <summary>The procedure's own default sort column. Held against script 500 by the build.</summary>
    public const string DefaultSortBy = "StartedDateUtc";

    /// <summary>Rows to skip.</summary>
    public int Skip { get; init; }

    /// <summary>Page size.</summary>
    public int Take { get; init; } = 25;

    /// <summary>Which column to order by. Defaults to <see cref="DefaultSortBy"/>.</summary>
    public string SortBy { get; init; } = DefaultSortBy;

    /// <summary>Whether to order descending.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Restrict to one run.</summary>
    public int? LoadRunId { get; init; }

    /// <summary>Restrict to one run mode.</summary>
    public string? RunMode { get; init; }

    /// <summary>Restrict to one state.</summary>
    public string? ActivityLocation { get; init; }

    /// <summary>Restrict to one run status.</summary>
    public string? Status { get; init; }

    /// <summary>Earliest start time, inclusive, in UTC.</summary>
    public DateTimeOffset? StartedFromUtc { get; init; }

    /// <summary>Latest start time, inclusive, in UTC.</summary>
    public DateTimeOffset? StartedToUtc { get; init; }

    /// <summary>
    /// Include soft-deleted runs. Default false, which is the rule every read path in this database
    /// follows; the flag exists because retention soft-deletes old runs and an operator
    /// investigating a gap needs to see that they existed.
    /// </summary>
    public bool IncludeDeleted { get; init; }
}

/// <summary>
/// Filters for <c>logs.uspGetHandlerLoadStatusPage</c> (script 501) — per-handler load status,
/// which is the grid whose job is to show failures.
/// </summary>
public sealed record HandlerLoadStatusPageQuery
{
    /// <summary>The procedure's own default sort column. Held against script 501 by the build.</summary>
    public const string DefaultSortBy = "FirstSeenDateUtc";

    /// <summary>Rows to skip.</summary>
    public int Skip { get; init; }

    /// <summary>Page size.</summary>
    public int Take { get; init; } = 25;

    /// <summary>Which column to order by. Defaults to <see cref="DefaultSortBy"/>.</summary>
    public string SortBy { get; init; } = DefaultSortBy;

    /// <summary>Whether to order descending.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Restrict to one run.</summary>
    public int? LoadRunId { get; init; }

    /// <summary>Restrict to one handler.</summary>
    public string? HandlerId { get; init; }

    /// <summary>Restrict to one state.</summary>
    public string? ActivityLocation { get; init; }

    /// <summary>Restrict to one source-type code.</summary>
    public string? SourceType { get; init; }

    /// <summary>Restrict to one status.</summary>
    public string? Status { get; init; }

    /// <summary>Restrict to one outcome.</summary>
    public string? Outcome { get; init; }

    /// <summary>Earliest first-seen time, inclusive, in UTC.</summary>
    public DateTimeOffset? FirstSeenFromUtc { get; init; }

    /// <summary>Latest first-seen time, inclusive, in UTC.</summary>
    public DateTimeOffset? FirstSeenToUtc { get; init; }

    /// <summary>Show only the latest attempt per handler rather than every one.</summary>
    public bool LatestOnly { get; init; }

    /// <summary>Include soft-deleted status rows.</summary>
    public bool IncludeDeleted { get; init; }
}
