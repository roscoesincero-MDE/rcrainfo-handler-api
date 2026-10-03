using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Tests;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The reserved identity this suite writes under, and the fully-populated payload it sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in this database is ever hard-deleted</b>, so every row these tests write is permanent.
/// The corpus therefore has to be one that can never collide with real data and that a human can
/// separate exactly: every handler identifier begins <c>ZZ</c>, and <c>ZZ</c> is not a state, so EPA
/// cannot issue one. <c>ActivityLocation</c> is <c>ZZ</c> for the same reason — MD is the only value in
/// scope (G2), so no genuine row can carry it.
/// </para>
/// <para>
/// The payload itself is <b>generated</b>, by <c>build/generate_payload_fixture.py</c>, from the same
/// pinned swagger specification that generated <c>dbo.HandlerSource</c>, its 18 child tables and
/// script 400. A hand-written fixture of 377 values would be a second copy of that mapping, and the
/// way it would fail is the way this suite exists to catch: a field EPA adds appears in the table and
/// in the shredding, the fixture never mentions it, and the round-trip passes while that column has
/// never once been populated. <c>--check</c> is a guardrail, so the fixture cannot fall behind the
/// spec silently.
/// </para>
/// <para>
/// It covers both halves of the payload: the 210 scalar columns of <c>dbo.HandlerSource</c>, in
/// <see cref="Columns"/>, and <b>one element in every one of the 18 collections</b>, in
/// <see cref="Collections"/> — 167 more columns, grandchild arrays nested inside their own parent
/// element. One element apiece and not two, because multiplicity is
/// <see cref="ChildCollectionTests"/>'s subject and a mis-cased child path shreds to <c>NULL</c> in
/// element 0 exactly as it would in element 1.
/// </para>
/// <para>
/// It is read from the working tree rather than from the build output, for the reason
/// <see cref="TestPaths"/> gives: a copy is a thing that can be stale, and a stale fixture would make
/// these tests agree with a spec nobody is using any more.
/// </para>
/// </remarks>
public static class TestData
{
    /// <summary>The state code every row this suite writes carries. Not a state; EPA cannot issue it.</summary>
    public const string ActivityLocation = "ZZ";

    /// <summary>The source type the fixture uses.</summary>
    public const string SourceType = "Z";

    /// <summary>The prefix every handler identifier in the corpus starts with.</summary>
    public const string HandlerIdPrefix = "ZZTEST";

    /// <summary>The reason recorded on every soft delete this suite performs.</summary>
    public const string SoftDeleteReason = "DA5 round-trip test corpus.";

    private const string FixtureFileName = "FullHandlerSource.json";

