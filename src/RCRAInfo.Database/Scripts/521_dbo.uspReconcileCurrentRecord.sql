-- SET XACT_ABORT ON sits ABOVE the header block deliberately, and moving it back below would restore a
-- real defect. The GO on the next line ends the batch, and sys.sql_modules stores only the batch that
-- contains CREATE -- so a header placed AFTER this GO is invisible to anyone reading the procedure out
-- of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as CREATE", which is where a
-- maintainer actually reads it. build/check_stored_headers.py measures this against the deployed
-- catalog on every --with-database run.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReconcileCurrentRecord
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Sets dbo.HandlerSource.CurrentRecord across every version of a handler from EPA's own version list, and asserts the
invariant that the rest of the database depends on: at most one CurrentRecord = 1 per (HandlerId, SourceType) among live
rows.

That invariant is not decoration. dbo.vwHandlerSource -- the default read path for the web app and, in Phase 2, for the
ETS migration -- is dbo.vwHandlerSourceHistory filtered to WHERE CurrentRecord = 1. Two current versions of one handler
make that view return the same handler twice; none makes the handler disappear from it entirely. Neither failure is
visible in the table, and both are invisible in the view, which is exactly why they have to be measured here.

WHY THIS PROCEDURE EXISTS AT ALL, rather than the flag simply being merged with the rest of the payload. It is merged
with the rest of the payload -- dbo.uspMergeHandlerSourceBatch takes CurrentRecord from '$.handler.currentRecord' like
any other column. The problem is that the flag is a property of the handler's whole version list and is delivered as a
property of a single version. When EPA adds version 4, version 3 stops being current, and nothing in version 4's
payload says so. A loader that only ever merges the versions it fetched leaves version 3 claiming to be current forever.
So, per Phase1-Analysis.md: whenever any version of a handler changes, re-fetch that handler's full summary list from
GET /api/v1/hd/sources/summaries?handlerId={id} and reconcile every version's flag from that authoritative list rather
than inferring it. One extra call per changed handler. This procedure is the database half of that.

========================================================================================================================
Requirements and Key Dependencies:

dbo.HandlerSource (script 100) -- specifically CurrentRecord BIT NULL and UX_dbo_HandlerSource_Natural over
(HandlerId, SourceType, Sequence) WHERE IsDeleted = 0, which is the key this procedure joins on.

logs.LoadRun (script 300), for the run this reconcile belongs to. logs.DataQualityObservation (script 330), where a
violated invariant is recorded. This is the first procedure in the database to write to that table.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, copied from
.claude/skills/sql-objects/templates/procedure.sql.

EXECUTE is granted to RCRAInfoLoaderRole only. The monitoring web app reads CurrentRecord through the views and has no
business setting it.

========================================================================================================================
Notes:

@Summaries MUST BE THE COMPLETE VERSION LIST FOR EVERY PAIR IT MENTIONS. This is the one hard contract of this
procedure, and it is a contract because it cannot be checked from inside the database: there is no way to tell a handler
whose version 3 EPA no longer lists from a handler whose version 3 the caller simply did not include. A live version of
a mentioned (HandlerId, SourceType) that the list does not name is therefore set to CurrentRecord = 0 -- it cannot be
current if the authority on currency does not list it -- and an observation is recorded naming it, because a version
present in the mirror and absent from EPA's list is a candidate for the retention pass, not a normal state. Pass the
summaries response as it came back, filtered to nothing. Passing only the versions this run happened to fetch would
demote the rest of the handler's history and fill logs.DataQualityObservation with the consequences.

WHAT HAPPENS WHEN EPA'S OWN LIST NAMES TWO CURRENT VERSIONS, which is the case the Phase 1 data-quality check was asked
for. The mirror does NOT reproduce the contradiction. An Error-severity observation is recorded naming the pair and
every sequence that claimed to be current, each with its receivedDate, and then the LATEST RECEIVED of the claimants is
taken as current and the others are set to 0. Mirroring EPA faithfully would be the defensible choice for any other
column in this table and is the wrong one here, because the contradiction does not stay in the table: it propagates
into dbo.vwHandlerSource, which is the default read path, and from there into ETS in Phase 2. The deviation is recorded
rather than silent, which is the whole reason logs.DataQualityObservation exists ([R10]).

[R43] THE TIE-BREAK WAS HIGHEST SEQUENCE UNTIL 2026-09-07, AND HIGHEST SEQUENCE WAS WRONG. The reasoning it rested on
was that sequence is EPA's own version ordering and therefore EPA's own chronology. It is not. Sequence is an INSERTION
order, and the two come apart whenever the same handler is submitted more than once: 1,181 of 27,585 mirrored pairs
have a highest sequence that is not their newest version. MDD985416569 is the case that found it. EPA's Source Summary
page and EPA's API both name sequences 4 and 7 current there, so the Error above was faithful -- but 4 is the real
record (received 2026-05-11, the only version carrying an episodic event) while 3, 5, 6 and 7 are four copies of one
2025-01-02 submission that MDE sent to EPA repeatedly. Highest sequence picked the last DUPLICATE over the only real
update. receivedDate is EPA's own business date, is what EPA's screen displays, and is non-NULL on all 43,048 live
mirrored versions, so it is both correct and always available; sequence remains the final key, which is what resolves
the four copies deterministically. Measured against run 2623's 410 ambiguous pairs, this changes the outcome for
exactly one of them -- and that one was wrong before. Evidence: section F1-ui-comparison Check 1 in Phase1-Plan.md.

AND WHEN NO VERSION IS CURRENT. Recorded as a Warning, not refused. A HANDLER whose live versions are all
CurrentRecord = 0 vanishes from dbo.vwHandlerSource -- the handler is in the database and absent from every default read
of it, which looks exactly like a handler that was never loaded. That is worth a row in the observations table even
though EPA is entitled to say it.

