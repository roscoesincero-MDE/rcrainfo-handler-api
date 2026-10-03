# Negative-test fixtures for the SQL guardrails

Every file here is **deliberately wrong**. Each one exists to prove that a specific rule in
`.claude/hooks/validate-sql.py` actually fires. A validator that has only ever been run against
correct scripts has never been tested: it reports success either way, and the day a rule stops
matching is the day nobody finds out.

`build/check_sql_guardrails.py` runs every fixture through the validator and requires

1. a non-zero exit code, and
2. every substring named in the fixture's own `-- EXPECT:` header lines to appear in the output.

The second requirement is the point. Asserting only "it failed" would let a fixture pass because
some *unrelated* rule tripped, which is how a broken rule hides behind a working one.

## The `.badsql` extension

These are not named `.sql`, on purpose, for two reasons:

- The `PostToolUse` hook fires on `.sql` writes, so editing a fixture would report its intended
  violations as if they were defects to fix.
- The validator's directory mode globs `*.sql`, so a fixture under any scanned directory would
  fail the whole build. The runner passes fixture paths explicitly instead, which the validator's
  file mode accepts regardless of extension.

### The same reasoning applies to scratch probes: `build/tmp/*.probesql`

A throwaway probe run by hand against the dev database legitimately does things `src/` must never
contain — `CREATE TABLE #Page` with no existence guard, `TRUNCATE TABLE #Page` between calls,
`DROP TABLE #Page` at the end. All three are correct on a temp table and all three are rejected by the
hook, which fires on extension and has no way to know the target is `#Page` rather than a real table.
Naming the file `.probesql` keeps it out of the hook's way for exactly the reason a fixture is
`.badsql`. Nothing is weakened: `build/guardrails.py` scans `src/RCRAInfo.Database` only, so a probe
could not have been validated either way, and `build/tmp/` is gitignored.

**Do not reach for this to silence the hook on real SQL.** The test is whether the file is disposable.
A fixture and a probe are both run explicitly, by name, by a human or by the runner; anything the
deployment script picks up is `.sql` and is validated.

## `-- EXPECT:` lines

The expectations live in the fixture rather than in the runner so that the wrong SQL and the
reason it is wrong cannot drift apart. They are ordinary `--` comments, which the validator blanks
out before it scans, so they cannot themselves influence the result.

## Adding a rule

Add the rule to `validate-sql.py`, then add or extend a fixture here so the rule has a witness.
A rule with no fixture is a rule that is assumed to work.

**A fixture may contain something correct, and sometimes must.** `object_name_shape.badsql` opens with
a procedure that is right on both counts it tests, because a file in which everything is wrong cannot
distinguish a rule that fires correctly from a rule that fires on everything. Nothing here requires a
fixture to be wrong throughout — only that it be wrong somewhere, and that it name what it expects.
The same fixture is also the reason to reach for a **new** file rather than more `-- EXPECT:` lines in
an existing one: its last object is headerless on purpose, which only means anything because the
objects above it are documented.
