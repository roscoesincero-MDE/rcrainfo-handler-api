namespace RCRAInfo.Loader.Api;

/// <summary>
/// What happened when one of the RCRAInfo <b>data</b> or <b>lookup</b> endpoints was called. One value per
/// <b>loader branch</b> — the same rule <see cref="ApiAuthOutcome"/> follows, with one addition: every
/// value here must also project onto <c>logs.HandlerLoadAttempt.Outcome</c>, whose domain is closed at
/// five values by <c>CK_logs_HandlerLoadAttempt_Outcome</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This enum is wider than that column on purpose, and the projection is where the two meet.</b> The
/// column answers "what does this attempt say about EPA's health", so it has five values; the loader has
/// to answer "what do I do next", and <c>404</c> versus <c>403</c> versus <c>429</c> are three different
/// answers that all reduce to a failed or throttled attempt. Collapsing them here to match the column
/// would put the decision back into a status-code comparison at every call site, which is the shape of
/// mistake the closed set exists to prevent. <see cref="ApiFetchOutcomeExtensions.ToAttemptOutcome"/> does
/// the reduction once, and a test asserts it is total.
/// </para>
/// <para>
/// <b>The lookup family is the one caller with nowhere to project to.</b> Script 524 resolves every attempt
/// against a <c>logs.HandlerLoadStatus</c> row the same run enumerated, and a code-list refresh is about no
/// handler version at all — so a lookup call has no attempt row, and <c>LookupRefreshReport</c> keeps the
/// outcome itself instead. That is also why it keeps it on success: a <see cref="Succeeded"/> after a
/// <see cref="Throttled"/> is a different fact about EPA, and for the data endpoints the attempt log says so
/// where for a lookup nothing else would.
/// </para>
/// <para>
/// <b>Classification is against the documented status set of the endpoint being called, not against a
/// single table of status codes</b> — see <see cref="RcraInfoDataRequest.DocumentsNotFound"/>. The pinned
/// spec documents different answers for the three endpoints, and the differences are load-bearing:
/// <c>/hd/sources/{handlerId}/{sourceType}/{sequence}</c> documents <c>404</c>, so a <c>404</c> there is
/// EPA saying the record is gone and is the AR7 soft-delete signal; <c>/hd/other-ids</c> documents
/// <c>200, 400, 401, 403, 500</c> and <b>no <c>404</c></b>, so a <c>404</c> there is not an absent handler
/// — it is this client calling the endpoint wrongly, which is exactly the failure the corrected path form
/// would have produced (plan §D2, [R28]). Treating both as <see cref="NotFound"/> would have turned a
/// client defect into a stream of spurious soft deletes.
/// </para>
/// </remarks>
public enum ApiFetchOutcome
{
    /// <summary><c>200</c> with a body this client could read. The only value that carries data.</summary>
    Succeeded,

    /// <summary>
    /// <c>404</c> from an endpoint that documents it. EPA does not have the record asked for.
    /// </summary>
    /// <remarks>
    /// <b>Not a failure, and this is the one outcome whose handling is a requirement rather than a
    /// choice.</b> AR7 needs to know when RCRAInfo no longer has a record, and G23 records that we do not
    /// know whether the summaries feed reports deletions at all. A <c>404</c> on a version the feed itself
    /// named is the one deletion signal available without EPA answering G23, so it feeds
    /// <c>dbo.uspSoftDeleteHandlerSourceSet</c> rather than a retry. Never retried: a record EPA does not
    /// have does not appear because it was asked for twice.
    /// </remarks>
    NotFound,

    /// <summary>
    /// <c>400</c>. EPA rejected the request as malformed — a client defect, not a data condition.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Unexpected"/> because the remedy is specific and immediate: the request
    /// this client built is wrong, and every subsequent call built the same way will be wrong too. The
    /// orchestrator should treat a <c>400</c> as fatal to the run rather than skip the handler and continue,
    /// which is the reflexive per-handler response and which would send several hundred thousand malformed
    /// requests at EPA to collect several hundred thousand identical rejections.
    /// </remarks>
    BadRequest,