[R47] THE QUESTION IS ASKED PER HANDLER, AND UNTIL 2026-09-08 IT WAS ASKED PER (HandlerId, SourceType), WHICH MADE IT
MEANINGLESS. CurrentRecord is EPA's answer to "which version is this handler's live record", and EPA answers it once per
HANDLER, not once per source type. Asked per pair, the check fired 10,098 Warnings in one full load against a state
that is not merely legal but ordinary: 9,867 of those 10,098 pairs belonged to a handler that WAS current under a
different source type. MDE supplied the semantics the code was missing -- 'I' is Implementer, meaning the data set
originated with MDE rather than with the site, so 'I' legitimately carries the flag instead of the site's own 'N'
notification -- and 'R' (Biennial Report) held a current on 10 of 1,605 mirrored pairs without being defective the
other 1,595 times. Re-grained to HandlerId the same question over the same data reports 185 handlers, and those 185 are
real: every one of them was reconciled by the run that reported them and none has a current version under any source
type, so each is genuinely absent from dbo.vwHandlerSource. That is the difference between a check and alarm fatigue,
and it mattered beyond itself -- CurrentRecordAmbiguousInSource lives in the same table, is real, and was being buried
under a fifty-to-one ratio of noise. Evidence: section F1-ui-comparison Check 3 in Phase1-Plan.md.

THE ASSERTION IS "AT LEAST ONE" PER HANDLER AND NEVER "EXACTLY ONE". 331 mirrored handlers carry two or three currents
across DIFFERENT source types -- [R44] measured the combinations as B+N, D+N, I+N, B+D, D+I and B+D+N, 333 handlers at
the time -- and every one is a legitimate EPA state, so "exactly one" would trade a 10,098-row false alarm for a
331-row one. The contradiction that IS always wrong
is two currents within ONE pair, which is measured separately at the pair grain (6a) and where the mirror has zero
violations. Both halves are needed; neither substitutes for the other.

THE POST-WRITE CHECK IS RUN AGAINST THE TABLE, NOT AGAINST @Summaries, and that is not redundant with the tie-break
above. After the tie-break the source set cannot name two currents, so a violation found afterwards means something
else is true: a concurrent writer set the flag, or a version of this pair exists that the reconcile did not cover. The
check that only ever confirms its own arithmetic proves nothing; this one can actually fail, which is what makes
running it worth the scan.

G35 APPLIES HERE, unlike in script 520. Observations are logging, they live outside logs.ExecutionLog, and a rollback
takes them with it -- so they are accumulated in a table variable (which survives rollback) and written once. On the
successful path they are written inside the transaction. In the CATCH they are flushed after the rollback, with a
sentence appended to each Detail saying the reconcile that found the problem then failed, so nothing was corrected. An
observation that describes a correction which never happened is worse than no observation.

CurrentRecord IS NULLABLE and stays that way. NULL means "no reconcile has run for this version yet", which is a real
and useful third state on a freshly loaded row; dbo.vwHandlerSource filters CurrentRecord = 1, so NULL is excluded
without any special handling. Every comparison here therefore uses IS DISTINCT FROM rather than <>, or the first
reconcile of a newly inserted version would match nothing and write nothing.

WHAT @KeyParameters MAY CONTAIN. Identifiers and counts. @Summaries is excluded BY NAME -- it names regulated entities,
a full-Maryland reconcile carries tens of thousands of them, and logs.ExecutionLog has a different read audience and a
different retention policy from dbo.HandlerSource. From MDE's own template: do NOT include parameters such as
passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- 1. Handler MD0000123456 changed tonight, so its whole summary list was re-fetched. Version 3 was current
--    until version 4 appeared, and nothing in version 4's own payload said so.
DECLARE @Rows INT, @Observed INT;
EXEC dbo.uspReconcileCurrentRecord
      @LoadRunId    = 1
    , @Summaries    = N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"currentRecord":false}
                        ,{"handlerId":"MD0000123456","sourceType":"N","sequence":2,"currentRecord":false}
                        ,{"handlerId":"MD0000123456","sourceType":"N","sequence":3,"currentRecord":false}
                        ,{"handlerId":"MD0000123456","sourceType":"N","sequence":4,"currentRecord":true}]'
    , @RowsAffected  = @Rows     OUTPUT
    , @Observations  = @Observed OUTPUT;

-- 2. Many handlers in one call. The whole night's changed set is one reconcile, not one call per handler.
EXEC dbo.uspReconcileCurrentRecord @LoadRunId = 1, @Summaries = @NightlySummaries;

-- 3. A no-op, not an error. A run in which nothing changed reconciles nothing.
EXEC dbo.uspReconcileCurrentRecord @LoadRunId = 1, @Summaries = N'[]';

Two set-based UPDATEs, each seeking UX_dbo_HandlerSource_Natural, plus one aggregate pass over the live versions of the
pairs mentioned. Cost scales with the number of versions in @Summaries, and the payload is parsed once. Both UPDATEs are
guarded by IS DISTINCT FROM, so a reconcile that finds nothing to change writes nothing and moves no
auditModifiedDateUtc -- which matters more here than in most places, because this procedure is expected to run against
handlers whose flags are already correct.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4. Named by MDE as the procedure to build
											after logs.uspUpsertHandlerLoadStatusSet.
2026-09-05	rsincero						@Summaries validation now requires ISJSON (@Summaries, ARRAY). See script
											520's history entry of the same date for the finding.
2026-09-07	rsincero						[R43] The ambiguity tie-break now orders by receivedDate DESC before
											Sequence DESC, where it used to take the highest sequence alone.
											@Summary carries a new ReceivedDate column shredded from
											$.receivedDate; it is TRY_CAST and is NOT validated, so a caller that
											omits the field gets the previous behaviour rather than a refusal.
											CurrentRecordAmbiguousInSource now reports the winning sequence taken
											from @Authoritative instead of recomputing MAX (Sequence), and its
											ObservedValue pairs each claimant with its receivedDate so a duplicate
											submission is distinguishable from a genuine conflict without a second
											query. Found by F1's UI comparison on MDD985416569 -- see the [R43]
											paragraph in the header.
2026-09-08	rsincero						[R47] CurrentRecordMissingInMirror is now asked PER HANDLER instead of
											per (HandlerId, SourceType), which is the grain the question has. The
											single aggregate that raised both halves of the invariant is now two:
											6a keeps CurrentRecordAmbiguousInMirror at the pair grain, where two
											currents in one pair really is a contradiction, and 6b asks "is this
											handler current under ANY source type" and reports only when the answer
											is no. Same data, 10,098 Warnings -> 185, and the 185 are real. 6b's
											rows carry SourceType NULL because the finding is no longer about one
											source type. See the [R47] paragraph in the header.
