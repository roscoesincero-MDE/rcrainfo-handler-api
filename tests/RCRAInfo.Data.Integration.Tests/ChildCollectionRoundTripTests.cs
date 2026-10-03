using System.Globalization;
using System.Text.Json;

using Microsoft.Data.SqlClient;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The other 167 columns: every payload field of every one of the 18 child tables, sent once and read
/// back one column at a time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MergeRoundTripTests"/> does this for the 210 columns of <c>dbo.HandlerSource</c>. Its
/// reasoning applies unchanged here and it applies <i>harder</i>, because a child column shreds through a
/// nested <c>CROSS APPLY OPENJSON</c> over the array element, so its path is relative to that element and
/// a mis-cased segment there has one more place to hide.
/// </para>
/// <para>
/// <b>Nothing else in this suite would notice.</b> <see cref="ChildCollectionTests"/> is thorough about
/// multiplicity and retirement — order, a shorter array, an empty one, an absent property, revival,
/// grandchild parentage — and it asserts all of it through hand-written elements carrying one or two
/// properties apiece: <c>name</c>, <c>type.code</c>, <c>facilityCode.code</c>, a bare code string. Every
/// one of those tests would pass exactly as it does today with <b>165 of the 167 child columns
/// permanently null</b>, because <c>OPENJSON</c> matches property names case-sensitively regardless of
/// collation and a path that matches nothing shreds to <c>NULL</c> rather than erroring. That is the gap
/// this class closes, and it is the whole reason
/// <c>build/generate_payload_fixture.py</c> now emits the collections.
/// </para>
/// <para>
/// The assertion is on the VALUE and not on non-nullity, for G36's reason: <c>OPENJSON … WITH</c>
/// truncates a value wider than the declared type <b>silently</b>, and a truncated <c>NVARCHAR</c> is not
/// null. Every generated string is exactly its column's declared width and ends in a sentinel.
/// </para>
/// <para>
/// Read by direct query rather than through a procedure, because <b>no read procedure projects a child
/// table</b> — <c>dbo.uspGetHandlerSourceDetail</c> returns the parent row alone. That is not a gap in
/// the reads: the web app is monitoring only in Phase 1 (AR2) and nothing yet asks for a handler's
/// owners. It does mean the merge is the only thing under test here, which is what the acceptance item
/// asks for.
/// </para>
/// <para>
/// The corpus is permanent — <b>there is no hard delete anywhere in this database</b> — so this class
/// takes a sequence the handler has never used, exactly as <see cref="ChildCollectionTests"/> does. That
/// guarantees a parent with no child rows to start from without deleting anything, and it means the
/// thousandth run reads its own rows rather than a previous run's.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class ChildCollectionRoundTripTests
{
    /// <summary>The reserved handler this class writes under. Ordinals 1-18 belong to the other classes.</summary>
    private const int RoundTripOrdinal = 20;

    /// <summary>Pinned rather than defaulted to the clock, so two runs send one payload.</summary>
    private static readonly DateTimeOffset Retrieved = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>Every child payload column, as <c>table.column</c>, for the theory.</summary>
    public static TheoryData<string, string> CollectionColumns
    {
        get
        {
            TheoryData<string, string> data = [];

            foreach (FixtureChildTable collection in TestData.Collections)
            {
                foreach (FixtureColumn column in collection.Columns)
                {
                    data.Add(collection.Table, column.Name);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Every payload column of every child table round-trips: the merge shreds it out of the element and
    /// the stored row holds exactly what was sent.
    /// </summary>
    /// <param name="table">The child table.</param>
    /// <param name="columnName">The column under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// One case per column rather than one test asserting 167 things, so a failure names the column
    /// instead of naming whichever one happened to fail first — and so that a whole collection shredding
    /// to nulls reports as 35 named failures rather than as one. The merge and the 18 reads happen once,
    /// in <see cref="Stored"/>.
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(CollectionColumns))]
    public async Task EveryChildPayloadColumnRoundTripsExactly(string table, string columnName)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> stored = await Stored.Value;

        FixtureChildTable collection = TestData.Collections
            .Single(c => string.Equals(c.Table, table, StringComparison.Ordinal));

        FixtureColumn column = collection.Columns
            .Single(c => string.Equals(c.Name, columnName, StringComparison.Ordinal));

        Assert.True(
            stored.TryGetValue(table, out IReadOnlyDictionary<string, object?>? row),
            $"{collection} wrote no row at OrdinalPosition 0. The fixture sends exactly one element " +
            "for every collection, so an empty table means the merge did not shred this collection at " +
            "all -- a renamed collection property, or a JSON_PATH_EXISTS gate that closed over it.");

        object? actual = row![columnName];

        Assert.True(
            actual is not null,
            $"{collection.Describe(column)} came back null. The element was written, so the " +
            "collection's own path is right and this is the column's path WITHIN the element: " +
            "OPENJSON matches property names case-sensitively regardless of collation, and a path " +
            "matching nothing shreds to NULL with no error.");

        AssertMatches(collection, column, actual!);
    }

    /// <summary>
    /// Every collection put its element where the schema says, and the three nested ones hang off their
    /// own parent element rather than off the handler.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The theory above reads each table through the join the manifest describes, so it can only find a
    /// row that is already attached the way the manifest expects. This test states the count and the
    /// shape separately, because the theory's failure message for a whole missing collection is per
    /// column and says nothing about how many collections there are.
    /// </para>
    /// <para>
    /// 18 is not a number to leave implicit. If EPA adds a nineteenth collection, the generator refuses
    /// outright — <c>CHILD_ORDER</c> is a decision the spec may not edit silently — but if a collection
    /// were dropped from the fixture's manifest while the table survived, every one of its columns would
    /// simply stop being a test case and the suite would go green with fewer assertions than it had.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task EveryCollectionInTheSchemaIsPopulatedAndTheNestedOnesAreNested()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> stored = await Stored.Value;

        Assert.Equal(18, TestData.Collections.Count);
        Assert.Equal(167, TestData.Collections.Sum(c => c.Columns.Count));

        Assert.Equal(
            [.. TestData.Collections.Select(c => c.Table).Order()],
            [.. stored.Keys.Order()]);

        // The three that hang off another child, named rather than counted: their join runs through the
        // parent element's OrdinalPosition, so a manifest that had flattened them to HandlerSourceId
        // would still read a row -- the right one, here, because there is only one element -- and the
        // reason it is right would have stopped being the schema.
        Assert.Equal(
            [
                "HandlerSourceEpisodicWasteFederalWasteCode",
                "HandlerSourceEpisodicWasteStateWasteCode",
                "HandlerSourceHsmActivityWasteCode",
            ],
            [.. TestData.Collections.Where(c => c.IsNested).Select(c => c.Table).Order()]);

        Assert.Equal(5, TestData.Collections.Count(c => c.Bare));

        // A bare-string collection is one column shredded from arr.[value] alone, so a manifest entry
        // claiming to be bare with two columns would send an object where the merge reads a string.
        Assert.All(
            TestData.Collections.Where(c => c.Bare),
            c => Assert.Equal("$", Assert.Single(c.Columns).Path, StringComparer.Ordinal));
    }

    /// <summary>
    /// The one merge the theory reads, and the 18 reads of its result.
    /// </summary>
    /// <remarks>
    /// A <see cref="Lazy{T}"/> over a task, for the reason <see cref="MergeRoundTripTests"/> gives: xunit
    /// constructs a new test-class instance per case, and 167 merges of a 210 KB payload would spend
    /// minutes proving one thing. <see cref="IntegrationSuite"/> serialises the suite, so nothing else is
    /// writing this version while it runs.
    /// </remarks>
    private static Lazy<Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>>> Stored
    { get; } = new(MergeAndReadAsync);

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>>
        MergeAndReadAsync()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(RoundTripOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        // currentRecord is false, for ChildCollectionTests' reason: this class takes a fresh sequence on
        // every run, and a version claiming to be current would leave the handler with one more current
        // record each time -- an untruth about EPA's data, planted by a test that does not care either
        // way.
        await scope.Context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, sequence, currentRecord: false, Retrieved)],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Dictionary<string, IReadOnlyDictionary<string, object?>> stored = [];

        foreach (FixtureChildTable collection in TestData.Collections)
        {
            IReadOnlyDictionary<string, object?>? row =
                await ReadElementAsync(probe, collection, handlerId, sequence);

            if (row is not null)
            {
                stored[collection.Table] = row;
            }
        }

        return stored;
    }

    /// <summary>Reads the single element the fixture sent for one collection, or null if it is absent.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="collection">The collection, carrying its table and its parentage.</param>
    /// <param name="handlerId">The reserved handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>Every payload column of the row, by name, or null when there is no row.</returns>
    /// <remarks>
    /// Filtered on <c>IsDeleted = 0</c>, unlike <see cref="ChildCollectionTests"/>' helpers. Retirement is
    /// their subject and they must be able to see a retired row; here the sequence has never been written
    /// before, so a retired row at ordinal 0 could only mean the merge retired what it had just
    /// inserted — and reading it as a pass would hide that.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, object?>?> ReadElementAsync(
        SqlConnection probe, FixtureChildTable collection, string handlerId, int sequence)
    {
        string sql = ElementQuery(collection, handlerId, sequence);

#pragma warning disable CA2100 // Composed from the generated manifest and this suite's own reserved key; see Identifier and Version.
        await using SqlCommand command = new(sql, probe);
#pragma warning restore CA2100

        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        Dictionary<string, object?> row = [];

        foreach (FixtureColumn column in collection.Columns)
        {
            object value = reader[column.Name];
            row[column.Name] = value is DBNull ? null : value;
        }

        // One element per collection, so a second row is a duplicate the filtered unique index should
        // have made impossible -- worth failing on rather than ignoring, because the theory would
        // otherwise assert against the first of two and report nothing.
        Assert.False(
            await reader.ReadAsync(),
            $"{collection} returned more than one row at OrdinalPosition 0.");

        return row;
    }

    /// <summary>
    /// The query for one collection's element: its payload columns, joined up to the handler through
    /// whatever chain the manifest describes.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <param name="handlerId">The reserved handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>The query.</returns>
    /// <remarks>
    /// <para>
    /// Built from the manifest rather than written out 18 times, for the same reason the fixture is
    /// generated: 18 hand-written queries naming 167 columns would be a third copy of the mapping, and a
    /// column this one forgot to <c>SELECT</c> would fail with an obvious error while a column the
    /// <i>fixture</i> forgot would pass in silence.
    /// </para>
    /// <para>
    /// Every ordinal in the chain is 0 because the fixture sends exactly one element at every level. It
    /// is pinned at each level rather than only at the leaf: for a nested collection, leaving the
    /// parent's ordinal unconstrained would make the query correct only as long as there is one parent
    /// element, so it would go on passing after the fixture grew and stop meaning what it says.
    /// </para>
    /// </remarks>
    private static string ElementQuery(FixtureChildTable collection, string handlerId, int sequence)
    {
        List<FixtureChildTable> chain = [collection];

        while (chain[^1].IsNested)
        {
            FixtureChildTable parent = TestData.Collections.Single(c =>
                string.Equals(c.Table, chain[^1].ParentTable, StringComparison.Ordinal));

            chain.Add(parent);
        }

        List<string> joins = [];

        for (int level = 1; level < chain.Count; level++)
        {
            joins.Add(
                $"JOIN dbo.{Identifier(chain[level].Table)} AS t{level} " +
                $"ON t{level}.{Identifier(chain[level].IdentityColumn)} " +
                $"= t{level - 1}.{Identifier(chain[level - 1].ParentColumn)} " +
                $"AND t{level}.OrdinalPosition = 0 AND t{level}.IsDeleted = 0");
        }

        joins.Add(
            $"JOIN dbo.HandlerSource AS h ON h.HandlerSourceId " +
            $"= t{chain.Count - 1}.{Identifier(chain[^1].ParentColumn)}");

        string columns = string.Join(
            ", ", collection.Columns.Select(c => $"t0.{Identifier(c.Name)}"));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             SELECT {columns}
               FROM dbo.{Identifier(collection.Table)} AS t0
                    {string.Join("\n                    ", joins)}
              WHERE h.HandlerId  = N'{Key(handlerId)}'
                AND h.SourceType = N'{TestData.SourceType}'
                AND h.Sequence   = {sequence}
                AND t0.OrdinalPosition = 0
                AND t0.IsDeleted = 0;
             """);
    }

    /// <summary>
    /// Asserts that an identifier out of the generated manifest is one, and returns it.
    /// </summary>
    /// <param name="name">The table or column name.</param>
    /// <returns>The same name.</returns>
    /// <remarks>
    /// These names come from <c>build/generate_payload_fixture.py</c>, which derives them from the pinned
    /// specification and cannot emit anything else — so a name that failed this check would be a defect in
    /// the generator rather than an injection. The check is what makes the interpolation defensible
    /// anyway, and it stays in front of it. <c>QUOTENAME</c> would be the alternative and is worse here:
    /// it would let a name with a bracket through as a legal quoted identifier instead of refusing it.
    /// </remarks>
    private static string Identifier(string name)
    {
        Assert.NotEmpty(name);
        Assert.All(name, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"'{name}' is not an identifier."));

        return name;
    }

    /// <summary>Asserts that a handler identifier is one of this suite's reserved ones, and returns it.</summary>
    /// <param name="handlerId">The identifier.</param>
    /// <returns>The same identifier.</returns>
    /// <remarks>
    /// <c>ZZTEST</c> followed by six digits and nothing else — the same assertions
    /// <see cref="ChildCollectionTests"/> puts in front of its own interpolation, and for the same reason.
    /// </remarks>
    private static string Key(string handlerId)
    {
        Assert.StartsWith(TestData.HandlerIdPrefix, handlerId, StringComparison.Ordinal);
        Assert.Equal(12, handlerId.Length);
        Assert.All(handlerId[TestData.HandlerIdPrefix.Length..], c => Assert.True(char.IsAsciiDigit(c)));

        return handlerId;
    }

    /// <summary>Compares one round-tripped child value against what the fixture sent.</summary>
    /// <remarks>
    /// Typed per SQL type rather than compared as text, for <see cref="MergeRoundTripTests"/>' reason: a
    /// string comparison would blur a <c>DATE</c> that arrived with an offset applied, a <c>BIT</c> read
    /// from the text of a JSON boolean, and a <c>FLOAT</c> that lost its fraction. Read from
    /// <c>SqlDataReader</c> rather than from a projected type, so the CLR types are the provider's.
    /// </remarks>
    private static void AssertMatches(FixtureChildTable collection, FixtureColumn column, object actual)
    {
        string where = collection.Describe(column);

        switch (column.Expected.ValueKind)
        {
            case JsonValueKind.String when column.SqlType == "DATE":
                Assert.Equal(
                    DateOnly.Parse(column.Expected.GetString()!, CultureInfo.InvariantCulture),
                    DateOnly.FromDateTime(Assert.IsType<DateTime>(actual)));
                break;

            case JsonValueKind.String:
                string sent = column.Expected.GetString()!;
                string back = Assert.IsType<string>(actual);

                // The fixture's own contract, asserted where it is relied upon: a value shorter than its
                // column could not detect truncation at all.
                Assert.Equal(column.Width, sent.Length);

                Assert.True(
                    string.Equals(sent, back, StringComparison.Ordinal),
                    $"{where} came back changed. Sent {sent.Length} character(s), got {back.Length}. " +
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
                    $"{where} has a fixture value of kind {column.Expected.ValueKind}, which this " +
                    "comparison has not been told how to check. Add the kind deliberately rather than " +
                    "letting the column go unasserted.");
                break;
        }
    }

    private static string Tail(string value) =>
        value.Length <= 12 ? value : "..." + value[^11..];
}
