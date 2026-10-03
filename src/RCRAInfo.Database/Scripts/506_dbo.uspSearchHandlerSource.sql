-- SET XACT_ABORT ON sits ABOVE the header block deliberately. The GO on the next line ends the batch, and
-- sys.sql_modules stores only the batch that contains CREATE -- so a header placed AFTER this GO is invisible to
-- anyone reading the procedure out of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as
-- CREATE", which is where a maintainer actually reads it.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSearchHandlerSource
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

The search box behind the monitoring web app's handler screens. One free-text term, matched against the handler's
identifier, its name and its city, ranked by how well it matched, and returned one page at a time with the filtered
total carried on every row as TotalRows.

It is the last of the four DA4 reads over the handler mirror and the twentieth and last procedure of Workstream DA. It
is also the procedure five earlier headers have been pointing at: 501, 503, 504 and 505 each record a standing rule for
whenever a free-text search parameter finally appears, and this is the file where that rule has to be kept rather than
restated. See WHAT @KeyParameters MAY CONTAIN below, and build/check_search_term_privacy.py, which turns it into
something a guardrail run can fail on.

========================================================================================================================
Requirements and Key Dependencies:

dbo.vwHandlerSource, which is dbo.vwHandlerSourceHistory (IsDeleted = 0) with CurrentRecord = 1 added. Never
dbo.HandlerSource directly: those two views are the ONLY structural enforcement of AR7 now that EF Core is reduced to
calling stored procedures, and a procedure that reads the base table is a procedure that can forget the filter. A
search over history would return the same handler five times over, once per version, which is not what a search box is
for -- dbo.uspGetHandlerSourceHistoryPage is where versions are answered.

IX_dbo_HandlerSource_Grid, from script 390, for SPEED ONLY. No new index is added for this procedure, and the reason is
measured rather than assumed -- see MEASURED, THE INDEX below. This procedure returns the same rows on a database where
script 390 has never been applied.

logs.uspRecordExecutionError, for the AR8 instrumentation block.

build/check_search_term_privacy.py, which is a DEPENDENCY IN THE OTHER DIRECTION and is named here so that a rename
breaks something noisy. It asserts that no statement in this procedure which builds @KeyParameters, @ContextMessage or
@DynamicSql, and no message this procedure raises, mentions the term or anything derived from it -- in the file AND in
the deployed module.

EXECUTE is granted to RCRAInfoMonitorRole only. The console app never searches: it merges what the API hands it and
finds its own starting point through config.uspGetLoadWatermark. The asymmetry is asserted by
build/check_permission_posture.py.

========================================================================================================================
Notes:

THE SEARCH TERM IS NOT LOGGED, AND THAT IS THE WHOLE REASON THIS PARAGRAPH IS FIRST. Every other procedure in this
database logs its parameters, because every other parameter is an identifier, a code, a date or a count. A search term
is none of those: it is whatever a person typed, into a search box, over a database of regulated-entity records that
carries contact names, telephone numbers, email addresses and home-shaped mailing addresses. Someone searching for a
person's surname to see which handlers they are attached to writes that surname into logs.ExecutionLog for the retention
period if the term is logged -- and logs.ExecutionLog is readable by the monitoring web app, so the term would come back
out on a screen. Three facts ABOUT the term are logged instead, and each is a fact the term cannot be recovered from:
its length, whether it was identifier-shaped, and how many rows it matched.

WHAT @KeyParameters MAY CONTAIN. Skip, Take, TermLength, IdShaped and nothing else. @SearchTerm is EXCLUDED BY NAME, as
are @Term, @ExactTerm, @PrefixPattern and @ContainsPattern, which are the term after trimming and after escaping and
are the same secret in another shape. From MDE's own template: do NOT include parameters such as passwords and
Personally Identifiable Information. TermLength is a count; IdShaped is a BIT saying which of two branches ran, which is
the single most useful thing to know when someone reports that a search returned nothing. A third derived BIT,
@TermPresent, exists only so the statement that builds @KeyParameters never has to name the term even to test it for
NULL -- see the comment on its declaration. The rule is then exact and has no exceptions, which is what makes it
enforceable.

NO REFUSAL MESSAGE REPEATS THE TERM EITHER, AND THAT IS THE PART THAT IS EASY TO GET WRONG. A helpful error message is
the obvious place a term leaks: "no results for 'smith'" is exactly the sentence a developer writes without thinking,
and ERROR_MESSAGE () is captured into logs.ExecutionLog.ErrorMessage by the CATCH block below. So the two refusals here
name the PARAMETER and state the RULE, and the message says in as many words that the term is deliberately not quoted.
The same applies to the CATCH: nothing read from the term or from the view is copied into @ContextMessage, not even to
make a failure easier to diagnose.

ERROR-ONLY INSTRUMENTATION, WHICH IS THE POLICY 500 PUT TO MDE AND THIS PROCEDURE FOLLOWS. Write procedures are
instrumented always; read procedures opt in. logs.uspGetLoadRunPage keeps a successful-path row as the witness that a
read shape survives the template at all; every other read records only failures. A row per search request would be a row
per keystroke if the web app ever adds type-ahead.