    /// <summary>
    /// <c>401</c> on a data call. The bearer token was missing, malformed or expired.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ApiAuthOutcome.InvalidCredentials"/>, which is EPA rejecting the API ID and
    /// Key at the auth endpoint. Reaching here means <c>ApiTokenHandler</c> attached a token EPA would not
    /// accept, and it has already retried once with a fresh one — that is its single-retry contract. So a
    /// <c>401</c> that survives to this classification is <b>not</b> retryable by the caller: retrying
    /// would repeat the refresh the handler just did. It is the signal that the credential itself needs
    /// re-seeding, which is the auth endpoint's remedy reached by a different road.
    /// </remarks>
    Unauthorized,

    /// <summary>
    /// <c>403</c>. The token is good and the account is not permitted the data asked for.
    /// </summary>
    /// <remarks>
    /// A scope problem at EPA (G2) — RCRAInfo grants API access by state and region. Fatal to the run for
    /// the same reason as <see cref="BadRequest"/>: if this account may not read Maryland Handler data, it
    /// may not read it for the next four hundred thousand handlers either, and per-handler retries would
    /// turn one permissions problem into a sustained load aimed at an endpoint that is refusing us.
    /// </remarks>
    AccessDenied,

    /// <summary>
    /// <c>429</c>. EPA is asking this client to slow down, and may have said by how much.
    /// </summary>
    /// <remarks>
    /// The one outcome that carries an instruction rather than just a fact: a <c>Retry-After</c> header, if
    /// present, is EPA's own answer to G21 for this moment, and it is recorded in
    /// <c>logs.HandlerLoadAttempt.RetryAfterSeconds</c> so a run can be tuned from what EPA actually asked
    /// for instead of from a guess. Retryable, and the only retryable outcome where the delay is not ours
    /// to choose. It projects onto the attempt log's own <c>Throttled</c>, which exists precisely so that a
    /// throttled-then-succeeded handler does not read as a clean fetch.
    /// </remarks>
    Throttled,

    /// <summary><c>5xx</c> or <c>408</c>. EPA's problem, and retryable.</summary>
    ServiceFailure,

    /// <summary>
    /// The request never got an answer: DNS, TLS, a refused connection, a proxy. Retryable.
    /// </summary>
    /// <remarks>
    /// Named separately from <see cref="ServiceFailure"/> for the reason
    /// <see cref="ApiAuthOutcome.Unreachable"/> gives: the first thing an operator would try is a network
    /// remedy, not a wait.
    /// </remarks>
    Unreachable,

    /// <summary>The request was abandoned because it took too long. Retryable.</summary>
    /// <remarks>
    /// Kept apart from <see cref="Unreachable"/> because the attempt log keeps them apart — <c>TimedOut</c>
    /// is one of its five values — and because the two say different things about EPA under load. A run
    /// whose failures are timeouts is a run that found EPA's throughput limit without being told about it,
    /// which is the second-best answer to G21 and the one available without asking.
    /// </remarks>
    TimedOut,

    /// <summary>
    /// The caller cancelled: the process is shutting down, or the run was stopped.
    /// </summary>
    /// <remarks>
    /// Not a failure and never retried. It is also the one attempt outcome that
    /// <c>logs.uspRecordHandlerLoadAttemptSet</c> accepts with no accompanying detail — no status code, no
    /// error code, no failure message — because a cancelled call genuinely has nothing to say, and
    /// demanding a message would produce a fabricated one.
    /// </remarks>
    Cancelled,

    /// <summary>
    /// EPA answered with something this client cannot use: a <c>200</c> whose body will not parse, a
    /// <c>200</c> where the body is absent, or a status the pinned spec does not document for this endpoint.
    /// </summary>
    /// <remarks>
    /// Not retryable — an unparseable success parses no better the second time — and the one outcome that
    /// means <b>the pinned spec and the live service have diverged</b>. That makes it the most valuable
    /// failure in this enum and the one most easily lost by folding it into
    /// <see cref="ServiceFailure"/>, where it would be retried three times and then reported as EPA being
    /// unwell.
    /// </remarks>
    Unexpected,
}
