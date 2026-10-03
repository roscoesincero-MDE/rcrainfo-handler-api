using Microsoft.Data.SqlClient;
using RCRAInfo.Data.Results;

namespace RCRAInfo.Data;

/// <content>
/// The read path: nine procedures, five of them paged.
/// </content>
/// <remarks>
/// <para>
/// Each parameter is bound at the width the procedure declares it, read from
/// <c>sys.parameters</c> rather than assumed — which is why <see cref="GetHandlerSourceHistoryPageAsync"/>
/// binds <c>@HandlerId</c> at 20 characters and the other two bind it at 12. A parameter bound
/// narrower than the procedure declares truncates in the driver, before the procedure sees it, so a
/// single shared constant would be the kind of correctness bug that only appears for long values.
/// (The three procedures disagreeing is itself untidy — <c>dbo.HandlerSource.HandlerId</c> is 12 — but
/// widening a parameter is not a defect and narrowing one here would be.)
/// </para>
/// <para>
/// No read filters <c>IsDeleted</c> from this side. Every procedure does it internally and the two
/// <c>logs</c> reads expose <c>IncludeDeleted</c> as an explicit opt-in; a filter added here would be
/// a second, weaker copy of the rule, applied after the rows had already crossed the wire.
/// </para>
/// </remarks>
public sealed partial class RCRAInfoContext
{
    /// <summary>Reads one page of the handler grid. Script 503.</summary>
    /// <param name="query">Paging, sort and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, with the total row count for the filter.</returns>
    /// <remarks>
    /// The projection carries no contact columns and no <c>SrcUpdatedBy</c> — they are PII and an EPA
    /// user's name respectively, and are reachable only through
    /// <see cref="GetHandlerSourceDetailAsync"/>. That is the procedure's decision, recorded here
    /// because a caller who needs a contact name will otherwise assume this method is simply missing
    /// a column.
    /// </remarks>
    public async Task<Page<HandlerSourceGridRow>> GetHandlerSourcePageAsync (
        HandlerSourcePageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (query);

        SqlParameter[] parameters =
        [
            Int ("Skip", query.Skip),
            Int ("Take", query.Take),
            Text ("SortBy", query.SortBy, 50),
            Bit ("SortDescending", query.SortDescending),
            Text ("HandlerId", query.HandlerId, 12),
            Text ("ActivityLocation", query.ActivityLocation, 2),
            Text ("SourceType", query.SourceType, 1),
            Text ("FederalGeneratorCategory", query.FederalGeneratorCategory, 1),
            Date ("ReceivedFromDate", query.ReceivedFromDate),
            Date ("ReceivedToDate", query.ReceivedToDate),
            Date ("SrcUpdatedFromDate", query.SrcUpdatedFromDate),
            Date ("SrcUpdatedToDate", query.SrcUpdatedToDate),
        ];

        var rows = await this.QueryAsync<HandlerSourceGridRow> (
            "dbo.uspGetHandlerSourcePage",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return ToPage (rows, r => r.TotalRows, query.Skip, query.Take);
    }

    /// <summary>Reads one page of a single handler's version history. Script 504.</summary>
    /// <param name="query">The handler, plus paging and sort.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, with the total number of versions.</returns>
    public async Task<Page<HandlerSourceHistoryRow>> GetHandlerSourceHistoryPageAsync (
        HandlerSourceHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (query);

        SqlParameter[] parameters =
        [
            Text ("HandlerId", query.HandlerId, 20),
            Int ("Skip", query.Skip),
            Int ("Take", query.Take),
            Text ("SortBy", query.SortBy, 50),
            Bit ("SortDescending", query.SortDescending),
            Text ("SourceType", query.SourceType, 1),
        ];

        var rows = await this.QueryAsync<HandlerSourceHistoryRow> (
            "dbo.uspGetHandlerSourceHistoryPage",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return ToPage (rows, r => r.TotalRows, query.Skip, query.Take);
    }

    /// <summary>Searches handlers by identifier or name. Script 506.</summary>
    /// <param name="searchTerm">Whatever the user typed.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, with the total number of matches.</returns>
    /// <remarks>
    /// <para>
    /// <b>The term must never be logged.</b> A search term is whatever a user typed and this database
    /// holds regulated-entity records, so the procedure logs only the term's length and whether it
    /// looked like an identifier, excludes <c>@SearchTerm</c> from <c>@KeyParameters</c> by name, and
    /// quotes it in no refusal message. <c>build/check_search_term_privacy.py</c> enforces that on the
    /// SQL side. Callers of this method carry the same obligation: the argument must not reach a log,
    /// a telemetry attribute, or an exception message. It is a plain parameter rather than a member of
    /// a query record specifically so that it is never captured in an object that something else might
    /// serialise wholesale.
    /// </para>
    /// <para>
    /// Contact columns are excluded from the <i>predicate</i> as well as the projection, because
    /// searching by contact surname would turn a handler search into a people search.
    /// </para>
    /// </remarks>
    public async Task<Page<HandlerSourceSearchRow>> SearchHandlerSourceAsync (
        string searchTerm,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        SqlParameter[] parameters =
        [
            Text ("SearchTerm", searchTerm, 200),
            Int ("Skip", skip),
            Int ("Take", take),
        ];

        var rows = await this.QueryAsync<HandlerSourceSearchRow> (
            "dbo.uspSearchHandlerSource",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return ToPage (rows, r => r.TotalRows, skip, take);
    }

    /// <summary>Reads one handler version in full — all 215 columns. Script 505.</summary>
    /// <param name="handlerSourceId">The surrogate key of the version to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <see langword="null"/> if it does not exist or is soft-deleted.</returns>
    /// <remarks>
    /// <b>This is the only read that returns PII</b> — contact name, phone, e-mail and address. Script
    /// 505's header states the corollary and it applies to callers too: nothing read from this row is
    /// ever copied into a log, not even to make an error message more helpful. A message saying which
    /// handler's contact record failed to load would be writing a name into a table the monitoring web
    /// app can read.
    /// </remarks>
    public async Task<HandlerSourceDetail?> GetHandlerSourceDetailAsync (
        int handlerSourceId,
        CancellationToken cancellationToken = default)
    {
        SqlParameter[] parameters = [Int ("HandlerSourceId", handlerSourceId)];

        var rows = await this.QueryAsync<HandlerSourceDetail> (
            "dbo.uspGetHandlerSourceDetail",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Reads one page of the load-run history. Script 500.</summary>
    /// <param name="query">Paging, sort and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, with the total number of runs matching the filter.</returns>
    public async Task<Page<LoadRunRow>> GetLoadRunPageAsync (
        LoadRunPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (query);

        SqlParameter[] parameters =
        [
            Int ("Skip", query.Skip),
            Int ("Take", query.Take),
            Text ("SortBy", query.SortBy, 50),
            Bit ("SortDescending", query.SortDescending),
            Int ("LoadRunId", query.LoadRunId),
            Text ("RunMode", query.RunMode, 20),
            Text ("ActivityLocation", query.ActivityLocation, 2),
            Text ("Status", query.Status, 20),
            DateTime2 ("StartedFromUtc", query.StartedFromUtc),
            DateTime2 ("StartedToUtc", query.StartedToUtc),
            Bit ("IncludeDeleted", query.IncludeDeleted),
        ];

        var rows = await this.QueryAsync<LoadRunRow> (
            "logs.uspGetLoadRunPage",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return ToPage (rows, r => r.TotalRows, query.Skip, query.Take);
    }

    /// <summary>Reads one page of per-handler load status. Script 501.</summary>
    /// <param name="query">Paging, sort and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, with the total number of status rows matching the filter.</returns>
    /// <remarks>
    /// This is the grid whose job is to show failures, so it carries <c>ApiErrorMessage</c>. That text
    /// is EPA's, not a user's, and it is displayed — but it is excluded from <c>@KeyParameters</c> by
    /// name on the write side, because a log row is not the place for text of unbounded provenance.
    /// </remarks>
    public async Task<Page<HandlerLoadStatusRow>> GetHandlerLoadStatusPageAsync (
        HandlerLoadStatusPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull (query);

        SqlParameter[] parameters =
        [
            Int ("Skip", query.Skip),
            Int ("Take", query.Take),
            Text ("SortBy", query.SortBy, 50),
            Bit ("SortDescending", query.SortDescending),
            Int ("LoadRunId", query.LoadRunId),
            Text ("HandlerId", query.HandlerId, 12),
            Text ("ActivityLocation", query.ActivityLocation, 2),
            Text ("SourceType", query.SourceType, 1),
            Text ("Status", query.Status, 20),
            Text ("Outcome", query.Outcome, 20),
            DateTime2 ("FirstSeenFromUtc", query.FirstSeenFromUtc),
            DateTime2 ("FirstSeenToUtc", query.FirstSeenToUtc),
            Bit ("LatestOnly", query.LatestOnly),
            Bit ("IncludeDeleted", query.IncludeDeleted),
        ];

        var rows = await this.QueryAsync<HandlerLoadStatusRow> (
            "logs.uspGetHandlerLoadStatusPage",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return ToPage (rows, r => r.TotalRows, query.Skip, query.Take);
    }

    /// <summary>Reads one load run's full set of counters and timings. Script 502.</summary>
    /// <param name="loadRunId">The run to summarise.</param>
    /// <param name="includeDeleted">Whether to return the run if it has been soft-deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The summary, or <see langword="null"/> if the run does not exist.</returns>
    /// <remarks>
    /// The summary includes <c>FailureMessage</c>, which script 502's header records as being returned
    /// to a web page and therefore as never permitted to contain the API key or any credential. That
    /// obligation sits on whatever writes it — see <see cref="CompleteLoadRunAsync"/>.
    /// </remarks>
    public async Task<LoadRunSummary?> GetLoadRunSummaryAsync (
        int loadRunId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        SqlParameter[] parameters =
        [
            Int ("LoadRunId", loadRunId),
            Bit ("IncludeDeleted", includeDeleted),
        ];

        var rows = await this.QueryAsync<LoadRunSummary> (
            "logs.uspGetLoadRunSummary",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Reads the incremental-load watermark for one feed. Script 512.</summary>
    /// <param name="feedName">The feed, as <c>config.LoadWatermark</c> names it.</param>
    /// <param name="activityLocation">The state. <c>MD</c> is the only value in scope (G2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The watermark row. Never <see langword="null"/> in practice — see the remarks.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>An unconfigured feed is a <see cref="SqlException"/>, not a null.</b> Script 512 raises
    /// <see cref="SqlErrorNumbers.ProcedureRefusal"/> when no active watermark exists for the feed and
    /// activity location, and a soft-deleted row counts as none. Its reasoning is that both guesses a
    /// caller could make are wrong: reading "no configuration" as a full load makes a mistyped feed
    /// name re-fetch everything, and reading it as "nothing to do" makes a scheduled load stop
    /// happening and report success. So <b>a caller must catch the refusal</b> rather than test the
    /// result for null. The null branch below is unreachable and is kept only because a procedure that
    /// returned an empty set would otherwise crash here rather than say so.
    /// </para>
    /// <para>
    /// A null <c>WatermarkDate</c> on a row that <i>does</i> exist is a different thing again, and is
    /// returned normally: it means the feed is configured but has never completed a run, which is the
    /// signal for a full load rather than an incremental one. Use <c>IsEnabled = 0</c> to pause a feed;
    /// soft-deleting one retires it, and this read then refuses it.
    /// </para>
    /// </remarks>
    public async Task<LoadWatermark?> GetLoadWatermarkAsync (
        string feedName,
        string activityLocation,
        CancellationToken cancellationToken = default)
    {
        SqlParameter[] parameters =
        [
            Text ("FeedName", feedName, 50),
            Text ("ActivityLocation", activityLocation, 2),
        ];

        var rows = await this.QueryAsync<LoadWatermark> (
            "config.uspGetLoadWatermark",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Reads the resume set: every handler version the previous unfinished run of this activity
    /// location already dealt with. Script 525.
    /// </summary>
    /// <param name="activityLocation">The population being loaded. <c>MD</c> (G2).</param>
    /// <param name="loadRunId">
    /// A specific run to resume from, or <see langword="null"/> — the ordinary case — to let the
    /// procedure find the previous run of this activity location.
    /// </param>
    /// <param name="abandonAfterMinutes">
    /// How long a run may sit at <c>Running</c> before it counts as killed. <b>Must be the same value
    /// as <see cref="LoadRunRequest.AbandonAfterMinutes"/></b> — see the remarks.
    /// </param>
    /// <param name="maxAgeHours">
    /// How old a previous run's success may be and still be trusted enough to skip. <see
    /// langword="null"/> for no limit. Ignored when <paramref name="loadRunId"/> names a run.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One row per version, ordered by handler, source type and sequence. <b>Empty means resume from
    /// nothing</b>, which is a normal answer on four separate paths and never an error.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Call this before <see cref="StartLoadRunAsync"/>, and pass both the same
    /// <paramref name="abandonAfterMinutes"/>.</b> The ordering is forced: <c>logs.uspStartLoadRun</c>
    /// takes <c>@ResumedFromLoadRunId</c> as an input, so the answer has to exist before the new run's
    /// row does. Which means the abandonment sweep — which lives in that procedure — has not run yet,
    /// and a run killed by a reboot is <i>still</i> marked <c>Running</c> at the moment this read
    /// looks. Script 525 therefore applies the same threshold itself, without writing anything, and
    /// the two procedures agree only because the caller sends them the same number. Send a shorter one
    /// here and a healthy in-flight run is offered as resumable; send a longer one and the resume
    /// quietly stops happening, which looks exactly like a first run.
    /// </para>
    /// <para>
    /// <b>A returned row is not automatically a skip.</b> Only
    /// <see cref="HandlerLoadResumeRow.Status"/> <c>== "Succeeded"</c> means "do not fetch this one".
    /// The rest are returned so that a version the previous run knew about and this run's summaries
    /// walk does not name can be counted and reported (G25).
    /// </para>
    /// <para>
    /// <b>This read raises rather than returning nothing in exactly two cases</b>, both of them caller
    /// defects with no safe reading: a <paramref name="loadRunId"/> that does not exist or has been
    /// soft-deleted, and one belonging to a different activity location. The second is the dangerous
    /// one — resuming one population's run into another's would skip versions that are not in the load
    /// and show every one of them as handled. A live <c>Running</c> run raises when it was named and
    /// returns nothing when it was found automatically, because <c>logs.uspStartLoadRun</c> already
    /// owns the concurrency refusal and two procedures reporting one collision means the caller sees
    /// the wrong one first.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<HandlerLoadResumeRow>> GetHandlerLoadResumeSetAsync (
        string activityLocation,
        int? loadRunId = null,
        int? abandonAfterMinutes = 720,
        int? maxAgeHours = 48,
        CancellationToken cancellationToken = default)
    {
        SqlParameter[] parameters =
        [
            Text ("ActivityLocation", activityLocation, 2),
            Int ("LoadRunId", loadRunId),
            Int ("AbandonAfterMinutes", abandonAfterMinutes),
            Int ("MaxAgeHours", maxAgeHours),
        ];

        return await this.QueryAsync<HandlerLoadResumeRow> (
            "logs.uspGetHandlerLoadResumeSet",
            parameters,
            this.options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait (false);
    }
}