THAT MAKES THE TRY/CATCH THE ONLY THING RECORDING ANYTHING, WHICH IS THE POINT. The defect this project is guarding
against is a procedure that only SELECTs, calls something that fails inside the SELECT, and records nothing because it
wrote no row on the way past. Every path out of the TRY block below either returns rows or reaches
logs.uspRecordExecutionError.

NO TRANSACTION, AND THEREFORE NO ROLLBACK. A procedure rolls back only what it opened; this one opens nothing, so any
transaction live in the CATCH belongs to the caller and rolling it back would discard work this procedure never did.
ROLLBACK is also illegal inside INSERT ... EXEC (error 8004), where it would replace the error being reported and abort
the CATCH before the failure could be recorded. build/check_stored_headers.py enforces the rule against the DEPLOYED
module: a module containing ROLLBACK must also contain BEGIN TRANSACTION.

--- WHAT IS SEARCHED, AND WHAT DELIBERATELY IS NOT ---------------------------------------------------------------
THREE COLUMNS ARE SEARCHED: HandlerId, HandlerName and SiteLocationCity. Those are the three things a person knows
about a handler before they have found it -- the EPA identifier they were given, the business name they were told, or
the town it is in.

CONTACT COLUMNS ARE NOT SEARCHABLE, AND THAT IS A STRONGER STATEMENT THAN "NOT PROJECTED". 503 and 504 exclude
ContactFirstName, ContactLastName, ContactPhone, ContactEmail and the contact address from their projections as PII, and
505 returns them to a user who has navigated to one handler. This procedure excludes them from the PREDICATE as well:
searching by contact surname would turn a handler search into a people search -- type a name, get every regulated
business that person is attached to -- and no monitoring screen needs that. SrcUpdatedBy is excluded for the same
reason: it identifies an EPA user.

SiteLocationAddress1 AND SiteLocationZip ARE PROJECTED BUT NOT SEARCHED, WHICH LOOKS INCONSISTENT AND IS NOT. They are
in the projection because two handlers with similar names are told apart by their street address, and a result list
that cannot be told apart is not a result. They are out of the predicate because of the index: the three searched
columns are exactly the columns IX_dbo_HandlerSource_Grid already carries, so this procedure needs no index of its own,
and adding a fourth searched column would push the scan back onto the 218-column clustered index -- measured at 66,776
logical reads against 1,826. If MDE asks for address search later, the honest change is an additive INCLUDE on script
390's index in a new 3xx script, not a fourth predicate here.

--- THE TERM, AND WHAT IS DONE TO IT -----------------------------------------------------------------------------
@SearchTerm IS NVARCHAR (200) AND THE WIDEST COLUMN IT IS COMPARED AGAINST IS 80. A parameter that is trimmed,
normalised or pattern-checked must be declared WIDER than its target, because the truncation happens at the CALL
BOUNDARY -- before the body runs and before any validation can see it. That is not a hypothetical: 504 was written with
@HandlerId NVARCHAR (12), and a caller passing N'  MDPROBE05041 ' had it silently cut to N'  MDPROBE054' before the
TRIM, so the procedure validated a string the caller never sent.

A TERM LONGER THAN 80 CHARACTERS IS NOT REFUSED, AND THAT IS A DELIBERATE DIFFERENCE FROM 503's INVERTED DATE RANGE. It
cannot match a row -- the widest searched column is HandlerName at 80 -- so by 503's own argument it looks like a caller
defect worth refusing. The difference is where the boundary lives: an inverted date range is arithmetic and will still be
arithmetic in ten years, whereas 80 is a column width in a GENERATED script, and a refusal keyed to it would go stale in
silence the day EPA widens the field. An empty result set is the correct answer at any width.

