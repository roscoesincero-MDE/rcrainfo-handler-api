#!/usr/bin/env python
"""
A5 acceptance test: prove the two application logins cannot do what they are denied.

050_Roles_and_Users.sql issues a long list of DENY statements. Reading them back out of the catalog
proves only that the statements ran, which is the easy half. The half that matters is whether the
DENY actually stops the operation, and there are several ways for it not to:

  - a fixed database role picked up later (db_datareader, db_owner) outranks a schema-level DENY on
    everything it covers
  - a server role (sysadmin) makes every database-level restriction in this project decorative
  - ownership chaining means a procedure the login CAN execute reaches tables it cannot read, which
    is intended -- but dynamic SQL inside that procedure breaks the chain and needs direct rights,
    so the direct-rights denial has to be real
  - a GRANT added for a plausible reason silently outranks nothing, but a GRANT WITH GRANT OPTION or
    a role membership does

So every assertion here is made by CONNECTING AS THAT LOGIN and attempting the operation. The
catalog is not asked what should happen.

Credentials come from the environment (LoaderPassword, MonitorPassword) so that they are not on a
command line, and are handed to sqlcmd through SQLCMDPASSWORD for the same reason: -P would put the
password in the process list, where any other user on the machine can read it.

    $env:LoaderPassword  = '<from the password manager>'
    $env:MonitorPassword = '<from the password manager>'
    python build\\check_permission_posture.py

On the developer workstation, --dev-credentials reads them from
%LOCALAPPDATA%\\RCRAInfo\\dev-credentials.json instead. That path is outside the repository on
purpose and applies to the local instance only.

A note on the probes that CREATE something: if one of them succeeds, it has created a real object,
and this script does NOT remove it. Partly because there is no hard delete anywhere in this database
(AR7), and partly because the object is the evidence. A posture failure should be awkward to ignore.

Exit 0 when the posture holds, 1 when it does not, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

DEV_CREDENTIALS = Path(os.environ.get("LOCALAPPDATA", "")) / "RCRAInfo" / "dev-credentials.json"

# A probe is (label, database, query, expectation).
#
# Expectations:
#   'denied'      the operation must FAIL. Anything else is a posture failure.
#   '=<value>'    the operation must succeed and return exactly <value>.
#
# {other} is the other application's role, so each login is checked for its inability to see the
# posture of the one it shares a machine with (G18).
PROBES = [
    # ---- the login works at all. Without this, every 'denied' below would pass for the wrong
    # ---- reason: a bad password denies everything.
    ("connects to RCRAInfo", "RCRAInfo",
     "SET NOCOUNT ON; SELECT DB_NAME();", "=RCRAInfo"),

    # ---- no DDL. Neither application login holds DDL rights; the developer runs the DDL (G3).
    ("cannot CREATE TABLE", "RCRAInfo",
     "CREATE TABLE dbo.PostureProbe (Id INT NOT NULL);", "denied"),
    ("cannot CREATE PROCEDURE", "RCRAInfo",
     "CREATE PROCEDURE dbo.uspPostureProbe AS SELECT 1;", "denied"),
    ("cannot CREATE SCHEMA", "RCRAInfo",
     "CREATE SCHEMA probe;", "denied"),
    ("cannot ALTER SCHEMA", "RCRAInfo",
     "ALTER SCHEMA logs TRANSFER OBJECT::util.uspSetObjectDescription;", "denied"),
    ("cannot CREATE LOGIN", "master",
     "CREATE LOGIN PostureProbe WITH PASSWORD = 'D0-not-create-me-1234567';", "denied"),

    # ---- util is the developer's toolbox, not an application surface. DENY EXECUTE ON SCHEMA::util.
    ("cannot EXEC util.uspSetObjectDescription", "RCRAInfo",
     "EXEC util.uspSetObjectDescription @SchemaName = N'util', @ObjectType = N'PROCEDURE', "
     "@ObjectName = N'uspSetObjectDescription', @Description = N'posture probe';", "denied"),

    # ---- DENY VIEW DEFINITION. Metadata visibility FILTERS rather than errors, so the assertion is
    # ---- a count of zero, not a failure. A login that can enumerate the schema can plan against it.
    ("sees no user objects", "RCRAInfo",
     "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0;", "=0"),
    ("sees no procedures", "RCRAInfo",
     "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.procedures;", "=0"),
    ("sees no module source", "RCRAInfo",
     "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.sql_modules;", "=0"),
    ("cannot read a procedure body", "RCRAInfo",
     "SET NOCOUNT ON; SELECT ISNULL(OBJECT_DEFINITION("
     "OBJECT_ID(N'util.uspSetObjectDescription')), N'hidden');", "=hidden"),

    # ---- DENY VIEW DATABASE STATE. A login always sees its own session; seeing more than one means
    # ---- it can watch the other application work.
    ("sees only its own session", "RCRAInfo",
     "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_sessions;", "=1"),

    # ---- and cannot see how the other application is restricted.
    ("sees no permission of {other}", "RCRAInfo",
     "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.database_permissions AS dp "
     "JOIN sys.database_principals AS p ON p.principal_id = dp.grantee_principal_id "
     "WHERE p.name = N'{other}';", "=0"),

    # ---- [R12] AR8 instrumentation: the WRAPPERS are granted, the INNER procedures are not.
    # ---- logs.uspStartExecutionLogging and logs.uspRecordExecutionError carry GRANT EXECUTE to
    # ---- both roles, because every instrumented procedure calls them. The two procedures behind
    # ---- them carry no grant, so DENY EXECUTE ON SCHEMA::logs leaves them unreachable directly,
    # ---- and ownership chaining (dbo owns all five schemas) is what carries the call through.
    # ----
    # ---- Both probes pass ARGUMENTS THAT WOULD OTHERWISE WORK. That is the whole point: with a
    # ---- NULL @StartDateUtc the insert would fail on the NOT NULL column instead, and the probe
    # ---- would report 'denied' while proving nothing about permissions. If either of these ever
    # ---- succeeds it writes a real row to logs.ExecutionLog, which is the evidence.
    ("cannot EXEC logs.uspStartExecutionLoggingInsert", "RCRAInfo",
     "EXEC logs.uspStartExecutionLoggingInsert @ProcedureName = N'[posture].[probe]', "
     "@StartDateUtc = '2026-09-05T00:00:00';", "denied"),
    ("cannot EXEC logs.uspRecordExecutionErrorUpdate", "RCRAInfo",
     "EXEC logs.uspRecordExecutionErrorUpdate @ProcedureName = N'[posture].[probe]', "
     "@ExecutionLogId = NULL, @ErrorNumber = 50000;", "denied"),
]

# Role membership is checked separately because a single query covers every role, and because a role
# picked up later is the one change that would quietly undo everything above.
FIXED_DATABASE_ROLES = [
    "db_owner", "db_securityadmin", "db_accessadmin", "db_backupoperator",
    "db_ddladmin", "db_datawriter", "db_datareader", "db_denydatawriter", "db_denydatareader",
]

SERVER_ROLES = [
    "sysadmin", "securityadmin", "serveradmin", "setupadmin",
    "processadmin", "diskadmin", "dbcreator", "bulkadmin",
]

LOGINS = [
    ("RCRAInfoLoader", "LoaderPassword", "RCRAInfoMonitorRole"),
    ("RCRAInfoMonitor", "MonitorPassword", "RCRAInfoLoaderRole"),
]

# ------------------------------------------------------------------------------------------------
# DA1: the application procedures, and the two things about them a PROBES entry cannot express.
#
# 1. THE GRANTS ARE ASYMMETRIC. Each PROBES entry runs once per login with the same expectation, so
#    it can say 'denied for everyone' but not 'granted to the monitor and denied to the loader'.
#    That asymmetry is the whole permission design for these procedures -- the console app writes
#    handler data and never reads the monitoring grid; the web app is the mirror image -- so it needs
#    a structure that names the login each procedure belongs to.
#
# 2. CHAINED INSTRUMENTATION IS NOT VISIBLE FROM THE CALLING SIDE. An instrumented procedure in dbo
#    or logs calls logs.uspStartExecutionLogging, which calls
#    logs.uspStartExecutionLoggingInsert, which INSERTs into logs.ExecutionLog. The application
#    login holds EXECUTE on the outer procedure and the middle wrapper and NOTHING on the inner
#    procedure or the table -- DENY EXECUTE ON SCHEMA::logs and the direct-table denials cover both.
#    A successful call therefore proves the grant; it does not prove the chained INSERT reached the
#    table. Only reading the row back as the DEVELOPER does that, and only auditCreatedBy on that
#    row proves it was written on the application login's behalf rather than by something else.
#
# Each entry is (qualified procedure, the login granted EXECUTE, a call that must succeed for that
# login, the ProcedureName the instrumentation records). The call is chosen to be a no-op: an empty
# JSON array merges nothing, and one page of one row writes nothing but its own log entry.
#
# THE ProcedureName FIELD IS AN ASSERTION IN ITS OWN RIGHT -- DO NOT LOOSEN THE FILTER TO MAKE THIS
# PASS. The row is looked up by that exact string, so a row logged under any other name reads as no
# row at all. That is how the first run of this check found the defect it was written to find:
# OBJECT_NAME (@@PROCID) returns NULL for a principal denied metadata visibility, both these logins
# are denied it by script 050, and every row either application had ever written was therefore logged
# under the template's placeholder instead of a procedure name. The tempting "fix" -- matching on the
# row's identity instead of its ProcedureName -- would have made the check green and left the
# monitoring web app with nothing to group by. Match on the name.
#
# This does write two real rows to logs.ExecutionLog per run of this script, one per login. That is
# accepted rather than worked around: there is no hard delete anywhere in this database, the rows are
# the evidence the chain works, and G7 retention is what bounds logs.ExecutionLog in the end.
# ------------------------------------------------------------------------------------------------
# logs.uspStartLoadRun is the one application procedure with no no-op call available: a successful
# start IS a row in logs.LoadRun, and the row is the entire point of the procedure. So the call opens
# a run and closes it in the same batch. Leaving one 'Running' would make the NEXT run of this script
# fail its own concurrency refusal and read as a permission defect -- an assertion that breaks the
# thing it asserts on a second run is worse than no assertion.
#
# @AllowConcurrent = 1 for the mirror-image reason: a real load in flight must not make this check
# fail, and this check must not queue behind one. It is not the loader and the concurrency rule is not
# about it.
#
# @RunMode = 'Reconcile' with the machine and version naming this script is what keeps the resulting
# row honest in the monitoring grid. It is a real run row, it will be displayed to real users, and it
# says on its face what wrote it. Four of them per pass -- the entries below that need a Running run to
# call into -- because nothing in this database is hard-deleted and pretending otherwise is how the
# check would start lying.
START_AND_CLOSE_A_RUN = (
    "SET NOCOUNT ON; DECLARE @LoadRunId INT; "
    "EXEC logs.uspStartLoadRun @RunMode = N'Reconcile', @AllowConcurrent = 1, "
    "@MachineName = N'check_permission_posture', "
    "@ApplicationVersion = N'check_permission_posture.py', "
    "@LoadRunId = @LoadRunId OUTPUT; "
    "EXEC logs.uspCompleteLoadRun @LoadRunId = @LoadRunId, @Status = N'Succeeded';")


def within_a_run(body: str) -> str:
    """The same open-and-close batch with one call in the middle.

    logs.uspUpsertHandlerLoadStatusSet and dbo.uspReconcileCurrentRecord both REFUSE a LoadRunId that
    is not open and Running, which is the right behaviour and leaves them with no standalone no-op
    call. So the batch opens a run, makes the no-op call inside it, and closes it -- and the run is
    closed in the same batch for the reason START_AND_CLOSE_A_RUN gives: a Running row left behind
    would block the next real load, and a check that breaks the thing it asserts is worse than none.
    """
    return (
        "SET NOCOUNT ON; DECLARE @LoadRunId INT; "
        "EXEC logs.uspStartLoadRun @RunMode = N'Reconcile', @AllowConcurrent = 1, "
        "@MachineName = N'check_permission_posture', "
        "@ApplicationVersion = N'check_permission_posture.py', "
        "@LoadRunId = @LoadRunId OUTPUT; "
        f"{body} "
        "EXEC logs.uspCompleteLoadRun @LoadRunId = @LoadRunId, @Status = N'Succeeded';")


# ------------------------------------------------------------------------------------------------
# THE FIFTH FIELD, deny_call, AND WHY THE DENIAL HALF NEEDED ONE.
#
# The denial half runs the entry's call as every login that is NOT its owner and requires a failure.
# A bare "it failed" is a weaker assertion than it looks, because a multi-statement batch fails at its
# FIRST statement: the monitor running START_AND_CLOSE_A_RUN is refused at logs.uspStartLoadRun and
# never reaches logs.uspCompleteLoadRun, so that entry's denial was being satisfied by a refusal of a
# different object. Every batch here that opens a run has the same shape and the same hole.
#
# So the refusal is now required to be a PERMISSION refusal that NAMES the object under test (error
# 229 spells the object out), and an entry whose main call cannot deliver that supplies a deny_call
# that can: a single EXEC of the object itself. A deny_call may pass arguments the procedure would
# reject, because permission is checked before the body runs -- the loader never runs it, and for any
# other login the only outcome that matters is whether the EXECUTE was allowed. If a grant ever leaks,
# the call gets past the permission check and fails on the arguments instead, which is not a permission
# denial and is exactly what the assertion now catches.
#
# None means "the main call already names the right object", which is true for every single-EXEC entry.
# ------------------------------------------------------------------------------------------------
APPLICATION_PROCEDURES = [
    ("dbo.uspMergeHandlerSourceBatch", "RCRAInfoLoader",
     "EXEC dbo.uspMergeHandlerSourceBatch @Payload = N'[]';",
     "[dbo].[uspMergeHandlerSourceBatch]", None),
    ("logs.uspGetLoadRunPage", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC logs.uspGetLoadRunPage @Take = 1;",
     "[logs].[uspGetLoadRunPage]", None),

    # ---- DA4: the load-run lifecycle and the watermark. Loader-only, all four of them. The monitor
    # ---- being refused each one is the half of this that matters most: the web app is read-only in
    # ---- Phase 1, and a leaked grant on config.uspSetLoadWatermark would let a monitoring screen
    # ---- move the watermark.
    ("logs.uspStartLoadRun", "RCRAInfoLoader", START_AND_CLOSE_A_RUN,
     "[logs].[uspStartLoadRun]", None),
    ("logs.uspCompleteLoadRun", "RCRAInfoLoader", START_AND_CLOSE_A_RUN,
     "[logs].[uspCompleteLoadRun]",
     "EXEC logs.uspCompleteLoadRun @LoadRunId = -1, @Status = N'Succeeded';"),

    # config.uspSetLoadWatermark CONVERGES, so a call with nothing but its defaults is a genuine
    # no-op: it reads the seeded row, finds every target equal to what is already stored, and writes
    # nothing but its own log row. That makes it both the cheapest posture probe here and a standing
    # assertion that the convergence guard actually holds -- if it ever stopped holding, this call
    # would start moving auditModifiedDateUtc on the watermark once per posture run.
    ("config.uspSetLoadWatermark", "RCRAInfoLoader",
     "EXEC config.uspSetLoadWatermark;",
     "[config].[uspSetLoadWatermark]", None),

    # A logged name of None declares that the procedure writes NO row to logs.ExecutionLog on the
    # SUCCESSFUL path, and that is ASSERTED rather than skipped -- see check_application_procedures for
    # the inverted test. It does NOT mean uninstrumented: since MDE's review on 2026-09-05 every
    # procedure in this database carries a TRY/CATCH that records its errors, reads included. What this
    # entry asserts is the other half of that decision -- a monitoring grid refreshing every few
    # seconds must not write a log row per refresh (DA1 review decision 2).
    ("config.uspGetLoadWatermark", "RCRAInfoLoader",
     "SET NOCOUNT ON; EXEC config.uspGetLoadWatermark;",
     None, None),

    # ---- DA4: the loader's data writes. An empty JSON set is a genuine no-op in both, so each call
    # ---- writes nothing but its own log row -- and both refuse a run that is not Running, hence
    # ---- within_a_run.
    ("logs.uspUpsertHandlerLoadStatusSet", "RCRAInfoLoader",
     within_a_run("EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = @LoadRunId, "
                  "@Mode = N'Enumerate', @Elements = N'[]';"),
     "[logs].[uspUpsertHandlerLoadStatusSet]",
     "EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = -1, @Mode = N'Enumerate', "
     "@Elements = N'[]';"),
    ("dbo.uspReconcileCurrentRecord", "RCRAInfoLoader",
     within_a_run("EXEC dbo.uspReconcileCurrentRecord @LoadRunId = @LoadRunId, "
                  "@Summaries = N'[]';"),
     "[dbo].[uspReconcileCurrentRecord]",
     "EXEC dbo.uspReconcileCurrentRecord @LoadRunId = -1, @Summaries = N'[]';"),
    # The attempt log, and the only procedure that WRITES logs.HandlerLoadAttempt. The monitor already
    # READS that table -- logs.uspGetLoadRunSummary aggregates it through ownership chaining, down to the
    # per-outcome and Retry-After counts -- so the denial half is the argued one here: a grant leaking to
    # the web app would let a reader of the attempt log forge rows in it, which would make the 03:00
    # diagnosis untrustworthy rather than merely incomplete. Empty array, so nothing is written but the
    # procedure's own log row.
    ("logs.uspRecordHandlerLoadAttemptSet", "RCRAInfoLoader",
     within_a_run("EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @LoadRunId, "
                  "@Elements = N'[]';"),
     "[logs].[uspRecordHandlerLoadAttemptSet]",
     "EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = -1, @Elements = N'[]';"),
    # The only procedure that sets IsDeleted on dbo.HandlerSource, so it is the only one whose grant
    # leaking to the web app would REMOVE data rather than expose it. The empty array is a genuine
    # no-op -- validation of @LoadRunId and @Reason happens first, and nothing is resolved to delete.
    ("dbo.uspSoftDeleteHandlerSourceSet", "RCRAInfoLoader",
     within_a_run("EXEC dbo.uspSoftDeleteHandlerSourceSet @LoadRunId = @LoadRunId, "
                  "@Elements = N'[]', @Reason = N'Permission posture check: deletes nothing.';"),
     "[dbo].[uspSoftDeleteHandlerSourceSet]",
     "EXEC dbo.uspSoftDeleteHandlerSourceSet @LoadRunId = -1, @Elements = N'[]', "
     "@Reason = N'Permission posture check: deletes nothing.';"),
    # @Mode = 'Upsert' deliberately. Under 'Full' an empty array is REFUSED, because a complete code
    # list is never empty and an empty one means the fetch returned nothing -- so 'Full' here would make
    # a posture probe fail on a validation rule rather than on a permission, and the two failures look
    # nothing alike but would be reported the same way. Under 'Upsert' an empty array means "no
    # additions", which is a genuine no-op: nothing is written and nothing is retired.
    ("dbo.uspRefreshLookupSet", "RCRAInfoLoader",
     within_a_run("EXEC dbo.uspRefreshLookupSet @LoadRunId = @LoadRunId, "
                  "@LookupName = N'ContactType', @Mode = N'Upsert', @Elements = N'[]';"),
     "[dbo].[uspRefreshLookupSet]",
     "EXEC dbo.uspRefreshLookupSet @LoadRunId = -1, @LookupName = N'ContactType', "
     "@Mode = N'Upsert', @Elements = N'[]';"),

    # ---- DA4: the paged reads. Monitor-only, and the mirror image of every loader entry above.
    #
    # A logged name of None here is a stronger statement than it is on config.uspGetLoadWatermark. That
    # procedure never had a successful-path row; this one DROPPED it, on logs.uspGetLoadRunPage's
    # recommendation, so the None is what stops the drop from being quietly undone -- restore the start
    # call and this assertion fails. The error recording is untouched and is checked from the deployed
    # side by build/check_stored_headers.py.
    #
    # @Take = 1 and nothing else: the grid's defaults are exercised, and on an empty table the call
    # still succeeds and returns no rows, which is the shape a permission probe needs.
    ("logs.uspGetHandlerLoadStatusPage", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC logs.uspGetHandlerLoadStatusPage @Take = 1;",
     None, None),

    # The monitor's run summary, and the denial half is the argued one: the CONSOLE APP wrote every
    # counter this procedure reads, through logs.uspCompleteLoadRun and logs.uspUpsertHandlerLoadStatusSet.
    # Reading its own arithmetic back would tell it nothing, and the grant would hand a writer a read over
    # three logs tables it otherwise only writes.
    #
    # No arguments at all, which exercises the default path -- "summarize the most recent run". That call
    # returns one row on this database and ZERO rows on a freshly created one, and both are success: the
    # empty answer is deliberate, because a new installation with no runs yet is not a fault. So this
    # entry keeps passing on an empty database, which is what a permission probe needs and is also the
    # only reason it can run before the loader ever has.
    #
    # A logged name of None, for the same reason as the entry above: this procedure records errors and
    # writes nothing on the successful path, and the None is what fails if a start call is ever added.
    ("logs.uspGetLoadRunSummary", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC logs.uspGetLoadRunSummary;",
     None, None),

    # The handler grid. The first entry here whose grant reaches dbo.HandlerSource rather than the logs
    # schema, and that is what makes the DENIAL half worth asserting: the console app holds INSERT and
    # UPDATE on that table through dbo.uspMergeHandlerSourceBatch, so if ownership chaining were
    # misconfigured a stray grant would be easy to miss. The loader has no reason to page a grid -- it
    # merges what the API hands it and finds its resume point through config.uspGetLoadWatermark.
    #
    # The grant half also proves the chain is THREE links deep here and not two: the monitor holds
    # EXECUTE on the procedure only, the procedure selects dbo.vwHandlerSource, and the view selects
    # dbo.vwHandlerSourceHistory, which selects dbo.HandlerSource. Ownership chaining has to carry the
    # read the whole way for this call to return at all (AR3).
    #
    # No arguments, which exercises the default path -- first page of 50 by HandlerId. It returns ZERO
    # rows on this database, because all 50 dbo.HandlerSource rows are soft-deleted probe leftovers and
    # dbo.vwHandlerSource therefore exposes none. That is success, not a fault, and it is the same
    # property as the entry above: a permission probe must pass on an empty database, because it runs
    # before the loader ever has.
    #
    # A logged name of None: this procedure records errors and writes nothing on the successful path, so
    # the None is what fails if a successful-path log row is ever added without this entry being updated.
    ("dbo.uspGetHandlerSourcePage", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC dbo.uspGetHandlerSourcePage;",
     None, None),

    # The version history. Monitor-only for a different reason than the grid above, and the denial half
    # is the interesting one: the console app has no reason to read a version history at all. It knows
    # what it just merged, and dbo.uspReconcileCurrentRecord -- which it DOES execute -- reads the flags
    # it needs directly off the table. So a grant appearing here would mean the loader had been given a
    # reporting screen, which is exactly the drift this file exists to catch.
    #
    # THE FIRST ENTRY IN THIS LIST THAT MUST PASS AN ARGUMENT, and that is a property of the procedure
    # rather than of this probe. @HandlerId is REQUIRED and has no default, so a bare EXEC fails with
    # error 201 -- which would be reported here as a permission failure when it is nothing of the kind.
    # The id passed is deliberately one no handler has: it returns ZERO rows, which is success, and it
    # keeps this entry green on a freshly created database the way every other entry here has to be. If
    # a future change gives @HandlerId a default, this argument becomes redundant rather than wrong, so
    # it can stay.
    #
    # A logged name of None: this procedure records errors and writes nothing on the successful path, so
    # the None is what fails if a successful-path log row is ever added without this entry being updated.
    ("dbo.uspGetHandlerSourceHistoryPage", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = N'ZZNOSUCHID';",
     None, None),

    # The wide single-record read, and THE ONLY ENTRY IN THIS FILE WHERE THE DENIAL HALF IS A DISCLOSURE
    # BOUNDARY RATHER THAN A TIDINESS RULE. 505 returns the ~57 contact name, phone, email and
    # mailing-address columns that 503 and 504 withhold as PII, and one of the four properties 505's
    # header names as making that acceptable is precisely this: the grant is monitor-only. The console app
    # has no reason to read a handler back at all, never mind its contacts -- it wrote the row, and
    # dbo.uspReconcileCurrentRecord, which it DOES execute, reads the flags it needs directly. So the
    # DENY assertion below is not symmetry for its own sake; it is the assertion that turns a sentence in
    # a header into something a run can fail on.
    #
    # The second entry that must pass an argument, for the same reason as the one above: @HandlerSourceId
    # is REQUIRED, so a bare EXEC fails with error 201 and would be reported here as a permission failure.
    # 2147483647 is chosen rather than a small number for two reasons -- it is positive, so it gets past
    # 505's own validation and actually reaches the SELECT that the ownership chain has to carry, and
    # nothing will ever legitimately hold that IDENTITY value, so this returns ZERO rows and stays green
    # on a freshly created database the way every other entry here has to.
    #
    # A logged name of None: this procedure records errors and writes nothing on the successful path, so
    # the None is what fails if a successful-path log row is ever added without this entry being updated.
    ("dbo.uspGetHandlerSourceDetail", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = 2147483647;",
     None, None),

    # The search box, and the twentieth and last procedure of Workstream DA. Monitor-only for the same
    # reason 503, 504 and 505 are: the console app merges what the API hands it and finds its starting
    # point through config.uspGetLoadWatermark, so it has nothing to search FOR. The denial half is what
    # makes that a measured fact rather than an intention.
    #
    # @SearchTerm is REQUIRED with no default, so a bare EXEC fails with error 201 and would be reported
    # here as a permission failure when it is nothing of the kind. The term passed has to clear 506's own
    # two-character floor -- a one-character term is REFUSED with error 50000, which would also be
    # misreported -- and then match nothing, so this entry returns ZERO rows and stays green on a freshly
    # created database the way every other entry here has to. 'ZZNOSUCHHANDLER' is 15 characters of
    # letters that begin with two letters and no digit, so it takes 506's TEXT branch: that is the branch
    # that reads all three searched columns through the view, which is the ownership chain this entry
    # exists to prove.
    #
    # A logged name of None: this procedure records errors and writes nothing on the successful path, so
    # the None is what fails if a successful-path log row is ever added without this entry being updated.
    # For 506 the None is doing a second job as well -- a successful-path row would be a log row per
    # search request, and the row's @KeyParameters would then be the thing standing between a typed search
    # term and logs.ExecutionLog. build/check_search_term_privacy.py guards the term itself; this None
    # guards the existence of the row it would be written to.
    ("dbo.uspSearchHandlerSource", "RCRAInfoMonitor",
     "SET NOCOUNT ON; EXEC dbo.uspSearchHandlerSource @SearchTerm = N'ZZNOSUCHHANDLER';",
     None, None),
]


def run_as(login: str, password: str, database: str, query: str) -> tuple[int, str]:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    # SQLCMDPASSWORD rather than -P: the password stays out of the process list.
    env = dict(os.environ, SQLCMDPASSWORD=password)

    result = subprocess.run(
        [exe, "-S", ARGS.server, "-U", login, "-C", "-b", "-I", "-x",
         "-d", database, "-h", "-1", "-W", "-Q", query],
        env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    text = " | ".join(
        line.strip() for line in (result.stdout + result.stderr).splitlines() if line.strip())
    return result.returncode, text


def first_value(output: str) -> str:
    return output.split(" | ")[0].strip() if output else ""


def check_login(login: str, password: str, other_role: str) -> tuple[list[str], int]:
    failures: list[str] = []
    performed = 0

    for label, database, query, expectation in PROBES:
        label = label.format(other=other_role)
        code, output = run_as(login, password, database, query.format(other=other_role))
        performed += 1

        if expectation == "denied":
            if code == 0:
                failures.append(
                    f"{login}: {label} -- THE OPERATION SUCCEEDED. Whatever object it created is "
                    f"still there, deliberately; it is the evidence. Output: {output or '(none)'}")
            continue

        want = expectation[1:]

        if code != 0:
            failures.append(
                f"{login}: {label} -- expected {want!r} but the query failed. Either the "
                f"credential is wrong or a permission this test relies on has changed: {output}")
        elif first_value(output) != want:
            failures.append(
                f"{login}: {label} -- expected {want!r}, got {first_value(output)!r}. "
                f"Full output: {output}")

    # Fixed database roles, all in one round trip.
    role_query = "SET NOCOUNT ON; SELECT " + " + N'|' + ".join(
        f"CAST(ISNULL(IS_ROLEMEMBER(N'{role}'), 0) AS NVARCHAR(1))"
        for role in FIXED_DATABASE_ROLES) + ";"

    code, output = run_as(login, password, "RCRAInfo", role_query)
    performed += 1

    if code != 0:
        failures.append(f"{login}: could not read fixed database role membership: {output}")
    else:
        held = [role for role, flag in zip(FIXED_DATABASE_ROLES, first_value(output).split("|"))
                if flag == "1"]
        if held:
            failures.append(
                f"{login}: is a member of fixed database role(s) {', '.join(held)}. A fixed role "
                f"outranks the schema-level DENY posture for everything it covers, so every other "
                f"assertion in this test is worth less than it looks.")

    server_query = "SET NOCOUNT ON; SELECT " + " + N'|' + ".join(
        f"CAST(ISNULL(IS_SRVROLEMEMBER(N'{role}'), 0) AS NVARCHAR(1))"
        for role in SERVER_ROLES) + ";"

    code, output = run_as(login, password, "master", server_query)
    performed += 1

    if code != 0:
        failures.append(f"{login}: could not read server role membership: {output}")
    else:
        held = [role for role, flag in zip(SERVER_ROLES, first_value(output).split("|"))
                if flag == "1"]
        if held:
            failures.append(
                f"{login}: is a member of server role(s) {', '.join(held)}. Server roles are above "
                f"the database, so this defeats the entire posture rather than part of it.")

    return failures, performed


def check_direct_table_access(login: str, password: str, developer_visible: list[str]) -> list[str]:
    """Once Workstream B creates tables, the loader must still not be able to read or write one
    directly: every write goes through a set-based procedure that owns the audit stamping
    (Revision 6). Until a table exists there is nothing to probe, which this reports rather than
    passes over in silence."""
    failures: list[str] = []

    for qualified in developer_visible:
        for verb, query in (
            ("SELECT", f"SET NOCOUNT ON; SELECT TOP (1) 1 FROM {qualified};"),
            ("INSERT", f"INSERT INTO {qualified} DEFAULT VALUES;"),
        ):
            code, output = run_as(login, password, "RCRAInfo", query)
            if code == 0:
                failures.append(
                    f"{login}: direct {verb} on {qualified} SUCCEEDED. Writes belong to the "
                    f"set-based procedures, which own the audit stamping; a direct path around them "
                    f"produces rows no audit column describes. Output: {output or '(none)'}")

    return failures


def as_developer(server: str, query: str) -> list[str]:
    """Runs a query with the DEVELOPER's Windows credentials. Used for the two things the application
    logins are specifically unable to do: enumerate the tables, and read logs.ExecutionLog."""
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-h", "-1", "-W",
         "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode} running\n  {query}\n{result.stdout}")

    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def developer_tables(server: str) -> list[str]:
    """Table list read with the DEVELOPER's Windows credentials, because the whole point is that the
    application logins cannot enumerate them."""
    return as_developer(
        server,
        "SET NOCOUNT ON; SELECT QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) "
        "FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id "
        "ORDER BY s.name, t.name;")


def check_application_procedures(credentials: list[tuple[str, str, str]]) -> tuple[list[str], int]:
    """DA1: the EXECUTE grants on the application procedures are asymmetric, and an instrumented
    procedure's log row has to be read back as the developer to prove the chain reached the table.
    Neither fits a PROBES entry -- see APPLICATION_PROCEDURES for why."""
    failures: list[str] = []
    performed = 0
    passwords = {login: password for login, password, _ in credentials}

    for procedure, owner, call, logged_name, deny_call in APPLICATION_PROCEDURES:
        if owner not in passwords:
            failures.append(
                f"{procedure}: no credential for {owner}, so the one login that is SUPPOSED to be "
                f"able to execute it was never tested. The denial half of this assertion passing "
                f"means nothing on its own -- a procedure nobody can execute passes it too.")
            continue

        # The denial half: every OTHER login must be refused, and refused ON THIS OBJECT. See the
        # comment above APPLICATION_PROCEDURES for why the second half of that sentence is load-bearing.
        refusal = deny_call if deny_call is not None else call
        bare_name = procedure.split(".", 1)[1]

        for login, password, _ in credentials:
            if login == owner:
                continue
            code, output = run_as(login, password, "RCRAInfo", refusal)
            performed += 1
            if code == 0:
                failures.append(
                    f"{login}: EXEC {procedure} SUCCEEDED, and only {owner} is granted it. The two "
                    f"applications share a machine (G18), so a grant that leaks across them is not "
                    f"a theoretical problem. Output: {output or '(none)'}")
            elif "permission was denied" not in output or f"'{bare_name}'" not in output:
                # It failed, but not because EXECUTE on THIS procedure was refused -- so the call
                # never reached the permission check on the object under test, and the entry proves
                # nothing about it. Either the batch fails earlier (give the entry a deny_call that
                # is a single EXEC of this procedure), or the grant has leaked and the body is now
                # rejecting the arguments instead, which is the case worth catching.
                failures.append(
                    f"{login}: EXEC {procedure} failed, but NOT with a permission denial naming "
                    f"{bare_name!r}, so this assertion did not test the grant on that object. "
                    f"Output: {output or '(none)'}")

        # The grant half, and with it the chained INSERT into logs.ExecutionLog.
        #
        # The watermark the log row must appear above -- or, for an uninstrumented procedure, must NOT
        # appear above. For an instrumented one it is scoped to that procedure's own name, so a
        # concurrent call to something else cannot satisfy the assertion. For an uninstrumented one it
        # has to span the whole table, because the claim is that nothing NEW arrives under the name;
        # scoping it to the name would make an old row from a since-removed instrumentation block keep
        # the check red forever, and nothing in this database is hard-deleted.
        name_filter = f"ProcedureName = N'{logged_name}'" if logged_name is not None else "1 = 1"
        before = as_developer(
            ARGS.server,
            f"SET NOCOUNT ON; SELECT COALESCE(MAX(ExecutionLogId), 0) FROM logs.ExecutionLog "
            f"WHERE {name_filter};")
        watermark = first_value(" | ".join(before)) or "0"

        code, output = run_as(owner, passwords[owner], "RCRAInfo", call)
        performed += 1

        if code != 0:
            failures.append(
                f"{owner}: EXEC {procedure} FAILED, and this login is the one granted it. Either "
                f"the GRANT at the end of that procedure's script did not run, or the procedure "
                f"itself is broken. Output: {output}")
            continue

        # An uninstrumented procedure is asserted from the other direction, not skipped. MDE settled
        # at the DA1 review that every procedure which WRITES is instrumented and a read is
        # instrumented only where the plan names one; config.uspGetLoadWatermark is the first read
        # that policy leaves alone. If instrumentation is ever added to it, this fails, and the fix is
        # to give the entry its logged name -- which is exactly the conversation that should happen.
        # A skip would let the policy drift in either direction with nothing to notice.
        if logged_name is None:
            schema_part, name_part = procedure.split(".", 1)
            expected = f"[{schema_part}].[{name_part}]"
            rows = as_developer(
                ARGS.server,
                f"SET NOCOUNT ON; SELECT COUNT(*) FROM logs.ExecutionLog "
                f"WHERE ProcedureName = N'{expected}' AND ExecutionLogId > {watermark};")
            performed += 1

            if first_value(" | ".join(rows)) != "0":
                failures.append(
                    f"{procedure} wrote a row to logs.ExecutionLog under {expected!r}, but this "
                    f"entry declares it UNINSTRUMENTED. Either the AR8 block was added to the "
                    f"procedure -- in which case give this entry that name instead of None, and the "
                    f"assertion becomes the ordinary one -- or half a block was added, which is the "
                    f"case worth catching: a partial block logs a start and never completes it, so "
                    f"every call it makes reads as a failure in the monitoring grid.")
            continue

        # Read the log row back as the developer. The application login cannot see this table at
        # all, which is exactly why the read has to happen from the other side.
        rows = as_developer(
            ARGS.server,
            f"SET NOCOUNT ON; SELECT CONCAT(auditCreatedBy, N'|', Successful, N'|', "
            f"ReCreatedAfterRollback) FROM logs.ExecutionLog "
            f"WHERE ProcedureName = N'{logged_name}' AND ExecutionLogId > {watermark} "
            f"ORDER BY ExecutionLogId;")
        performed += 1

        if not rows:
            failures.append(
                f"{owner}: EXEC {procedure} succeeded but wrote NO row to logs.ExecutionLog. The "
                f"AR8 instrumentation is chained -- the login holds EXECUTE on the wrapper and "
                f"nothing on logs.uspStartExecutionLoggingInsert or the table -- so a missing row "
                f"means ownership chaining is broken and every instrumented call is running "
                f"unlogged. A successful EXEC does not prove this by itself, which is why it is "
                f"checked separately.")
            continue

        fields = rows[-1].split("|")
        actor = fields[0] if fields else ""
        successful = fields[1] if len(fields) > 1 else ""

        if actor != owner:
            failures.append(
                f"{owner}: EXEC {procedure} wrote a log row, but auditCreatedBy is {actor!r}. The "
                f"column defaults to ORIGINAL_LOGIN(), which survives EXECUTE AS, so anything other "
                f"than the calling login means the row does not record who actually ran the call.")

        if successful != "1":
            failures.append(
                f"{owner}: EXEC {procedure} returned success to the caller but its log row has "
                f"Successful = {successful!r}. The completion UPDATE sits after the COMMIT, so this "
                f"is the shape of a committed batch reported as failed.")

    return failures, performed


def load_password(variable: str, use_dev: bool, login: str) -> str:
    value = os.environ.get(variable)
    if value:
        return value

    if not use_dev:
        raise RuntimeError(
            f"Environment variable {variable} is not set. Set it from the password manager entry "
            f"for {login}, or pass --dev-credentials on the developer workstation.")

    if not DEV_CREDENTIALS.is_file():
        raise RuntimeError(f"{DEV_CREDENTIALS} does not exist.")

    data = json.loads(DEV_CREDENTIALS.read_text(encoding="utf-8"))
    value = (data.get("logins") or {}).get(login)

    if not value:
        raise RuntimeError(f"{DEV_CREDENTIALS} has no password for {login}.")

    return value


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default=".", help="target SQL Server instance (default: .)")
    parser.add_argument("--dev-credentials", action="store_true",
                        help="read passwords from %%LOCALAPPDATA%%\\RCRAInfo\\dev-credentials.json")
    global ARGS
    ARGS = parser.parse_args()

    try:
        credentials = [
            (login, load_password(variable, ARGS.dev_credentials, login), other)
            for login, variable, other in LOGINS
        ]
        tables = developer_tables(ARGS.server)
    except (RuntimeError, json.JSONDecodeError) as exc:
        print(f"FAIL  permission posture: {exc}")
        return 2

    failures: list[str] = []
    performed = 0

    for login, password, other in credentials:
        login_failures, count = check_login(login, password, other)
        failures.extend(login_failures)
        performed += count

        failures.extend(check_direct_table_access(login, password, tables))
        performed += len(tables) * 2

    try:
        procedure_failures, count = check_application_procedures(credentials)
    except RuntimeError as exc:
        print(f"FAIL  permission posture: {exc}")
        return 2

    failures.extend(procedure_failures)
    performed += count

    if failures:
        print(f"FAIL  permission posture ({len(failures)} of {performed} assertions):")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print(f"PASS  permission posture ({performed} assertions, attempted as the logins themselves, "
          f"across {len(credentials)} logins)")

    if not tables:
        # Not a pass and not a failure: a real assertion that cannot run yet. Saying so is the
        # difference between a test suite that is complete and one that only looks complete.
        print("      NOTE: no tables exist yet, so direct SELECT/INSERT denial was not tested. "
              "Re-run after Workstream B1 creates dbo.HandlerSource; that assertion is the one "
              "that proves writes cannot bypass the procedures.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