    // Declared ahead of the public members that read it, and not for style: static field and
    // auto-property initialisers run in TEXTUAL order, so a Fixture declared below them would still be
    // null when they ran. The two derived from it follow immediately for the same reason.
    private static readonly JsonDocument Fixture = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "tests", "RCRAInfo.Data.Integration.Tests", "Fixtures",
            FixtureFileName)));

    /// <summary>The <c>handler</c> subtree as text, so each call can parse its own copy.</summary>
    private static readonly string HandlerJson =
        Fixture.RootElement.GetProperty("handler").GetRawText();

    /// <summary>A reserved handler identifier, distinct per ordinal.</summary>
    /// <param name="ordinal">1 through 999999.</param>
    /// <returns>A 12-character identifier, e.g. <c>ZZTEST000007</c>.</returns>
    /// <remarks>
    /// Exactly 12 characters, which is the declared width of <c>dbo.HandlerSource.HandlerId</c>. That
    /// is deliberate: a key one character short of the column would be the one value in the corpus
    /// whose truncation nothing could detect.
    /// </remarks>
    public static string HandlerId(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordinal, 999999);

        return HandlerIdPrefix + ordinal.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The identifier the generated fixture carries.</summary>
    public static string FixtureHandlerId { get; } = Fixture.RootElement.GetProperty("handlerId").GetString()!;

    /// <summary>The 210 <c>dbo.HandlerSource</c> payload columns, with the value each must read back.</summary>
    public static IReadOnlyList<FixtureColumn> Columns { get; } = ReadColumns();

    /// <summary>The 18 collections, with the table each lands in and the 167 columns between them.</summary>
    public static IReadOnlyList<FixtureChildTable> Collections { get; } = ReadCollections();

    /// <summary>
    /// An envelope carrying the fully-populated handler, under the given key.
    /// </summary>
    /// <param name="handlerId">The identifier to write. Must come from <see cref="HandlerId"/>.</param>
    /// <param name="sequence">The version number.</param>
    /// <param name="currentRecord">Whether this version is the current one.</param>
    /// <param name="retrievedDateUtc">When it was retrieved. Now, unless a test needs otherwise.</param>
    /// <param name="mutate">
    /// An optional edit applied to the handler object after the key is set, for the tests that need one
    /// field to differ between two merges.
    /// </param>
    /// <returns>The envelope, ready to send.</returns>
    /// <remarks>
    /// The handler is re-parsed from text on every call rather than shared. A
    /// <see cref="JsonElement"/> borrows the buffer of the <see cref="JsonDocument"/> that owns it, so
    /// a shared, mutated document would give two tests two views of one object — and these tests run
    /// in parallel across classes by default.
    /// </remarks>
    public static HandlerEnvelope Envelope(
        string handlerId,
        int sequence,
        bool currentRecord = true,
        DateTimeOffset? retrievedDateUtc = null,
        Action<JsonObject>? mutate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);

        Assert.StartsWith(HandlerIdPrefix, handlerId, StringComparison.Ordinal);

        JsonObject handler = (JsonObject)JsonNode.Parse(HandlerJson)!;

        handler["handlerId"] = handlerId;
        handler["activityLocation"] = ActivityLocation;
        handler["sequence"] = sequence;
        handler["currentRecord"] = currentRecord;

        mutate?.Invoke(handler);

        // Parsed rather than handed over as a node: HandlerEnvelope.Handler is a JsonElement, which is
        // what reaches OPENJSON verbatim.
        using JsonDocument document = JsonDocument.Parse(handler.ToJsonString());

        return new HandlerEnvelope
        {
            RetrievedDateUtc = retrievedDateUtc ?? DateTimeOffset.UtcNow,

            // Cloned because the document is disposed at the end of this method and an un-cloned
            // element would then be reading a returned buffer.
            Handler = document.RootElement.Clone(),
        };
    }

    /// <summary>The natural key of an envelope, as the other five payload types spell it.</summary>
    /// <param name="handlerId">The identifier.</param>
    /// <param name="sequence">The version number.</param>
    /// <returns>The key element.</returns>
    public static HandlerKeyElement Key(string handlerId, int sequence) =>
        new() { HandlerId = handlerId, SourceType = SourceType, Sequence = sequence };

    private static List<FixtureColumn> ReadColumns()
    {
        List<FixtureColumn> columns = [.. Manifest(Fixture.RootElement.GetProperty("columns"))];

        int declared = Fixture.RootElement.GetProperty("columnCount").GetInt32();

        // The manifest and its own count, from the same file. Cheap, and it means a truncated or
        // half-written fixture is a startup failure rather than a suite that checks 3 columns.
        Assert.Equal(declared, columns.Count);
        Assert.NotEmpty(columns);

        return columns;
    }

    private static List<FixtureChildTable> ReadCollections()
    {
        List<FixtureChildTable> collections =
        [
            .. Fixture.RootElement.GetProperty("collections").EnumerateArray().Select(c =>
                new FixtureChildTable(
                    c.GetProperty("path").GetString()!,
                    c.GetProperty("table").GetString()!,
                    c.GetProperty("identityColumn").GetString()!,
                    c.GetProperty("parentTable").GetString()!,
                    c.GetProperty("parentColumn").GetString()!,
                    c.GetProperty("bare").GetBoolean(),
                    [.. Manifest(c.GetProperty("columns"))]))
        ];

        Assert.Equal(Fixture.RootElement.GetProperty("collectionCount").GetInt32(), collections.Count);
        Assert.Equal(
            Fixture.RootElement.GetProperty("collectionColumnCount").GetInt32(),
            collections.Sum(c => c.Columns.Count));

        // The total, asserted against the two halves rather than trusted from the file. A fixture
        // whose parts had drifted apart would still satisfy each half's own count.
        Assert.Equal(
            Fixture.RootElement.GetProperty("payloadColumnCount").GetInt32(),
            Columns.Count + collections.Sum(c => c.Columns.Count));

        Assert.NotEmpty(collections);

        return collections;
    }

    private static IEnumerable<FixtureColumn> Manifest(JsonElement columns) =>
        columns.EnumerateArray().Select(c => new FixtureColumn(
            c.GetProperty("name").GetString()!,
            c.GetProperty("path").GetString()!,
            c.GetProperty("sqlType").GetString()!,
            c.GetProperty("nullable").GetBoolean(),
            c.GetProperty("ordinal").GetInt32(),
            c.GetProperty("expected").Clone()));
}