2026-09-08	rsincero						[R47] Both flushes now populate
											logs.DataQualityObservation.HandlerLoadStatusId, which no writer in the
											database had ever set -- the column, FK_logs_DataQualityObservation_
											HandlerLoadStatus and IX_logs_DataQualityObservation_HandlerLoadStatusId
											all existed for a value that was NULL on 100% of 10,507 rows. Resolved
											by scalar subquery on UX_logs_HandlerLoadStatus_Natural, so it cannot
											multiply the flush, and it stays NULL for observations that name no
											Sequence because those point at no single version.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReconcileCurrentRecord
      @LoadRunId    INT
    , @Summaries    NVARCHAR (MAX)
    , @RowsAffected INT            = NULL OUTPUT
    , @Observations INT            = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- AR8 instrumentation. Boilerplate: copied verbatim from the template.
    -- =============================================================================================
    -- The literal is not a fallback for odd cases; it is what the loader login actually logs, because
    -- script 050 denies it metadata visibility. See the header. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReconcileCurrentRecord]')
          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2      = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    DECLARE @NowUtc          DATETIME2       = SYSUTCDATETIME ()
          , @VersionCount    INT             = 0
          , @PairCount       INT             = 0
          , @SetFromList     INT             = 0
          , @DemotedUnlisted INT             = 0
          , @AmbiguousSource INT             = 0
          , @RunStatus       NVARCHAR (20)   = NULL
          , @Failure         NVARCHAR (2048) = NULL
          -- Guards the CATCH flush. The completion UPDATE runs after the COMMIT, so a failure there
          -- reaches the CATCH with the observations already committed and no rollback having happened.
          -- Flushing them again would double every row.
          , @Committed       BIT             = 0;

    -- Identifiers and counts only. @Summaries is excluded BY NAME; see the header. The counts are
    -- added after the shred, because that is when they are known.
    SET @KeyParameters = CONCAT (N'LoadRunId='
                               , COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)'));

    -- The shred target is WIDE on purpose. OPENJSON ... WITH truncates silently (G36), and a truncated
    -- HandlerId names a DIFFERENT handler rather than no handler -- which in this procedure would set
    -- the current flag on somebody else's version list. Widths are checked below, before any write.
    DECLARE @Summary TABLE
    (
        Ordinal           INT             NOT NULL PRIMARY KEY,
        HandlerId         NVARCHAR (4000)     NULL,
        SourceType        NVARCHAR (4000)     NULL,
        Sequence          INT                 NULL,
        CurrentRecordText NVARCHAR (4000)     NULL,
        IsCurrent         BIT                 NULL,
        -- Tie-break input only, and deliberately NOT validated: an absent or unreadable receivedDate
        -- means "no opinion", the ordering falls back to Sequence, and the result is exactly what this
        -- procedure did before the field was consulted at all. Refusing on it would make a caller that
        -- omits the field fail against a procedure it used to satisfy, and the loader and the database
        -- deploy independently of each other. See [R43] in the header.
        ReceivedDate      DATE                NULL
    );

    -- EPA's list after the tie-break, narrowed to the real column widths. This, not @Summary, is what
    -- the writes join to: it holds at most one IsCurrent = 1 per pair by construction.
    DECLARE @Authoritative TABLE
    (
        HandlerId  NVARCHAR (12) NOT NULL,
        SourceType NVARCHAR (1)  NOT NULL,
        Sequence   INT           NOT NULL,
        IsCurrent  BIT           NOT NULL,
        PRIMARY KEY (HandlerId, SourceType, Sequence)
    );

    -- G35. Accumulated here rather than inserted as they are found, so a rollback does not take the
    -- record of WHY the reconcile was unhappy along with the reconcile. A table variable is not
    -- affected by ROLLBACK. See the header.
    DECLARE @Observation TABLE
    (
        Ordinal         INT IDENTITY (1, 1) NOT NULL PRIMARY KEY,
        ObservationType NVARCHAR (50)       NOT NULL,
        Severity        NVARCHAR (20)       NOT NULL,
        HandlerId       NVARCHAR (12)           NULL,
        SourceType      NVARCHAR (1)            NULL,
        Sequence        INT                     NULL,
        JsonPath        NVARCHAR (400)          NULL,
        ObservedValue   NVARCHAR (400)          NULL,
        Detail          NVARCHAR (4000)         NULL
    );

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. ==============================================
        -- =========================================================================================

        -- -----------------------------------------------------------------------------------------
        -- 1. Validation, all of it before BEGIN TRANSACTION so a rejected call has no transaction to
        --    unwind and is recorded as a failed execution like any other.
        -- -----------------------------------------------------------------------------------------
        -- Assigned first, then thrown. THROW's message argument accepts a literal or a variable and
        -- NOT an expression, so a concatenation written inline is a syntax error.
        IF @LoadRunId IS NULL
        BEGIN
            SET @Failure = N'@LoadRunId is required. This procedure can record observations, and '
                         + N'logs.DataQualityObservation.LoadRunId is NOT NULL -- an observation that '
                         + N'names no run cannot be shown against one, so a reconcile with nowhere to '
                         + N'report a violated invariant must not run at all.';
            THROW 50000, @Failure, 1;
        END;

        -- ARRAY, not just ISJSON, for the reason script 520 records in full: a bare object passes a
        -- plain ISJSON, OPENJSON then enumerates its properties rather than its elements, and the
        -- caller gets error 245 from an internal CAST instead of this refusal.
        IF @Summaries IS NULL OR ISJSON (@Summaries, ARRAY) = 0
        BEGIN
            SET @Failure = N'@Summaries is NULL, is not valid JSON, or is not a JSON array. It must be '
                         + N'an array of objects carrying handlerId, sourceType, sequence and '
                         + N'currentRecord -- even for a single element -- and it must be the COMPLETE '
                         + N'version list for every handler and source type it mentions. An empty '
                         + N'array [] is accepted and does nothing.';
            THROW 50000, @Failure, 1;
        END;

        -- The run has to exist, be live, and still be Running, for the reason script 520 gives:
        -- observations recorded against a closed run contradict the summary the monitor displays.
        SELECT @RunStatus = r.Status
          FROM logs.LoadRun AS r
         WHERE r.LoadRunId = @LoadRunId
           AND r.IsDeleted = 0;

        IF @RunStatus IS NULL
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' does not exist, or has been '
                                 , N'soft-deleted. Open a run with logs.uspStartLoadRun and reconcile '
                                 , N'against the LoadRunId it returns.');
            THROW 50000, @Failure, 1;
        END;

        IF @RunStatus <> N'Running'
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' has Status ''', @RunStatus
                                 , N''', so it is closed and no further work can be recorded against '
                                 , N'it. Either this call belongs to a newer run, or '
                                 , N'logs.uspCompleteLoadRun was called before the reconcile ran -- '
                                 , N'and the reconcile is part of the load, not something after it.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 2. Shred. JSON_VALUE rather than OPENJSON ... WITH, so nothing is truncated on the way in.
        -- -----------------------------------------------------------------------------------------
        INSERT INTO @Summary (Ordinal, HandlerId, SourceType, Sequence, CurrentRecordText, IsCurrent
                            , ReceivedDate)
        SELECT CAST (e.[key] AS INT) + 1
             , JSON_VALUE (e.value, '$.handlerId')
             , JSON_VALUE (e.value, '$.sourceType')
             , TRY_CAST (JSON_VALUE (e.value, '$.sequence') AS INT)
             , JSON_VALUE (e.value, '$.currentRecord')
             -- A JSON boolean comes out of JSON_VALUE as the text 'true' or 'false', and TRY_CAST of
             -- either to BIT is NULL -- so the mapping has to be explicit. Numeric 1/0 is accepted
             -- as well, because a serializer that writes the flag as a number is not wrong, only
             -- different. Anything else lands as NULL and is refused by name below rather than
             -- quietly read as "not current", which would demote a version EPA calls current.
             , CASE WHEN LOWER (JSON_VALUE (e.value, '$.currentRecord')) IN ('true',  '1') THEN 1
                    WHEN LOWER (JSON_VALUE (e.value, '$.currentRecord')) IN ('false', '0') THEN 0
                    ELSE NULL END
             -- TRY_CAST, not CAST: see the column comment. EPA sends 'yyyy-MM-dd', which is
             -- unambiguous for DATE under every language setting, so this needs no style argument.
             , TRY_CAST (JSON_VALUE (e.value, '$.receivedDate') AS DATE)
          FROM OPENJSON (@Summaries) AS e;

        SET @VersionCount = @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 3. The natural key, complete, within its widths, and with a readable flag. Every check
        --    names the offending element, because "the batch was rejected" is not actionable at 02:00.
        -- -----------------------------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM @Summary
                    WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'Element ', Ordinal, N' of @Summaries is missing part '
                                            , N'of the natural key. handlerId, sourceType and '
                                            , N'sequence are all required and identify the version '
                                            , N'whose flag is being set: handlerId='
                                            , COALESCE (N'''' + HandlerId + N'''', N'NULL')
                                            , N', sourceType='
                                            , COALESCE (N'''' + SourceType + N'''', N'NULL')
                                            , N', sequence='
                                            , COALESCE (CAST (Sequence AS NVARCHAR (11)), N'NULL')
                                            , N'.')
              FROM @Summary
             WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL
             ORDER BY Ordinal;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Summary
                    WHERE LEN (HandlerId) > 12 OR LEN (SourceType) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'Element ', Ordinal, N' of @Summaries does not fit '
                                            , N'dbo.HandlerSource: handlerId is ', LEN (HandlerId)
                                            , N' characters (max 12) and sourceType is '
                                            , LEN (SourceType), N' (max 1). The set is refused rather '
                                            , N'than truncated, because a shortened handlerId is a '
                                            , N'DIFFERENT handler and this procedure would set the '
                                            , N'current flag across their version list.')
              FROM @Summary
             WHERE LEN (HandlerId) > 12 OR LEN (SourceType) > 1
             ORDER BY Ordinal;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Summary WHERE IsCurrent IS NULL)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'Element ', Ordinal, N' of @Summaries has an '
                                            , N'unreadable currentRecord: '
                                            , COALESCE (N'''' + CurrentRecordText + N'''', N'NULL')
                                            , N'. Send the JSON boolean true or false (1 and 0 are '
                                            , N'also accepted). It is refused rather than read as '
                                            , N'false, because reading it as false would demote a '
                                            , N'version EPA may well be calling current, and the '
                                            , N'handler would then vanish from dbo.vwHandlerSource.')
              FROM @Summary
             WHERE IsCurrent IS NULL
             ORDER BY Ordinal;
            THROW 50000, @Failure, 1;
        END;

        -- A duplicated version cannot be resolved here: if one copy says current and the other says
        -- not, there is no basis for choosing, and the tie-break below is about which VERSION is
        -- current, not which copy of one version to believe.
        IF EXISTS (SELECT 1 FROM @Summary
                   GROUP BY HandlerId, SourceType, Sequence
                   HAVING COUNT (*) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'@Summaries names the same version ', COUNT (*)
                                            , N' times: handlerId=''', HandlerId, N''', sourceType='''
                                            , SourceType, N''', sequence=', Sequence
                                            , N'. A summary list holds each version once; a repeat '
                                            , N'means the pages were concatenated wrongly or two '
                                            , N'handlers'' lists were merged.')
              FROM @Summary
             GROUP BY HandlerId, SourceType, Sequence
             HAVING COUNT (*) > 1
             ORDER BY HandlerId, SourceType, Sequence;
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 4. Narrow the list and apply the tie-break, so that what the writes join to holds at most
        --    one current version per pair. See the header for why the contradiction is not mirrored.
        -- -----------------------------------------------------------------------------------------
        -- The winner per pair is the version that CLAIMED to be current and was RECEIVED LATEST --
        -- never a version that did not claim it. Sequence breaks a tie in receivedDate and is the
        -- whole ordering when no receivedDate is readable, so a caller that omits the field gets the
        -- behaviour this procedure had before [R43] added it.
        --
        -- [R43] SEQUENCE ALONE WAS THE WRONG ORDERING, AND MDD985416569 IS THE PROOF. EPA's screen and
        -- EPA's API both name sequences 4 and 7 current there. 7 is the higher sequence and 4 is the
        -- real record: 4 was received 2026-05-11, while 3, 5, 6 and 7 are four copies of one 2025-01-02
        -- submission that MDE sent to EPA repeatedly. Sequence is an insertion order, not a chronology
        -- -- 1,181 of 27,585 mirrored pairs have a highest sequence that is not their newest version --
        -- so ordering by it picked the last DUPLICATE over the only real update. receivedDate is EPA's
        -- own business date, is the column EPA's Source Summary page displays, and is non-NULL on all
        -- 43,048 live mirrored versions. See section F1-ui-comparison Check 1 in Phase1-Plan.md.
        --
        -- ROW_NUMBER over the pre-filtered set, rather than MAX (CASE WHEN IsCurrent = 1 THEN ...):
        -- that form makes the aggregate discard a NULL for every non-current version, and SQL Server
        -- then raises warning 8153 to the client on every single call. The warning is harmless and it
        -- is also noise arriving from a procedure the loader runs thousands of times a night, where
        -- anything the server volunteers should mean something. Ranking a set that WHERE has already
        -- narrowed to IsCurrent = 1 feeds no NULL to any aggregate and stays silent.
        INSERT INTO @Authoritative (HandlerId, SourceType, Sequence, IsCurrent)
        SELECT CAST (s.HandlerId  AS NVARCHAR (12))
             , CAST (s.SourceType AS NVARCHAR (1))
             , s.Sequence
             , CASE WHEN s.IsCurrent = 1 AND s.Sequence = w.WinningSequence THEN 1 ELSE 0 END
          FROM @Summary AS s
          LEFT JOIN (SELECT HandlerId
                          , SourceType
                          , WinningSequence = Sequence
                       FROM (SELECT HandlerId
                                  , SourceType
                                  , Sequence
                                  -- NULL sorts LAST under DESC in SQL Server, so a version with no
                                  -- readable receivedDate loses to any version that has one, and
                                  -- Sequence decides among versions that all lack it.
                                  , Standing = ROW_NUMBER () OVER (
                                        PARTITION BY HandlerId, SourceType
                                            ORDER BY ReceivedDate DESC, Sequence DESC)
                               FROM @Summary
                              WHERE IsCurrent = 1) AS r
                      WHERE r.Standing = 1) AS w
            ON w.HandlerId  = s.HandlerId
           AND w.SourceType = s.SourceType;

        SELECT @PairCount = COUNT (*)
          FROM (SELECT DISTINCT HandlerId, SourceType FROM @Authoritative) AS p;

        SET @KeyParameters = CONCAT (@KeyParameters, N', Versions=', @VersionCount
                                   , N', Pairs=', @PairCount);

        -- The contradiction, recorded before anything is written. It is derived from @Summaries alone,
        -- so it is known here and survives a later rollback in @Observation.
        --
        -- The Sequence column carries the WINNER, taken from @Authoritative rather than recomputed, so
        -- the row cannot drift out of agreement with what was actually written. ObservedValue lists
        -- every claimant with its receivedDate, because that pairing is what makes the row diagnosable
        -- without a second query: four claimants sharing one date is MDE's duplicate submission, four
        -- claimants with four dates is something else entirely. Dates are business facts, not PII.
        INSERT INTO @Observation (ObservationType, Severity, HandlerId, SourceType, Sequence
                               , JsonPath, ObservedValue, Detail)
        SELECT N'CurrentRecordAmbiguousInSource'
             , N'Error'
             , s.HandlerId
             , s.SourceType
             , MAX (s.WinningSequence)
             , N'$.currentRecord'
             , LEFT (CONCAT (N'sequences claiming current (sequence@receivedDate): '
                           , STRING_AGG (CONCAT (s.Sequence, N'@'
                                               , COALESCE (CONVERT (NVARCHAR (10), s.ReceivedDate, 23)
                                                         , N'(none)')), N', ')
                               WITHIN GROUP (ORDER BY s.Sequence)), 400)
             , CONCAT (N'EPA''s summary list named ', COUNT (*), N' current versions of this handler '
                     , N'and source type, and at most one can be current. The mirror does not '
                     , N'reproduce the contradiction: sequence ', MAX (s.WinningSequence), N' -- the '
                     , N'latest RECEIVED of the versions that claimed it, with sequence breaking a tie '
                     , N'-- was taken as current and the others were set to 0, because '
                     , N'dbo.vwHandlerSource filters CurrentRecord = 1 and would otherwise return this '
                     , N'handler more than once to the web app and to the Phase 2 ETS migration. '
                     , N'Ordering by sequence alone would be wrong here: sequence is an insertion '
                     , N'order, so where MDE submitted one form to EPA several times the highest '
                     , N'sequence is the last DUPLICATE rather than the newest record. Re-fetch '
                     , N'/api/v1/hd/sources/summaries?handlerId=', s.HandlerId, N' to confirm; if EPA '
                     , N'still reports two, it is a defect in EPA''s data and this row is the evidence '
                     , N'-- and if the claimants share a receivedDate, MDE sent the same submission '
                     , N'more than once and MDE can have EPA retire the copies.')
          FROM (SELECT HandlerId  = CAST (s.HandlerId  AS NVARCHAR (12))
                     , SourceType = CAST (s.SourceType AS NVARCHAR (1))
                     , s.Sequence
                     , s.ReceivedDate
                     , WinningSequence = a.Sequence
                  FROM @Summary AS s
                  JOIN @Authoritative AS a
                    ON a.HandlerId  = CAST (s.HandlerId  AS NVARCHAR (12))
                   AND a.SourceType = CAST (s.SourceType AS NVARCHAR (1))
                   AND a.IsCurrent  = 1
                 WHERE s.IsCurrent = 1) AS s
         GROUP BY s.HandlerId, s.SourceType
        HAVING COUNT (*) > 1;

        SET @AmbiguousSource = @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 5. The writes.
        -- -----------------------------------------------------------------------------------------
        BEGIN TRANSACTION;

        -- 5a. Every version EPA listed takes the flag EPA gave it. IS DISTINCT FROM, not <>: the
        --     column is nullable and NULL is the ordinary state of a freshly inserted version, so <>
        --     would match nothing on exactly the rows that most need setting.
        UPDATE h
           SET CurrentRecord         = a.IsCurrent
             , auditModifiedBy       = ORIGINAL_LOGIN ()
             , auditModifiedDateUtc  = @NowUtc
          FROM dbo.HandlerSource AS h
          JOIN @Authoritative    AS a
            ON a.HandlerId  = h.HandlerId
           AND a.SourceType = h.SourceType
           AND a.Sequence   = h.Sequence
         WHERE h.IsDeleted = 0
           AND h.CurrentRecord IS DISTINCT FROM a.IsCurrent;

        SET @SetFromList = @@ROWCOUNT;

        -- 5b. A live version of a mentioned pair that the list does not name cannot be current: the
        --     authority on currency did not list it. Scoped to the pairs @Summaries mentions, so a
        --     handler this call says nothing about is left entirely alone.
        --
        --     Recorded BEFORE the demotion, and the order is the whole point: ObservedValue reports
        --     the flag as it stood when the version was found missing from EPA's list. Recorded after,
        --     it would read "CurrentRecord was 0" on every row -- including the rows this statement
        --     had just changed from 1 -- and the observation would be describing its own effect. It is
        --     recorded whether or not the flag actually changes, because the finding is that the
        --     version exists here and not in EPA's list, which is true even when it was already 0.
        INSERT INTO @Observation (ObservationType, Severity, HandlerId, SourceType, Sequence
                               , JsonPath, ObservedValue, Detail)
        SELECT N'VersionNotInSourceSummary'
             , N'Warning'
             , h.HandlerId
             , h.SourceType
             , h.Sequence
             , NULL
             , LEFT (CONCAT (N'CurrentRecord was '
                           , COALESCE (CAST (h.CurrentRecord AS NVARCHAR (1)), N'NULL')), 400)
             , N'This version is live in dbo.HandlerSource and EPA''s summary list for the handler '
             + N'does not name it, so this reconcile sets it to CurrentRecord = 0 -- it cannot be '
             + N'current if the authority on currency does not list it. Two things produce this, and '
             + N'they need different responses: EPA withdrew the version, in which case it belongs to '
             + N'the retention pass (dbo.uspSoftDeleteHandlerSourceSet), or @Summaries was passed a '
             + N'FILTERED list, in which case the caller is violating this procedure''s one hard '
             + N'contract and every other version of the handler was demoted too.'
          FROM dbo.HandlerSource AS h
          JOIN (SELECT DISTINCT HandlerId, SourceType FROM @Authoritative) AS p
            ON p.HandlerId  = h.HandlerId
           AND p.SourceType = h.SourceType
         WHERE h.IsDeleted = 0
           AND NOT EXISTS (SELECT 1
                             FROM @Authoritative AS a
                            WHERE a.HandlerId  = h.HandlerId
                              AND a.SourceType = h.SourceType
                              AND a.Sequence   = h.Sequence);

        UPDATE h
           SET CurrentRecord         = 0
             , auditModifiedBy       = ORIGINAL_LOGIN ()
             , auditModifiedDateUtc  = @NowUtc
          FROM dbo.HandlerSource AS h
          JOIN (SELECT DISTINCT HandlerId, SourceType FROM @Authoritative) AS p
            ON p.HandlerId  = h.HandlerId
           AND p.SourceType = h.SourceType
         WHERE h.IsDeleted = 0
           AND h.CurrentRecord IS DISTINCT FROM 0
           AND NOT EXISTS (SELECT 1
                             FROM @Authoritative AS a
                            WHERE a.HandlerId  = h.HandlerId
                              AND a.SourceType = h.SourceType
                              AND a.Sequence   = h.Sequence);

        SET @DemotedUnlisted = @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 6. The invariant, measured against the TABLE, at the TWO GRAINS its two halves actually
        --    have. Until [R47] both halves were asked at the (HandlerId, SourceType) grain from one
        --    aggregate, and the "none" half was wrong there -- see the [R47] paragraph in the header
        --    for the measurement, and 6b below for why the grain is the whole finding.
        --
        --    SUM (CASE ...), not COUNT (CASE ...) -- COUNT over a CASE with no ELSE raises warning
        --    8153 for the NULLs it then ignores.
        -- -----------------------------------------------------------------------------------------

        -- 6a. TOO MANY, per (HandlerId, SourceType). This grain is correct and stays correct: two
        --     live versions of ONE pair both flagged current is a contradiction no reading of EPA's
        --     data excuses, because dbo.vwHandlerSource filters CurrentRecord = 1 and would return
        --     that pair twice. Scoped to the pairs @Summaries mentions, like the writes above.
        INSERT INTO @Observation (ObservationType, Severity, HandlerId, SourceType, Sequence
                               , JsonPath, ObservedValue, Detail)
        SELECT N'CurrentRecordAmbiguousInMirror'
             , N'Error'
             , g.HandlerId
             , g.SourceType
             , NULL
             , NULL
             , LEFT (CONCAT (g.CurrentVersions, N' of ', g.LiveVersions
                           , N' live versions have CurrentRecord = 1'), 400)
             , N'More than one live version of this handler and source type is flagged current '
             + N'AFTER the reconcile, which the reconcile itself cannot cause: the list it applied '
             + N'held at most one current version per pair by construction. So something else is '
             + N'true -- another session set the flag inside this transaction''s window, or a live '
             + N'version of this pair exists that was outside the reconcile''s reach. '
             + N'dbo.vwHandlerSource filters CurrentRecord = 1 and is now returning this handler '
             + N'more than once, so this needs a person: re-fetch the handler''s summary list and '
             + N'reconcile it alone.'
          FROM (SELECT h.HandlerId
                     , h.SourceType
                     , LiveVersions    = COUNT (*)
                     , CurrentVersions = SUM (CASE WHEN h.CurrentRecord = 1 THEN 1 ELSE 0 END)
                  FROM dbo.HandlerSource AS h
                  JOIN (SELECT DISTINCT HandlerId, SourceType FROM @Authoritative) AS p
                    ON p.HandlerId  = h.HandlerId
                   AND p.SourceType = h.SourceType
                 WHERE h.IsDeleted = 0
                 GROUP BY h.HandlerId, h.SourceType) AS g
         WHERE g.CurrentVersions > 1;

        -- 6b. NONE, per HandlerId -- NOT per (HandlerId, SourceType), and the difference is the
        --     entire point of [R47]. CurrentRecord is EPA's answer to "which version is this
        --     handler's live record", and EPA answers it ONCE PER HANDLER rather than once per
        --     source type. A source type carrying no current version is the NORMAL case for most
        --     source types on most handlers: 'I' is Implementer, meaning the data set originated
        --     with MDE rather than with the site, and it legitimately holds the current-record flag
        --     instead of the site's own 'N' notification -- while 'R' (Biennial Report) held a
        --     current on 10 of 1,605 mirrored pairs and was not defective the other 1,595 times.
        --
        --     THE JOIN IS ON HandlerId ALONE, AND THE AGGREGATE DELIBERATELY READS SOURCE TYPES
        --     @Summaries DID NOT MENTION. That is not scope creep, it is the question: a handler
        --     whose current version sits under a source type this call said nothing about is still
        --     in dbo.vwHandlerSource, so reporting it as missing would be false. The HANDLERS are
        --     still limited to the ones @Summaries names, so a call about one handler cannot sweep
        --     the state.
        --
        --     The assertion is AT LEAST ONE and never EXACTLY ONE. 331 handlers in the mirror carry
        --     two or three currents across different source types -- B+N, D+N, I+N, B+D, D+I and
        --     B+D+N combinations -- and every one of them is a legitimate EPA state. "Exactly one"
        --     per handler would replace the false alarm this fixes with a new one 331 rows long. The
        --     within-pair contradiction is 6a's job and 6a keeps it, and there the mirror has zero
        --     violations -- no handler anywhere carries two currents inside ONE source type.
        INSERT INTO @Observation (ObservationType, Severity, HandlerId, SourceType, Sequence
                               , JsonPath, ObservedValue, Detail)
        SELECT N'CurrentRecordMissingInMirror'
             , N'Warning'
             , g.HandlerId
             -- SourceType, Sequence, JsonPath all NULL: the finding is about the HANDLER. Naming
             -- one of its source types here would re-assert at the grain 6b exists to abandon.
             , NULL
             , NULL
             , NULL
             , LEFT (CONCAT (N'0 of ', g.LiveVersions, N' live version(s) across '
                           , g.LiveSourceTypes, N' source type(s) have CurrentRecord = 1'), 400)
             , N'No live version of this handler is flagged current under ANY source type, so the '
             + N'handler is in dbo.HandlerSource and absent from dbo.vwHandlerSource -- '
             + N'indistinguishable, to every default read and to the Phase 2 ETS migration, from a '
             + N'handler that was never loaded. This is asked per handler and not per source type, '
             + N'because EPA flags currency once per handler: a handler current under any one of '
             + N'its source types is correctly current and is NOT reported here. EPA is entitled to '
             + N'report a version list with nothing current anywhere, so this is a Warning and the '
             + N'load continues. If it appears for many handlers at once, suspect the caller sent '
             + N'currentRecord as something other than a JSON boolean, or sent a filtered list.'
          FROM (SELECT h.HandlerId
                     , LiveVersions    = COUNT (*)
                     , LiveSourceTypes = COUNT (DISTINCT h.SourceType)
                     , CurrentVersions = SUM (CASE WHEN h.CurrentRecord = 1 THEN 1 ELSE 0 END)
                  FROM dbo.HandlerSource AS h
                  JOIN (SELECT DISTINCT HandlerId FROM @Authoritative) AS p
                    ON p.HandlerId = h.HandlerId
                 WHERE h.IsDeleted = 0
                 GROUP BY h.HandlerId) AS g
         WHERE g.CurrentVersions = 0;

        -- The successful-path flush. Inside the transaction on purpose: on this path the corrections
        -- described by these rows are about to commit, so the observations and the corrections stand
        -- or fall together.
        INSERT INTO logs.DataQualityObservation
              (LoadRunId, HandlerLoadStatusId, ObservationType, Severity, HandlerId, SourceType
             , Sequence, TableName, ColumnName, JsonPath, ObservedValue, Detail, ObservedDateUtc)
        SELECT @LoadRunId
             -- [R47] The link the table was built with and nothing ever wrote. A SCALAR subquery
             -- rather than a join, so it cannot multiply the flush: UX_logs_HandlerLoadStatus_Natural
             -- is unique on exactly these four columns WHERE IsDeleted = 0, so this returns one row
             -- or none. None is the ordinary case for an observation whose Sequence is NULL -- 6b's
             -- handler-grained rows name no version, so there is no one status row to point at, and
             -- the column stays NULL there by construction rather than by omission.
             , (SELECT s.HandlerLoadStatusId
                  FROM logs.HandlerLoadStatus AS s
                 WHERE s.LoadRunId  = @LoadRunId
                   AND s.HandlerId  = o.HandlerId
                   AND s.SourceType = o.SourceType
                   AND s.Sequence   = o.Sequence
                   AND s.IsDeleted  = 0)
             , o.ObservationType
             , o.Severity
             , o.HandlerId
             , o.SourceType
             , o.Sequence
             , N'dbo.HandlerSource'
             , N'CurrentRecord'
             , o.JsonPath
             , o.ObservedValue
             , o.Detail
             , @NowUtc
          FROM @Observation AS o
         ORDER BY o.Ordinal;

        SET @Observations = @@ROWCOUNT;
        SET @RowsAffected = @SetFromList + @DemotedUnlisted;

        SET @Comments = CONCAT (N'versions=', @VersionCount
                              , N', pairs=', @PairCount
                              , N', flagsSet=', @SetFromList
                              , N', demotedUnlisted=', @DemotedUnlisted
                              , N', ambiguousInSource=', @AmbiguousSource
                              , N', observations=', @Observations);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        SET @Committed = 1;

        -- Completion. Deliberately after the COMMIT; see the template header for what that costs. A
        -- retry provoked by a failure here is safe: both UPDATEs are guarded by IS DISTINCT FROM and
        -- find nothing the second time, and the observations are re-derived from a table that now
        -- satisfies the invariant, so the second run records none.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc           = @EndTimeUtc
                 , ElapsedMilliseconds  = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                     , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful           = 1
                 , KeyParameters        = @KeyParameters
                 , Comments             = @Comments
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so
        -- capture them before doing anything else -- including before the rollback.
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- One test, not two: XACT_ABORT ON makes XACT_STATE () = -1 the common case, and -1 and 1
        -- both need the same unqualified rollback.
        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        -- G35's flush. @Observation survived the rollback and holds what the reconcile noticed before
        -- it failed -- the ambiguous source lists in particular, which are a property of EPA's data
        -- and are worth keeping whether or not this run got to act on them. Each Detail gets a
        -- sentence appended saying the correction never happened, because a row that describes a
        -- correction which was rolled back is worse than no row.
        --
        -- Guarded by @Committed: a failure in the completion UPDATE arrives here with the
        -- observations already committed and nothing rolled back, and flushing again would double
        -- every one of them.
        BEGIN TRY
            IF @Committed = 0
            BEGIN
                INSERT INTO logs.DataQualityObservation
                      (LoadRunId, HandlerLoadStatusId, ObservationType, Severity, HandlerId
                     , SourceType, Sequence, TableName, ColumnName, JsonPath, ObservedValue, Detail
                     , ObservedDateUtc)
                SELECT @LoadRunId
                     -- [R47], as on the successful path. The rows in logs.HandlerLoadStatus were
                     -- written by script 520 in its OWN transaction, so the rollback that brought us
                     -- here did not take them and the link still resolves.
                     , (SELECT s.HandlerLoadStatusId
                          FROM logs.HandlerLoadStatus AS s
                         WHERE s.LoadRunId  = @LoadRunId
                           AND s.HandlerId  = o.HandlerId
                           AND s.SourceType = o.SourceType
                           AND s.Sequence   = o.Sequence
                           AND s.IsDeleted  = 0)
                     , o.ObservationType
                     , o.Severity
                     , o.HandlerId
                     , o.SourceType
                     , o.Sequence
                     , N'dbo.HandlerSource'
                     , N'CurrentRecord'
                     , o.JsonPath
                     , o.ObservedValue
                     , LEFT (CONCAT (o.Detail
                                   , N' NOTE: the reconcile that found this then FAILED and rolled '
                                   , N'back, so nothing described above was corrected. The finding '
                                   , N'itself stands; the correction did not happen. See '
                                   , N'logs.ExecutionLog for the error.'), 4000)
                     , @NowUtc
                  FROM @Observation AS o
                 ORDER BY o.Ordinal;

                SET @Observations = @@ROWCOUNT;
            END;
        END TRY
        BEGIN CATCH
            -- Swallowed on purpose. These rows are secondary to the error below, and a failure
            -- writing them must not replace the error the caller needs to see. The count is set to
            -- NULL rather than left at whatever it was, so the caller cannot read a stale number as
            -- a report of rows that were written.
            SET @Observations = NULL;
        END CATCH;

        SET @ContextMessage = CONCAT (N'versions=', @VersionCount
                                    , N', pairs=', @PairCount
                                    , N', observationsHeld='
                                    , (SELECT COUNT (*) FROM @Observation)
                                    , CASE WHEN @Committed = 1
                                           THEN N', the reconcile COMMITTED and the failure is in the '
                                              + N'completion update; the flags are set.'
                                           ELSE N', no flag was changed (rolled back).' END);

        -- The rollback may have destroyed the row logs.uspStartExecutionLogging wrote, and only when
        -- this procedure was called inside a transaction that was ALREADY open. Put it back with the
        -- ORIGINAL @StartTimeUtc, or the only executions never recorded would be the failures.
        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        -- Swallows everything by design, so this call cannot mask the error below it.
        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and
        -- the loader could no longer tell a deadlock (1205, retry) from a rejected set.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspReconcileCurrentRecord'
    , @Description = N'Sets dbo.HandlerSource.CurrentRecord across every version of a handler from EPA''s own summary list, passed as a JSON @Summaries array of handlerId/sourceType/sequence/currentRecord, and asserts the invariant dbo.vwHandlerSource depends on: at most one CurrentRecord = 1 per (HandlerId, SourceType) among live rows. It exists because the flag is a property of a handler''s whole version list but is delivered as a property of one version -- when EPA adds version 4, nothing in version 4''s payload says version 3 stopped being current -- so per Phase1-Analysis.md a changed handler''s full summary list is re-fetched from /api/v1/hd/sources/summaries and every version''s flag reconciled from that authoritative list rather than inferred. ONE HARD CONTRACT: @Summaries must be the COMPLETE version list for every pair it mentions, because a live version of a mentioned pair that the list does not name is set to CurrentRecord = 0 and reported as VersionNotInSourceSummary; a filtered list would demote a handler''s whole history. When EPA''s list itself names two current versions the mirror deliberately does NOT reproduce the contradiction -- an Error observation records the pair and every sequence that claimed current with its receivedDate, and the latest RECEIVED claimant is taken as current, sequence breaking a tie -- because the contradiction would otherwise propagate through dbo.vwHandlerSource into the web app and the Phase 2 ETS migration. [R43] The tie-break was highest sequence until 2026-09-07 and that was wrong: sequence is an insertion order rather than a chronology, so where MDE submitted one form to EPA repeatedly the highest sequence is the last duplicate rather than the newest record (MDD985416569, found by F1''s UI comparison). $.receivedDate is TRY_CAST and NOT validated, so a caller that omits it falls back to sequence and gets the previous behaviour rather than a refusal. [R47] A HANDLER left with no current version anywhere is a Warning, since the handler then vanishes from every default read -- and that check is asked PER HANDLER, where until 2026-09-08 it was asked per (HandlerId, SourceType) and was meaningless there. EPA flags currency once per handler, not once per source type: ''I'' is Implementer, meaning the data set originated with MDE rather than with the site, so ''I'' legitimately carries the flag instead of the site''s own ''N'' notification, and ''R'' held a current on 10 of 1,605 mirrored pairs without being defective the other 1,595 times. At the pair grain the check raised 10,098 Warnings in one load of which 9,867 belonged to a handler that WAS current under another source type; re-grained to HandlerId the same data reports 185 handlers, and all 185 are genuinely absent from dbo.vwHandlerSource. The assertion is AT LEAST ONE per handler and never EXACTLY ONE, because 331 mirrored handlers legitimately carry two or three currents across different source types, while ZERO carry two inside one source type -- which is why 6a keeps the pair grain and 6b does not. Two currents within ONE pair remains an Error at the pair grain, which is the half of the invariant that is always wrong. The post-write check runs against the table rather than against @Summaries, so it can actually fail: after the tie-break the source set cannot name two currents, and a violation found afterwards means a concurrent writer or an uncovered version. G35 applies: observations accumulate in a table variable and are written inside the transaction on the successful path, or flushed after the rollback in the CATCH with a sentence appended saying the correction never happened. [R47] Both flushes resolve logs.DataQualityObservation.HandlerLoadStatusId from UX_logs_HandlerLoadStatus_Natural by scalar subquery, which no writer in this database had ever populated -- the column, its foreign key and its index all existed for a value that was NULL on every one of 10,507 rows. It resolves only for observations that name a Sequence, so the handler-grained rows leave it NULL by construction rather than by omission. Both UPDATEs use IS DISTINCT FROM because CurrentRecord is nullable and NULL is the ordinary state of a freshly inserted version, and a reconcile that finds nothing to change writes nothing and moves no auditModifiedDateUtc. An empty array is a no-op, not an error. The run must exist, be live, and still be Running. @Summaries is excluded from @KeyParameters by name. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. The loader reconciles the flag; the monitoring web app reads it through dbo.vwHandlerSource
-- and dbo.vwHandlerSourceHistory and must never be able to set it.
--
-- Ownership chaining carries the UPDATEs to dbo.HandlerSource, the read of logs.LoadRun, the inserts
-- into logs.DataQualityObservation, and the chained inserts into logs.ExecutionLog through this single
-- grant, so the loader login holds no direct permission on any of the four tables (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileCurrentRecord TO RCRAInfoLoaderRole;
END;
GO

PRINT N'521: dbo.uspReconcileCurrentRecord created or altered, EXECUTE granted to the loader role.';
GO