THE TERM IS ESCAPED FOR LIKE, AND THE ORDER OF THE FOUR REPLACEMENTS MATTERS. %, _ and [ are LIKE metacharacters, so a
user typing a % into the search box would otherwise match every handler in Maryland and a [ would produce an invalid
pattern rather than a search for a bracket. The escape character is \, and it is replaced FIRST -- escaping % before \
would then re-escape the backslash that had just been inserted, and the pattern would be wrong in a way that only shows
up for terms containing both characters.

AN IDENTIFIER-SHAPED TERM IS TREATED AS AN IDENTIFIER, NOT AS TEXT, AND THIS IS THE ONE BEHAVIOURAL JUDGEMENT IN THIS
PROCEDURE. A term of nothing but letters and digits that starts with two letters followed by a digit -- MD0000123456,
MD00000123, AB1 -- is a handler ID or the start of one, and it is matched against HandlerId by PREFIX only; nothing is
matched against the name or the city. Any term containing a space, a comma, an ampersand or a hyphen is text, so every
multi-word business name takes the text branch, which is what makes the rule safe in practice. What it costs is the
term that is alphanumeric AND meant as a name fragment: searching for AB1 hoping to find "AB1 SOLVENTS INC" returns
nothing. The MatchRank and MatchField columns tell the web app which branch ran, so a "no matches -- searched by
identifier" caption is available to it.

--- MEASURED, THE INDEX -----------------------------------------------------------------------------------------
NO NEW INDEX, AND NO INDEX CAN HELP THE TEXT BRANCH ANYWAY. A leading-wildcard LIKE cannot seek: '%ACME%' has no
prefix to position on, so the best any B-tree can do is scan. What an index can do is make the scan narrow, and script
390's already does -- it is keyed on HandlerId and INCLUDEs HandlerName and SiteLocationCity, filtered to exactly
dbo.vwHandlerSource's own predicate. Measured at a stated assumed 400,000 rows with 100,000 current
(build/tmp/measure506_search.probesql), logical reads for the count and the page:

    no covering index, any term                 66,776 + 66,776
    IX_dbo_HandlerSource_Grid, term ACME         1,826 + 1,826       25,000 matches
    IX_dbo_HandlerSource_Grid, term BALTIMORE    1,826 + 1,826       12,500 matches
    IX_dbo_HandlerSource_Grid, identifier-shaped 1,826 +     6          100 matches

The scan cost is FLAT in the number of matches, which is the property to expect from a scan and the reason a selective
term is not faster than a broad one on the text branch. A full-text index is the tool that would change that, and it is
not proposed here: it is a separate feature to install, populate and keep in step, and 1,826 logical reads is around 15
milliseconds for a human who has just pressed Enter.

--- MEASURED, AND THIS ONE WAS A SURPRISE -----------------------------------------------------------------------
`SELECT @Variable = COUNT (*)` SILENTLY GIVES UP OPTION (RECOMPILE)'s PARAMETER EMBEDDING. That is why the count below
is an INSERT into a table variable rather than the obvious assignment, and it is worth the four extra lines because the
difference is 239-fold. Measured, same fixture, same query, same hint, identifier-shaped term:

    SELECT @TotalRows = COUNT (*) ... OPTION (RECOMPILE)          1,196 logical reads
    SELECT COUNT (*)              ... OPTION (RECOMPILE)              5 logical reads
    INSERT INTO @Counted (Value) SELECT COUNT (*) ... RECOMPILE       5 logical reads
    SET @TotalRows = (SELECT COUNT (*) ...)                       1,196 logical reads

The mechanism this procedure relies on is the one 503 measured: under OPTION (RECOMPILE) a parameter's VALUE is embedded
as a constant at compile time, so `@IdShaped = 1 AND v.HandlerId LIKE @PrefixPattern` becomes a bare seekable prefix and
the other OR group folds to false and is eliminated. What was not known is that the embedding does not happen when the
statement assigns its result to a variable -- the plan keeps @IdShaped as a parameter reference, neither OR group can be
eliminated, and the seek is unreachable. The page query never had the problem, because INSERT INTO @Hits ... SELECT is
not a variable assignment; only the count did, and the count is the statement where the two differed.

503 IS NOT CHANGED TO MATCH, AND THAT IS DELIBERATE. Its count has the same shape and therefore the same missed
embedding, so a dbo.uspGetHandlerSourcePage call that supplies @HandlerId scans the grid index where it could seek. No
claim in 503's header is falsified by that -- it makes the folding claim about its ORDER BY, which is an INSERT ...
SELECT and does fold -- and rewriting a deployed, probed, measured procedure to save milliseconds on a filter the grid
applies occasionally is churn of exactly the kind 503's own header declines for 500 and 501. This note is the argument
for revisiting it if F2 measures the grid as slow.

OPTION (RECOMPILE) IS THEREFORE LOAD-BEARING HERE, NOT HOUSE STYLE. 505 does not carry it, because one row by surrogate
key is one plan. This procedure carries it on both statements: without the embedding, @IdShaped stays a parameter, the
identifier branch loses its seek, and the measured 6-read page becomes 1,826.

--- THE REST ------------------------------------------------------------------------------------------------------
THE RESULT IS RANKED, AND THE RANK IS THE ANSWER, WHICH IS WHY THERE IS NO @SortBy. 503 offers six sort columns and pays
fourteen CASE expressions for them. A search does not want that: the point of a search is that the best match is first,
and a search result re-sorted by handler ID is dbo.uspGetHandlerSourcePage with @HandlerId supplied. MatchRank runs 1 to
7 -- exact identifier, identifier prefix, identifier contains, name prefix, name contains, city prefix, city contains --
and MatchField collapses it to which of the three columns matched, for a caption. Ranks 1 and 2 are the only ones
reachable on the identifier branch; 3 to 7 are the only ones reachable on the text branch.

THE TIEBREAKER IS NOT OPTIONAL. The ORDER BY is MatchRank, then HandlerName, then HandlerSourceId. Thousands of rows
share a MatchRank and plenty share a name, so paging without a unique final key lets the engine return a row on page 1
and again on page 2 while another appears on neither -- silently, only under load, because the order of tied rows is
undefined and the plan may change between two calls.

A ONE-CHARACTER TERM IS REFUSED, AND THE COST IS NOT THE REASON. Measured, it costs the same 1,826-read scan as any
other term. What it RETURNS is the problem: at the assumed volume, '%A%' matched all 100,000 current rows, ranked by a
CASE that cannot separate them, of which the caller sees an arbitrary 25 and a TotalRows of 100,000. That is not an
answer to a question, and a user reading the first 25 would reasonably conclude those are the best matches. Two
characters is a low bar and it is where the bar is; the empty and whitespace-only cases fall out of the same test.

@Take IS CLAMPED TO 1..100 AND NOT TO 503's 1..500. A search result list is read, not scrolled: a term that matches 500
handlers is a term worth retyping. @Skip is floored at 0 and ceilinged well below the INT maximum, because the paging
predicate is `Ordinal <= @Skip + @Take` and a @Skip near that maximum makes the sum overflow and fails the call with an
arithmetic error the caller cannot act on. Both clamps are inside the procedure because the procedure is the boundary
the permission model actually enforces; a check in the web app is a check the caller can skip.

DEEP PAGING DOES NOT GET CHEAPER HERE THE WAY IT DOES IN 503. 503 found that an indexed sort lets the page stop early,
so its cost scales with @Skip and not with the table. MatchRank is computed from the term, so no index can supply the
ordering and the whole matching set is ranked before the first row can be identified, at every @Skip. That is the same
position 503 is in on a sort column its index does not order, and it is acceptable for the same reason: one
human-initiated request.

THERE IS NO @IncludeDeleted AND NO @CurrentRecord FILTER. The view has decided both, and honouring either would mean
reading dbo.HandlerSource directly and restating the AR7 filter here. IsDeleted is not projected either: through this
view it is a provably constant 0, and a column that can only ever say one thing invites a result list to draw a
"deleted" marker that will never light.

AN EMPTY RESULT SET MEANS ZERO, AND THE CALLER CANNOT READ TotalRows FROM A ROW THAT IS NOT THERE. The count is known to
this procedure even when the page is empty, but there is nowhere to put it without inventing a row of NULLs and
corrupting the shape EF Core materializes. An empty result set means "nothing matched"; a non-empty TotalRows with an
empty page means the caller paged past the end and should re-request page 1.

@SearchTerm IS REQUIRED AND HAS NO DEFAULT, SO AN OMITTED TERM IS THE ONE FAILURE THIS PROCEDURE CANNOT RECORD. EXEC
dbo.uspSearchHandlerSource with no arguments fails with error 201 before the body executes, so the TRY/CATCH never runs
and nothing reaches logs.ExecutionLog. That is a property of the T-SQL call boundary rather than a gap in the
instrumentation -- the same boundary 504 and 505 sit on -- and the caller still gets the error. A NULL passed
EXPLICITLY does reach the body, and is refused and recorded.

@ProcName FALLS BACK TO A LITERAL, AND THE LITERAL IS THE BRANCH THE MONITOR ACTUALLY TAKES. OBJECT_NAME (@@PROCID)
returns NULL for a principal denied metadata visibility, and script 050 denies exactly that to both application logins.
Permission to run an object is not permission to see its name, so without the COALESCE every row the monitor wrote would
carry no procedure name -- the one column the monitoring grid groups by. Change both when renaming.

========================================================================================================================
Example Usage and Performance:

-- The ordinary case: someone types part of a business name.
EXEC dbo.uspSearchHandlerSource @SearchTerm = N'ACME PLATING';

-- A pasted handler ID. Takes the identifier branch: a prefix seek, MatchRank 1, and nothing matched on name or city.
EXEC dbo.uspSearchHandlerSource @SearchTerm = N'MD0000123456';

-- A partial ID. Same branch, MatchRank 2.
EXEC dbo.uspSearchHandlerSource @SearchTerm = N'MD00000123', @Take = 50;

-- A town, second page.
EXEC dbo.uspSearchHandlerSource @SearchTerm = N'Hagerstown', @Skip = 25, @Take = 25;

-- Refused, and recorded, without the term appearing anywhere in the log row:
EXEC dbo.uspSearchHandlerSource @SearchTerm = N'A';
EXEC dbo.uspSearchHandlerSource @SearchTerm = N'   ';

Measured at a stated assumed volume of 400,000 rows with 100,000 current, on IX_dbo_HandlerSource_Grid: a text term is
1,826 logical reads for the count and 1,826 for the page regardless of how many rows it matches, around 15 to 30 ms; an
identifier-shaped term is 1,826 for the count and 6 for the page. Without script 390's index both halves are 66,776,
about 215 ms. dbo.HandlerSource holds 50 rows on the development workstation, all soft-deleted, so those figures come
from a temp table built with SELECT TOP (0) * INTO ... FROM dbo.HandlerSource -- real column types, therefore real
widths -- at assumed volumes, because G22 is credential-gated and the true row count is measured in F2. The harness is
build/tmp/measure506_search.probesql and the acceptance probe is build/tmp/da4_searchhandlersource.probesql.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the fourth and last read over the handler
											mirror and the twentieth procedure of Workstream DA. Keeps the standing rule
											501, 503, 504 and 505 record for a free-text search parameter -- the term is
											not logged, and build/check_search_term_privacy.py asserts it. Measured that
											SELECT @Variable = COUNT (*) gives up OPTION (RECOMPILE)'s parameter
											embedding, which is why the count is an INSERT into a table variable.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE dbo.uspSearchHandlerSource
    -- REQUIRED and with no default, so an omitted term is error 201 rather than a search for nothing. See the header:
    -- that is the one failure this procedure cannot record. 200 wide against a widest searched column of 80, because
    -- truncation happens at the CALL boundary, before any validation here could see it.
      @SearchTerm NVARCHAR (200)
    -- Paging only. No @SortBy: the rank IS the order, and no filters, because a search that needs narrowing is
    -- dbo.uspGetHandlerSourcePage. See the header for both.
    , @Skip       INT = 0
    , @Take       INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- ------------------------------------------------------------------------------------------
    -- 1. The AR8 block. @ProcName is COALESCEd because OBJECT_NAME (@@PROCID) returns NULL for a
    --    principal denied metadata visibility, which is what script 050 does to both application
    --    logins. Keep the literal in step with the name above.
    -- ------------------------------------------------------------------------------------------
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                      + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                     , N'[dbo].[uspSearchHandlerSource]')
          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- The term after trimming, and the three patterns built from it. Every one of these is the search term in another
    -- shape, so every one of them is excluded by name from @KeyParameters -- see the header, and
    -- build/check_search_term_privacy.py, which fails if any of them reaches a log column.
    --
    -- The widths are not decoration. Escaping can insert one backslash per character, so a 200-character term becomes
    -- a 400-character pattern; @ContainsPattern adds two more for the surrounding wildcards. A narrower declaration
    -- would truncate the pattern and silently turn a contains-search into a prefix-search.
    DECLARE @Term            NVARCHAR (200) = NULL
          , @Escaped         NVARCHAR (400) = NULL
          , @ExactTerm       NVARCHAR (200) = NULL
          , @PrefixPattern   NVARCHAR (402) = NULL
          , @ContainsPattern NVARCHAR (402) = NULL
          , @TermLength      INT            = NULL
          , @IdShaped        BIT            = 0
          -- @TermLength, @IdShaped and @TermPresent are the three DERIVED facts that ARE logged, and the third one
          -- exists so that the statement building @KeyParameters need not mention the term at all -- not even to test it
          -- for NULL. @TermLength cannot distinguish a NULL term from an empty
          -- one (LEN of both is 0) and the log should say which, so @TermPresent carries that instead of a
          -- `CASE WHEN @Term IS NULL` inside the CONCAT. That keeps the rule
          -- build/check_search_term_privacy.py enforces exact: NO statement that writes a log column names the term or
          -- anything derived from its VALUE. A rule with an "except when it is only a NULL test" clause is a rule
          -- somebody will extend one clause at a time.
          , @TermPresent     BIT            = 0;

    DECLARE @RowsOnPage INT             = 0
          , @TotalRows  INT             = 0
          , @Failure    NVARCHAR (2048) = NULL;

    -- The ranked page as KEYS, not as rows. At most @Take rows come back here and the wide projection is a separate
    -- join, which is the same key-then-project split 503 and 504 use. MatchRank travels with the key because it is
    -- computed from the term and cannot be recovered from the view afterwards.
    DECLARE @Hits TABLE (Ordinal         INT     NOT NULL PRIMARY KEY
                       , HandlerSourceId INT     NOT NULL
                       , MatchRank       TINYINT NOT NULL);

    -- One row, one column, and it exists because of a measurement rather than a preference: SELECT @Variable =
    -- COUNT (*) gives up OPTION (RECOMPILE)'s parameter embedding, so @IdShaped stays a parameter reference, neither
    -- OR group in the predicate can be eliminated, and the identifier branch loses its seek -- 1,196 logical reads
    -- against 5. An INSERT ... SELECT is not a variable assignment and keeps the embedding. See MEASURED, AND THIS ONE
    -- WAS A SURPRISE in the header.
    DECLARE @Counted TABLE (Value INT NOT NULL);

    BEGIN TRY

        -- ------------------------------------------------------------------------------------------
        -- 2. The term. Normalised, validated, then escaped -- in that order, because a validation that
        --    runs after escaping is validating a longer string than the caller sent.
        -- ------------------------------------------------------------------------------------------

        -- Tab, line feed and carriage return as well as the space: a term pasted out of a spreadsheet or an email
        -- carries them, and a leading tab would otherwise make every search return nothing.
        SET @Term = TRIM (NCHAR (9) + NCHAR (10) + NCHAR (13) + N' ' FROM @SearchTerm);
        SET @TermLength = LEN (COALESCE (@Term, N''));
        SET @TermPresent = CASE WHEN @Term IS NULL THEN 0 ELSE 1 END;

        -- @KeyParameters is built HERE, before the first refusal, so a refused call still records its paging arguments
        -- and the two facts about the term. There is no fifth item: the term itself, the trimmed term and the three
        -- patterns are excluded BY NAME. TermLength is a count and IdShaped is a branch indicator, and the term cannot
        -- be recovered from either. See the header.
        SET @KeyParameters = CONCAT (N'Skip=',        COALESCE (CAST (@Skip AS NVARCHAR (11)), N'(null)')
                                   , N', Take=',       COALESCE (CAST (@Take AS NVARCHAR (11)), N'(null)')
                                   , N', TermLength=', CASE WHEN @TermPresent = 0 THEN N'(null)'
                                                            ELSE CAST (@TermLength AS NVARCHAR (11)) END
                                   , N', IdShaped=',   CAST (@IdShaped AS NVARCHAR (1)));

        -- Two characters is the floor, and NULL, empty and whitespace-only all fail the same test. The message names
        -- the parameter and states the rule and does NOT quote what was typed -- ERROR_MESSAGE () is captured into
        -- logs.ExecutionLog.ErrorMessage by the CATCH below, and the monitoring web app can read that column.
        IF @Term IS NULL OR @TermLength < 2
        BEGIN
            SET @Failure = N'@SearchTerm must contain at least 2 characters once leading and trailing whitespace is '
                         + N'removed. A one-character term matches a large fraction of every handler in the mirror '
                         + N'and returns an arbitrary page of it, which reads as "these are the best matches" and is '
                         + N'not an answer. The term is deliberately NOT repeated in this message: error messages are '
                         + N'written to logs.ExecutionLog, which the monitoring web app can read.';
            ;THROW 50000, @Failure, 1;
        END;

        -- Identifier-shaped: letters and digits only, starting with two letters and a digit. Anything with a space, a
        -- comma, an ampersand or a hyphen is text, which is what makes this safe for business names. Character classes
        -- in LIKE rather than a regex function -- REGEXP_LIKE is SQL Server 2025 and this database targets 2022.
        --
        -- The pattern is a literal this file controls, so the term is only ever the LEFT operand here and needs no
        -- escaping yet.
        SET @IdShaped = CASE WHEN @Term NOT LIKE N'%[^0-9A-Za-z]%'
                              AND @Term     LIKE N'[A-Za-z][A-Za-z][0-9]%' THEN 1 ELSE 0 END;

        -- Re-stated now that @IdShaped is known. Nothing else about the term is added.
        SET @KeyParameters = CONCAT (N'Skip=',        COALESCE (CAST (@Skip AS NVARCHAR (11)), N'(null)')
                                   , N', Take=',       COALESCE (CAST (@Take AS NVARCHAR (11)), N'(null)')
                                   , N', TermLength=', CAST (@TermLength AS NVARCHAR (11))
                                   , N', IdShaped=',   CAST (@IdShaped AS NVARCHAR (1)));

        -- LIKE metacharacters, escaped with \. The BACKSLASH GOES FIRST: escaping % before \ would re-escape the
        -- backslash just inserted, and the pattern would be wrong only for terms containing both characters. ] needs
        -- no escaping outside a bracket group, and [ having been escaped means no group can be opened.
        SET @Escaped = REPLACE (REPLACE (REPLACE (REPLACE (@Term, N'\', N'\\')
                                                , N'%', N'\%')
                                       , N'_', N'\_')
                              , N'[', N'\[');

        SET @ExactTerm       = @Term;
        SET @PrefixPattern   = @Escaped + N'%';
        SET @ContainsPattern = N'%' + @Escaped + N'%';

        -- Clamped, not refused. A caller asking for 100000 rows gets 100, and the clamp is here rather than in the web
        -- app because the procedure is the boundary the permission model enforces. The @Skip ceiling keeps
        -- @Skip + @Take inside INT.
        SET @Skip = LEAST (GREATEST (COALESCE (@Skip, 0), 0), 2000000000);
        SET @Take = LEAST (GREATEST (COALESCE (@Take, 25), 1), 100);

        -- ------------------------------------------------------------------------------------------
        -- 3. The filtered total.
        --
        --    THE PREDICATE HERE AND THE ONE IN SECTION 4 MUST STAY IDENTICAL. They are two statements
        --    for the reason 503 records -- COUNT (*) OVER () cost a 202,152-read worktable spool at the
        --    same assumed volume -- and the price is this drift risk: a condition added to one and not
        --    the other makes TotalRows a count of a different set than the page.
        --    build/tmp/da4_searchhandlersource.probesql asserts TotalRows against an independently
        --    computed count for both branches.
        --
        --    The INSERT rather than SELECT @TotalRows = is the measured point in the header. Under
        --    OPTION (RECOMPILE) @IdShaped is embedded as a constant, so exactly one of the two OR
        --    groups survives compilation: either a seekable HandlerId prefix, or a three-column
        --    contains-scan. Assigning to a variable instead loses the embedding and with it the seek.
        -- ------------------------------------------------------------------------------------------
        INSERT INTO @Counted (Value)
        SELECT COUNT (*)
          FROM dbo.vwHandlerSource AS v
         WHERE ((@IdShaped = 1 AND v.HandlerId LIKE @PrefixPattern ESCAPE N'\')
             OR (@IdShaped = 0 AND (v.HandlerId        LIKE @ContainsPattern ESCAPE N'\'
                                 OR v.HandlerName      LIKE @ContainsPattern ESCAPE N'\'
                                 OR v.SiteLocationCity LIKE @ContainsPattern ESCAPE N'\')))
        OPTION (RECOMPILE);

        SET @TotalRows = COALESCE ((SELECT c.Value FROM @Counted AS c), 0);

        -- ------------------------------------------------------------------------------------------
        -- 4. The ranked page, as keys.
        --
        --    Three levels, each doing one thing: the innermost computes MatchRank once, the middle
        --    ranks by it, the outer takes the window. Writing the CASE inside ROW_NUMBER's ORDER BY as
        --    well as in the select list would be the same seven comparisons twice, and the two copies
        --    could disagree.
        --
        --    ROW_NUMBER rather than OFFSET/FETCH, so Ordinal exists to be projected and the outer
        --    query does not have to trust a derived table's row order.
        -- ------------------------------------------------------------------------------------------
        INSERT INTO @Hits (Ordinal, HandlerSourceId, MatchRank)
        SELECT p.Ordinal
             , p.HandlerSourceId
             , p.MatchRank
          FROM (SELECT ROW_NUMBER () OVER (ORDER BY m.MatchRank       ASC
                                                  , m.HandlerName     ASC
                                                  -- Not optional. Thousands of rows share a MatchRank and plenty
                                                  -- share a name, and paging over a non-unique key repeats and skips
                                                  -- rows silently. Ascending on purpose: it only has to be
                                                  -- deterministic.
                                                  , m.HandlerSourceId ASC) AS Ordinal
                     , m.HandlerSourceId
                     , m.MatchRank
                  FROM (SELECT v.HandlerSourceId
                             , v.HandlerName
                             -- 1 and 2 are the only ranks reachable on the identifier branch, 3 to 7 the only ones
                             -- reachable on the text branch. The ELSE is city-contains: the predicate below admits no
                             -- row that matched nothing, so there is no eighth case.
                             , CASE WHEN v.HandlerId        =    @ExactTerm                        THEN CAST (1 AS TINYINT)
                                    WHEN v.HandlerId        LIKE @PrefixPattern   ESCAPE N'\'      THEN CAST (2 AS TINYINT)
                                    WHEN v.HandlerId        LIKE @ContainsPattern ESCAPE N'\'      THEN CAST (3 AS TINYINT)
                                    WHEN v.HandlerName      LIKE @PrefixPattern   ESCAPE N'\'      THEN CAST (4 AS TINYINT)
                                    WHEN v.HandlerName      LIKE @ContainsPattern ESCAPE N'\'      THEN CAST (5 AS TINYINT)
                                    WHEN v.SiteLocationCity LIKE @PrefixPattern   ESCAPE N'\'      THEN CAST (6 AS TINYINT)
                                    ELSE CAST (7 AS TINYINT) END AS MatchRank
                          FROM dbo.vwHandlerSource AS v
                         -- IDENTICAL to section 3. See the warning there.
                         WHERE ((@IdShaped = 1 AND v.HandlerId LIKE @PrefixPattern ESCAPE N'\')
                             OR (@IdShaped = 0 AND (v.HandlerId        LIKE @ContainsPattern ESCAPE N'\'
                                                 OR v.HandlerName      LIKE @ContainsPattern ESCAPE N'\'
                                                 OR v.SiteLocationCity LIKE @ContainsPattern ESCAPE N'\')))) AS m) AS p
         WHERE p.Ordinal >  @Skip
           AND p.Ordinal <= @Skip + @Take
        OPTION (RECOMPILE);

        SET @RowsOnPage = @@ROWCOUNT;

        -- Counts only, and no fragment of the term. Recovered into the context so a later failure still says how far
        -- the call got, since there is no start row carrying it.
        SET @ContextMessage = CONCAT (N'TotalRows=', @TotalRows, N', RowsOnPage=', @RowsOnPage);

        -- ------------------------------------------------------------------------------------------
        -- 5. The projection, and the last statement in the block. Joined back through the VIEW rather
        --    than the base table, so the AR7 filter is applied on this path too -- at most @Take
        --    clustered-index seeks. Curated: 218 columns are available and a result list needs enough
        --    to tell two similarly named handlers apart. Contact columns are excluded as PII and are
        --    not searchable either; see the header.
        -- ------------------------------------------------------------------------------------------
        SELECT h.Ordinal
             , v.HandlerSourceId
             , v.HandlerId
             , v.ActivityLocation
             , v.SourceType
             , v.SourceTypeDescription
             , v.Sequence
             , v.ReceivedDate
             , v.HandlerName
             -- Projected, not searched. Two handlers with similar names are told apart by their street address; adding
             -- these to the predicate would push the scan off IX_dbo_HandlerSource_Grid. See the header.
             , v.SiteLocationAddress1
             , v.SiteLocationCity
             , v.SiteLocationStateCode
             , v.SiteLocationZip
             , v.SiteLocationCountyDescription
             , v.WasteFederalGeneratorCategoryCode
             , v.WasteFederalGeneratorCategoryDescription
             , v.SrcUpdatedDate
             , v.auditModifiedDateUtc
             -- Why this row is here, for the web app's caption. MatchRank is the ordering; MatchField collapses it to
             -- the column that matched. Both are computed from the term and are safe to return to the caller who
             -- typed it -- and neither is written to a log row.
             , h.MatchRank
             , CASE WHEN h.MatchRank <= 3 THEN N'HandlerId'
                    WHEN h.MatchRank <= 5 THEN N'HandlerName'
                    ELSE                       N'SiteLocationCity' END AS MatchField
             -- A scalar, and the same number for every row on the page. Zero when nothing matches -- which the caller
             -- cannot see, because there is then no row to read it from.
             , @TotalRows AS TotalRows
          FROM @Hits               AS h
          JOIN dbo.vwHandlerSource AS v ON v.HandlerSourceId = h.HandlerSourceId
         ORDER BY h.Ordinal;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them -- the CONCAT and the
        -- EXEC below both do -- so capture them before doing anything else.
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- NO ROLLBACK. This procedure opens no transaction, so it has nothing of its own to roll back and any
        -- transaction live here belongs to the caller -- rolling it back would discard work this procedure never did.
        -- ROLLBACK is also illegal inside INSERT ... EXEC (error 8004), where it would replace the error being reported
        -- and abort this CATCH before the failure was recorded. A read that cannot record its own failure is precisely
        -- the defect this project is guarding against.

        -- Counts and a duration. NOTHING derived from the term, and nothing read from the view -- not even to make the
        -- message more helpful. A context that said which handler's row failed to load would be writing a
        -- regulated-entity record into a table the web app can read, and one that quoted the term would be writing
        -- whatever a person typed into a search box.
        SET @ContextMessage = CONCAT (COALESCE (@ContextMessage, N'(before the count)')
                                    , N', ElapsedMs='
                                    , LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, SYSUTCDATETIME ())
                                           , CAST (2147483647 AS BIGINT)));

        -- @ExecutionLogId = NULL on purpose: there is no start row to update, so this takes the orphan-insert branch of
        -- logs.uspRecordExecutionErrorUpdate. The row is recognisable by Successful = 0 with EndDateUtc set and
        -- ElapsedMilliseconds NULL, which is why the duration is carried in the context message instead. Swallows
        -- everything by design, so this call cannot mask the error below it.
        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and the client could no
        -- longer tell a deadlock from a refused term. The leading semicolon is required: a bare THROW immediately after
        -- BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSearchHandlerSource'
    , @Description = N'The monitoring web app''s handler search: one free-text term matched against HandlerId, HandlerName and SiteLocationCity over the CURRENT version of Maryland''s handler records, ranked by match quality and returned one page at a time with the filtered total on every row as TotalRows. THE SEARCH TERM IS NOT LOGGED, and that is the rule 501, 503, 504 and 505 all record in advance for this procedure: a search term is whatever a person typed over a database of regulated-entity records, and logs.ExecutionLog is readable by the web app. @KeyParameters carries Skip, Take, TermLength and IdShaped and nothing else; @SearchTerm and the trimmed and escaped forms of it are excluded BY NAME, no refusal message quotes the term, and build/check_search_term_privacy.py asserts all of that in the file and in the deployed module. Contact name, phone, email and address are not merely excluded from the projection as PII -- they are excluded from the PREDICATE, because searching by contact surname would turn a handler search into a people search; SrcUpdatedBy is excluded for the same reason. SiteLocationAddress1 and SiteLocationZip are projected but not searched, because the three searched columns are exactly what IX_dbo_HandlerSource_Grid already carries, so this procedure needs no index of its own. A term of two characters is the floor and NULL, empty and whitespace-only fail the same test: measured at an assumed 400,000 rows, a one-character term matched all 100,000 current rows and returned an arbitrary 25 of them, which reads as "these are the best matches". A term longer than the 80-character HandlerName is NOT refused, because that boundary is a column width in a generated script and an empty result set is correct at any width. An identifier-shaped term -- alphanumeric only, two letters then a digit -- is matched against HandlerId by prefix and not against name or city, so a pasted handler ID is an identifier lookup rather than a text search; MatchRank and MatchField tell the caller which branch ran. The term is escaped for LIKE with the backslash replaced first, so a typed % cannot match every handler in Maryland. There is no @SortBy, because the rank is the answer, and no @IncludeDeleted or @CurrentRecord, because dbo.vwHandlerSource has decided both. @Take is clamped to 1..100, narrower than the grid''s 500, because a search result list is read rather than scrolled. The filtered count is an INSERT into a table variable rather than SELECT @Variable = COUNT (*): measured, the assignment form silently gives up OPTION (RECOMPILE)''s parameter embedding, which costs the identifier branch its seek -- 1,196 logical reads against 5. Instrumented for FAILURES ONLY, with no successful-path row; an omitted @SearchTerm is error 201 and is the one failure this procedure cannot record, because it happens before the body runs. It opens no transaction and therefore never rolls one back. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The monitor only. The console app never searches -- it merges what the API hands it through
-- dbo.uspMergeHandlerSourceBatch, which the monitor in turn cannot execute -- and both directions are
-- asserted by build/check_permission_posture.py, so the asymmetry is measured rather than intended.
--
-- Ownership chaining carries the SELECT on dbo.vwHandlerSource, and through it on dbo.HandlerSource,
-- and the INSERT on logs.ExecutionLog, through this grant -- so the monitor login holds no direct
-- permission on any of the three (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSearchHandlerSource TO RCRAInfoMonitorRole;
END;
GO

PRINT N'506: dbo.uspSearchHandlerSource created or altered, EXECUTE granted to the monitor role.';
GO
