using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using RCRAInfo.Data.Results;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The round trip that DA5 and G32 exist for: send a fully-populated handler, read every column back,
/// and assert each one holds what was sent.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that a table-valued parameter would have made unnecessary and JSON makes essential.
/// With a TVP a shape mismatch fails loudly. With JSON, <b><c>OPENJSON</c> matches property names
/// case-sensitively regardless of collation, and a path that matches nothing shreds to <c>NULL</c>
/// rather than erroring</b> — so a renamed or mis-cased property merges a column of nulls, returns the
/// row count the caller expected, and logs a successful run. Nothing in the database, the loader or the
/// log says otherwise.
/// </para>
/// <para>
/// So the assertion is per column and it is on the VALUE, not merely on non-nullity. Non-nullity alone
/// would miss G36: <c>OPENJSON … WITH</c> truncates a value wider than the declared type
/// <b>silently</b> — no warning, no error, and a truncated <c>NVARCHAR</c> is not null. Every string in
/// the generated fixture is exactly its column's declared width and ends in a sentinel character, so a
/// loss of even one character shows up in the comparison.
/// </para>
/// <para>
/// The corpus is permanent — <b>there is no hard delete anywhere in this database</b> — so nothing here
/// is cleaned up afterwards and every test is written to give the same answer on its thousandth run as
/// on its first. <see cref="Runs"/> explains how.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class MergeRoundTripTests
{
    /// <summary>The reserved handler each test writes under. One apiece, so no test reads another's row.</summary>
    private const int FixtureOrdinal = 1;
    private const int AuditOrdinal = 2;
    private const int SoftDeleteOrdinal = 3;
    private const int BatchOrdinal = 4;

    /// <summary>Every payload column, as a theory source.</summary>
    public static TheoryData<string> ColumnNames
    {
        get
        {
            TheoryData<string> data = [];

            foreach (FixtureColumn column in TestData.Columns)
            {
                data.Add(column.Name);
            }

            return data;
        }
    }

    /// <summary>
    /// Every one of the payload columns round-trips: the merge writes it and
    /// <c>dbo.uspGetHandlerSourceDetail</c> reads back exactly what was sent.
    /// </summary>
    /// <param name="columnName">The column under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// One test case per column rather than one test asserting two hundred things, so a failure names
    /// the column instead of naming whichever column happened to fail first. The merge itself runs once,
    /// in <see cref="Merged"/>; the theory only reads its result.
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(ColumnNames))]
    public async Task EveryPayloadColumnRoundTripsExactly(string columnName)
    {
        HandlerSourceDetail detail = await Merged.Value;
        FixtureColumn column = TestData.Columns.Single(c => c.Name == columnName);

        object? actual = typeof(HandlerSourceDetail).GetProperty(columnName)?.GetValue(detail);

        Assert.True(
            actual is not null,
            $"{column} came back null. Either the OPENJSON path does not match the property the " +
            "fixture emitted -- a mis-cased or renamed path shreds to NULL with no error -- or " +
            "dbo.uspGetHandlerSourceDetail does not project this column.");

        AssertMatches(column, actual!);
    }

    /// <summary>
    /// The not-matched branch stamps both audit pairs, and the matched branch moves
    /// <c>auditModifiedDateUtc</c> without touching <c>auditCreatedDateUtc</c>.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// This is the acceptance item "a test proves the merge procedure's audit stamping in both the
    /// matched and not-matched branches". The failure it guards is not a crash: a <c>WHEN MATCHED</c>
    /// branch that forgot <c>auditModifiedDateUtc</c> leaves the column at the value the INSERT default
    /// gave it, so the audit trail says the row has never changed since the day it appeared. It is
    /// right for one row out of two and it reads as ordinary data.
    /// </para>
    /// <para>
    /// The created stamp is asserted UNCHANGED as well, because the other half of the same mistake is a
    /// MERGE that re-stamps creation on update — which loses when the row first appeared, and loses it
    /// irrecoverably.
    /// </para>
    /// <para>
    /// The not-matched branch needs a key that has never existed, which is why this test asks for the
    /// next unused sequence rather than retiring an existing row. A retired row still matches.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task TheMergeStampsCreationOnceAndModificationEveryTime()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = TestData.HandlerId(AuditOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        HandlerSourceDetail inserted = await MergeAndReadAsync(
            scope.Context, loadRunId, handlerId, sequence, expected: "Inserted");

        // One column differs, so the merge's change detection has something to find. Sent identically,
        // the record would be already current, the procedure would correctly report nothing, and this
        // test would be asserting the opposite of what it means to.
        HandlerSourceDetail updated = await MergeAndReadAsync(
            scope.Context, loadRunId, handlerId, sequence, expected: "Updated",
            mutate: h => h["comments"] = "A second merge, differing in exactly one column.");

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(inserted.HandlerSourceId, updated.HandlerSourceId);
        Assert.Equal(inserted.AuditCreatedDateUtc, updated.AuditCreatedDateUtc);
        Assert.Equal(inserted.AuditCreatedBy, updated.AuditCreatedBy, StringComparer.Ordinal);

        Assert.True(
            updated.AuditModifiedDateUtc > inserted.AuditModifiedDateUtc,
            "auditModifiedDateUtc did not move on the matched branch: it was " +
            $"{inserted.AuditModifiedDateUtc:O} and is still {updated.AuditModifiedDateUtc:O}. The " +
            "column defaults only on INSERT, so a WHEN MATCHED branch that does not set it leaves the " +
            "audit trail claiming the row has not changed since it first appeared.");

        Assert.True(
            inserted.AuditModifiedDateUtc >= inserted.AuditCreatedDateUtc,
            $"On the not-matched branch auditModifiedDateUtc ({inserted.AuditModifiedDateUtc:O}) is " +
            $"before auditCreatedDateUtc ({inserted.AuditCreatedDateUtc:O}).");

        Assert.False(string.IsNullOrWhiteSpace(inserted.AuditCreatedBy));
        Assert.False(string.IsNullOrWhiteSpace(updated.AuditModifiedBy));
    }

    /// <summary>
    /// A batch of many behaves as a batch of one repeated, and a batch that changes nothing reports
    /// nothing.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The set-based requirement, checked on the widest procedure rather than inferred from its
    /// signature. Three versions of one handler in a single call, only one of them current.
    /// </para>
    /// <para>
    /// The second half is the more interesting assertion. The procedure returns one row per record
    /// inserted or updated and nothing for a record that was already current, and its header says that
    /// subtraction is how the loader gets its unchanged count. So an identical re-send must return
    /// <b>zero</b> rows — not three. A procedure that reported all three as updated would inflate every
    /// incremental run's changed count to the size of the batch, and the run would look like a day's
    /// work when nothing had happened.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ABatchOfManyReportsOnlyWhatItChanged()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = TestData.HandlerId(BatchOrdinal);
        int loadRunId = await Runs.StartAsync(scope.Context);
        int[] sequences = [1, 2, 3];

        // Pinned, not defaulted to now. RetrievedDateUtc is envelope metadata rather than a merged
        // column -- the second send is still correctly reported as unchanged with a later one, which
        // is itself worth knowing -- but it IS part of the serialised payload, so leaving it to the
        // clock would make the digest differ between two otherwise identical batches and the
        // assertion below would be measuring the clock.
        DateTimeOffset retrieved = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await Runs.RetireAsync(scope.Context, loadRunId, handlerId, sequences);

        MergeBatchResult first = await scope.Context.MergeHandlerSourceBatchAsync(
            [.. sequences.Select(s => TestData.Envelope(handlerId, s, s == 3, retrieved))],
            loadRunId);

        Assert.Equal(3, first.Batch.ElementCount);
        Assert.Equal(3, first.Outcomes.Count);
        Assert.Equal(sequences, first.Outcomes.Select(o => o.Sequence).Order());
        Assert.All(first.Outcomes, o => Assert.Equal(handlerId, o.HandlerId, StringComparer.Ordinal));
        Assert.All(
            first.Outcomes, o => Assert.Equal(TestData.SourceType, o.SourceType, StringComparer.Ordinal));

        MergeBatchResult again = await scope.Context.MergeHandlerSourceBatchAsync(
            [.. sequences.Select(s => TestData.Envelope(handlerId, s, s == 3, retrieved))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(3, again.Batch.ElementCount);
        Assert.Empty(again.Outcomes);

        // The identical payload twice must produce the identical digest, or the loader's unchanged
        // count is being compared against a batch identity that moves on its own.
        Assert.Equal(first.Batch.Sha256, again.Batch.Sha256, StringComparer.Ordinal);

        Page<HandlerSourceHistoryRow> history = await scope.Context
            .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery
            {
                HandlerId = handlerId,
                Skip = 0,
                Take = 50,
            });

        Assert.Equal(3, history.TotalRows);
        Assert.Equal(3, history.Items.Count);
        Assert.Single(history.Items, r => r.CurrentRecord == true);
    }

    /// <summary>An empty batch is a no-op rather than an error, and sends nothing.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Script 400's own comment: turning a harmless no-op into a failed run would make an unattended
    /// load report a problem it does not have.
    /// </remarks>
    [IntegrationFact]
    public async Task AnEmptyBatchIsANoOp()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        int loadRunId = await Runs.StartAsync(scope.Context);

        MergeBatchResult result = await scope.Context
            .MergeHandlerSourceBatchAsync([], loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Empty(result.Outcomes);
        Assert.Equal(0, result.Batch.ElementCount);
        Assert.Equal("[]", result.Batch.Json, StringComparer.Ordinal);
    }

    /// <summary>
    /// A soft-deleted version is absent from every read that can see a handler, and merging it again
    /// brings it back.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The acceptance item is "a test proves that a soft-deleted row is absent from every read
    /// procedure's output", and the reason it is worth a test rather than a review is that the failure
    /// is silent in the direction that looks safest: a read that forgot <c>IsDeleted = 0</c> returns
    /// <i>more</i> rows, all of them real, all of them well-formed, none of them wrong-looking.
    /// </para>
    /// <para>
    /// Checked on the four reads that can reach a handler version — the grid, the history, the search
    /// and the detail. The two <c>logs</c> reads take an explicit <c>IncludeDeleted</c> and are covered
    /// in <see cref="ReadProcedureTests"/>, where the opt-in itself is the thing being tested.
    /// </para>
    /// <para>
    /// The revival at the end is not a separate concern. Script 400 tests <c>tgt.IsDeleted = 1</c>
    /// before any column comparison precisely so that a record EPA publishes again comes back, and a
    /// soft-deleted row whose columns all still match would otherwise stay invisible for good.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ASoftDeletedVersionIsAbsentFromEveryHandlerReadUntilItIsMergedAgain()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = TestData.HandlerId(SoftDeleteOrdinal);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await Runs.RetireAsync(scope.Context, loadRunId, handlerId, [1]);

        HandlerSourceDetail present = await MergeAndReadAsync(
            scope.Context, loadRunId, handlerId, sequence: 1, expected: null);

        SoftDeleteResult deleted = await scope.Context.SoftDeleteHandlerSourceSetAsync(
            loadRunId, [TestData.Key(handlerId, 1)], TestData.SoftDeleteReason);

        Assert.Equal(1, deleted.RowsAffected);

        Assert.Null(await scope.Context
            .GetHandlerSourceDetailAsync(present.HandlerSourceId));

        Page<HandlerSourceGridRow> grid = await scope.Context
            .GetHandlerSourcePageAsync(new HandlerSourcePageQuery
            {
                HandlerId = handlerId,
                Skip = 0,
                Take = 50,
            });

        Assert.DoesNotContain(grid.Items, r => r.HandlerSourceId == present.HandlerSourceId);

        Page<HandlerSourceHistoryRow> history = await scope.Context
            .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery
            {
                HandlerId = handlerId,
                Skip = 0,
                Take = 50,
            });

        Assert.DoesNotContain(history.Items, r => r.HandlerSourceId == present.HandlerSourceId);

        Page<HandlerSourceSearchRow> search = await scope.Context
            .SearchHandlerSourceAsync(handlerId, 0, 50);

        Assert.DoesNotContain(search.Items, r => r.HandlerSourceId == present.HandlerSourceId);

        // Deleting it again changes nothing. A procedure that re-marked an already-deleted row would
        // move auditDeletedDateUtc every time a retry ran, so the record of WHEN the deletion happened
        // would be the time of the last retry.
        SoftDeleteResult repeat = await scope.Context.SoftDeleteHandlerSourceSetAsync(
            loadRunId, [TestData.Key(handlerId, 1)], TestData.SoftDeleteReason);

        Assert.Equal(0, repeat.RowsAffected);

        HandlerSourceDetail revived = await MergeAndReadAsync(
            scope.Context, loadRunId, handlerId, sequence: 1, expected: "Updated");

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(present.HandlerSourceId, revived.HandlerSourceId);
    }

    /// <summary>
    /// The merge that <see cref="EveryPayloadColumnRoundTripsExactly"/> reads, run once for the whole
    /// theory.
    /// </summary>
    /// <remarks>
    /// A <see cref="Lazy{T}"/> over a task rather than a class fixture, because xUnit constructs a new
    /// test-class instance per test case and two hundred merges of the same payload would spend minutes
    /// proving one thing. <see cref="IntegrationSuite"/> serialises the suite, so nothing else is
    /// writing this row while it runs.
    /// </remarks>
    private static Lazy<Task<HandlerSourceDetail>> Merged { get; } = new(MergeFixtureAsync);

    private static async Task<HandlerSourceDetail> MergeFixtureAsync()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = TestData.HandlerId(FixtureOrdinal);
        int loadRunId = await Runs.StartAsync(scope.Context);

        // Retired first, so the merge below reports the record whether or not a previous run of this
        // suite already wrote it. See Runs.
        await Runs.RetireAsync(scope.Context, loadRunId, handlerId, [1]);

        HandlerSourceDetail detail = await MergeAndReadAsync(
            scope.Context, loadRunId, handlerId, sequence: 1, expected: null);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        return detail;
    }

    /// <summary>Merges one fully-populated version and reads it back in full.</summary>
    private static async Task<HandlerSourceDetail> MergeAndReadAsync(
        RCRAInfoContext context,
        int loadRunId,
        string handlerId,
        int sequence,
        string? expected,
        Action<JsonObject>? mutate = null)
    {
        MergeBatchResult result = await context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, sequence, mutate: mutate)],
            loadRunId);

        MergeOutcomeRow outcome = Assert.Single(result.Outcomes);

        Assert.Equal(handlerId, outcome.HandlerId, StringComparer.Ordinal);
        Assert.Equal(sequence, outcome.Sequence);

        if (expected is not null)
        {
            Assert.Equal(expected, outcome.Outcome, StringComparer.Ordinal);
        }

        HandlerSourceDetail? detail = await context
            .GetHandlerSourceDetailAsync(outcome.HandlerSourceId);

        Assert.NotNull(detail);
        return detail;
    }

    /// <summary>Compares one round-tripped value against what the fixture sent.</summary>
    /// <remarks>
    /// Typed per SQL type rather than compared as text, because the differences that matter are exactly
    /// the ones a string comparison would blur: a <c>DATE</c> that arrived with a UTC offset applied, a
    /// <c>BIT</c> read from the <i>text</i> of a JSON boolean, and a <c>FLOAT</c> that lost its fraction
    /// to an integer column.
    /// </remarks>
    private static void AssertMatches(FixtureColumn column, object actual)
    {
        switch (column.Expected.ValueKind)
        {
            case JsonValueKind.String when column.SqlType == "DATE":
                Assert.Equal(
                    DateOnly.Parse(column.Expected.GetString()!, CultureInfo.InvariantCulture),
                    Assert.IsType<DateOnly>(actual));
                break;

            case JsonValueKind.String:
                string sent = column.Expected.GetString()!;
                string back = Assert.IsType<string>(actual);

                // The fixture's own contract, asserted where it is relied upon: a value shorter than
                // its column could not detect truncation at all.
                Assert.Equal(column.Width, sent.Length);

                Assert.True(
                    string.Equals(sent, back, StringComparison.Ordinal),
                    $"{column} came back changed. Sent {sent.Length} character(s), got {back.Length}. " +
                    (back.Length < sent.Length
                        ? "It is SHORTER, which is G36: OPENJSON ... WITH truncates a value wider than " +
                          "the declared type silently -- no warning, no error, and the result is not null."
                        : "Same length or longer, so this is a mapping problem rather than a width one.") +
                    $" Sent tail: '{Tail(sent)}'; got tail: '{Tail(back)}'.");
                break;

            case JsonValueKind.True or JsonValueKind.False:
                Assert.Equal(column.Expected.GetBoolean(), Assert.IsType<bool>(actual));
                break;

            case JsonValueKind.Number when column.SqlType == "FLOAT":
                Assert.Equal(column.Expected.GetDouble(), Assert.IsType<double>(actual), 6);
                break;

            case JsonValueKind.Number when column.SqlType == "BIGINT":
                Assert.Equal(column.Expected.GetInt64(), Assert.IsType<long>(actual));
                break;

            case JsonValueKind.Number:
                Assert.Equal(column.Expected.GetInt32(), Assert.IsType<int>(actual));
                break;

            default:
                Assert.Fail(
                    $"{column} has a fixture value of kind {column.Expected.ValueKind}, which this " +
                    "comparison has not been told how to check. Add the kind deliberately rather than " +
                    "letting the column go unasserted.");
                break;
        }
    }

    private static string Tail(string value) =>
        value.Length <= 12 ? value : "..." + value[^11..];
}