/// <summary>One column of the generated fixture, and the value the round-trip must read back.</summary>
/// <param name="Name">The column in its table, which for the parent is also the C# property name.</param>
/// <param name="Path">
/// The JSON path the column shreds from, e.g. <c>type.code</c>. For a parent column that is relative to
/// the handler object; for a child column it is relative to the <b>array element</b>, and <c>$</c> means
/// the element itself — the bare-string shape, which 5 of the 18 collections have.
/// </param>
/// <param name="SqlType">The declared SQL type, e.g. <c>NVARCHAR (255)</c>.</param>
/// <param name="Nullable">Whether the column permits null. Every fixture value is non-null regardless.</param>
/// <param name="Ordinal">
/// The column's position in the generated sequence, which is what makes its value distinct. Carried so a
/// failure can say <i>which</i> of 377 values came back instead of the expected one.
/// </param>
/// <param name="Expected">The value the fixture sent.</param>
public sealed record FixtureColumn(
    string Name, string Path, string SqlType, bool Nullable, int Ordinal, JsonElement Expected)
{
    /// <summary>The declared width, for an <c>NVARCHAR</c> column; otherwise null.</summary>
    public int? Width =>
        SqlType.StartsWith("NVARCHAR (", StringComparison.Ordinal)
            ? int.Parse(SqlType[10..^1], CultureInfo.InvariantCulture)
            : null;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} [{SqlType}] from '$.handler.{Path}'";
}

/// <summary>
/// One of the 18 repeating collections: the table its elements land in, how to reach that table from
/// <c>dbo.HandlerSource</c>, and every column the fixture populates in it.
/// </summary>
/// <param name="Path">
/// The collection's path as the specification spells it, e.g. <c>hsm.activities[].wasteCodes</c>. A
/// <c>[]</c> segment means the collection is nested inside another one's element.
/// </param>
/// <param name="Table">The child table, e.g. <c>HandlerSourceHsmActivityWasteCode</c>.</param>
/// <param name="IdentityColumn">Its surrogate key, which a nested collection keys to.</param>
/// <param name="ParentTable">
/// What it keys to: <c>HandlerSource</c> for 15 of the 18, another child table for the 3 that are
/// nested. Emitted by the generator rather than inferred here, because a test that guessed the join
/// could read a grandchild row belonging to a different element and call it a pass.
/// </param>
/// <param name="ParentColumn">The column holding that key.</param>
/// <param name="Bare">
/// Whether the elements are bare strings rather than objects. True for 5 of the 18, and the reason
/// <see cref="FixtureColumn.Path"/> is <c>$</c> on those: there is no property name to path to.
/// </param>
/// <param name="Columns">The payload columns, in table order.</param>
public sealed record FixtureChildTable(
    string Path,
    string Table,
    string IdentityColumn,
    string ParentTable,
    string ParentColumn,
    bool Bare,
    IReadOnlyList<FixtureColumn> Columns)
{
    /// <summary>Whether this collection hangs off another child rather than off the handler.</summary>
    public bool IsNested =>
        !string.Equals(ParentTable, "HandlerSource", StringComparison.Ordinal);

    /// <summary>Names one of this collection's columns and the full path it shreds from.</summary>
    /// <param name="column">The column, whose own <see cref="FixtureColumn.Path"/> is element-relative.</param>
    /// <returns>Something like <c>HandlerSourceOwner.Name [NVARCHAR (80)] from '$.handler.owners[0].name'</c>.</returns>
    /// <remarks>
    /// <see cref="FixtureColumn.ToString"/> cannot do this on its own, and its answer is actively wrong for
    /// a child: it prints the path under <c>$.handler</c>, which is right for the 210 parent columns and
    /// reads as <c>'$.handler.name'</c> for an owner's name — a path that exists, means something else, and
    /// would send a reader looking in the wrong place. The element root is known only here.
    /// </remarks>
    public string Describe(FixtureColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        // '[0]' at every level, which is exact: the fixture sends one element per collection, nested
        // collections included. See build/generate_payload_fixture.py for why one and not two.
        string element = "$.handler." + Path.Replace("[]", "[0]", StringComparison.Ordinal) + "[0]";

        return Bare
            ? $"{Table}.{column.Name} [{column.SqlType}] from '{element}', the element itself"
            : $"{Table}.{column.Name} [{column.SqlType}] from '{element}.{column.Path}'";
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Table} from '$.handler.{Path}'";
}
