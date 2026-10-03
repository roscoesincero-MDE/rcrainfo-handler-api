using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.SqlClient;

using RCRAInfo.Data.Results;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// G36: <c>OPENJSON … WITH</c> truncates a string wider than the declared type <b>silently</b>, and this
/// class is the detector for it — every projected <c>NVARCHAR</c> column compared against the same value
/// re-extracted from the payload the merge stored alongside it.
/// </summary>
/// <remarks>
/// <para>
/// The measured asymmetry that makes this necessary: given <c>"ABCDE"</c> for an <c>NVARCHAR (1)</c>,
/// <c>OPENJSON … WITH</c> yields <c>"A"</c> with no error and no warning; given <c>"abc"</c> for an
/// <c>INT</c> it raises 245, and <c>"not-a-date"</c> for a <c>DATETIME2</c> raises 241. <b>Numbers and
/// dates fail loudly; strings fail silently.</b> So only the 132 <c>NVARCHAR</c> payload columns need a
/// detector, and including the others would introduce false positives — a <c>DATE</c> column legitimately
/// stores <c>2026-01-02</c> for a payload EPA might one day send as <c>2026-01-02T00:00:00Z</c>, which
/// differs as text and not at all as a value.
/// </para>
/// <para>
/// <b>The comparison is built on <c>OPENJSON … WITH (… NVARCHAR (MAX))</c> and deliberately not on
/// <c>JSON_VALUE</c>, which is what the analysis originally recommended.</b> Measured on the target
/// engine, against a 4001-character value:
/// </para>
/// <list type="table">
///   <item>
///     <term><c>JSON_VALUE (doc, '$.notes')</c></term>
///     <description>returns <b><c>NULL</c></b>. The default lax path answers "no value" for anything
///     over 4000 characters — it does not truncate and it does not raise.</description>
///   </item>
///   <item>
///     <term><c>JSON_VALUE (doc, 'strict $.notes')</c></term>
///     <description>raises 13625, "String value in the specified JSON path would be truncated." A real
///     signal, but it aborts the statement, so it cannot report <i>which</i> columns diverged across a
///     corpus.</description>
///   </item>
///   <item>
///     <term><c>OPENJSON (doc) WITH (V NVARCHAR (MAX) '$.notes')</c></term>
///     <description>returns all 4001 characters. This is the mechanism used below.</description>
///   </item>
///   <item>
///     <term><c>OPENJSON (doc) WITH (V NVARCHAR (4000) '$.notes')</c></term>
///     <description>returns exactly 4000 — the silent truncation itself, on the same construct the
///     merge uses.</description>
///   </item>
/// </list>
/// <para>
/// <b>A <c>JSON_VALUE</c> detector would still flag the divergence</b>, and it was measured doing so: a
/// stored 4000-character value against a <c>NULL</c> is a difference like any other. What it cannot do
/// is say what the difference <i>is</i>. It reports "the payload holds no value at this path" for a
/// payload holding 4001 characters — which is the signature of an entirely different defect, the one
/// G32 exists for, a property EPA renamed or re-cased. An operator handed that finding would go and
/// compare the path against the current API specification and find it correct, twice, and conclude the
/// detector was wrong.
/// </para>
/// <para>
/// That is not academic: <c>dbo.HandlerSource</c> has <b>five</b> <c>NVARCHAR (4000)</c> columns —
/// <c>Comments</c>, <c>PublicComments</c>, <c>PermitNatureOfBusiness</c>,
/// <c>WasteShortTermGeneratorNotes</c> and <c>EpisodicRescindComment</c> — so the misdirection would
/// land in exactly the five places the widest values live and the longest truncations happen. So the
/// negative control asserts the <b>length the payload holds</b>, not merely that it disagrees: with
/// <c>JSON_VALUE</c> the five come back <c>null</c> instead of 4001 and the test fails. Without that
/// assertion the <c>JSON_VALUE</c> form passes every test in this class, which was measured too.
/// </para>
/// <para>
/// <c>dbo.HandlerSourceRawJson</c> is what makes the comparison possible at all, and it is an
/// independent witness rather than a second copy of the projection: it stores the <c>$.handler</c>
/// subtree verbatim as <c>NVARCHAR (MAX)</c>, hashed by the engine from the text being stored, and it is
/// merged for <i>every</i> element rather than only the ones the parent merge touched. A truncation is
/// therefore recoverable in production by re-projection, without re-fetching from EPA — which is why the
/// remedy here is a test rather than 132 <c>LEN</c> checks on the hot path.
/// </para>
/// <para>
/// The pair below is the whole design. <see cref="EveryStringColumnAgreesWithTheStoredPayload"/> is the
/// <b>positive control</b>: every fixture string is already exactly its column's declared width, so a
/// detector that reported divergence here would be one that always fires.
/// <see cref="EveryStringColumnDivergesWhenItsValueWasOneCharacterTooWide"/> is the <b>negative
/// control</b>: it sends the same payload with one character appended to each string and requires
/// divergence on every one. Neither test means anything alone — a detector never shown to fail is
/// indistinguishable from one comparing nothing, and both tests also report how many columns they
/// examined so that "nothing diverged" cannot be produced by looking at nothing.
/// </para>
/// <para>
/// One character is <b>appended</b>, never substituted, and that is load-bearing twice over. The stored
/// value after truncation is then byte-for-byte the fixture value, so every <c>CHECK</c> constraint on
/// the table still sees a value it already accepts and the merge cannot fail for an unrelated reason;
/// and the parent <c>MERGE</c>'s own change detection compares post-truncation values, so it sees
/// nothing at all — which is <see cref="TruncationIsInvisibleToTheMergesOwnReporting"/>.
/// </para>
/// <para>
/// The over-wide payload is derived here from the generated fixture rather than generated as a second
/// file. <c>build/generate_payload_fixture.py --check</c> is a guardrail over one artefact; a second
/// generated artefact would be a second thing that can fall behind the specification, and it would fall
/// behind in the direction that matters — a column added to the table and to the fixture but not to the
/// over-wide variant is a column this detector silently stops covering.
/// </para>
/// <para>
/// The corpus is permanent — <b>there is no hard delete anywhere in this database</b> — so nothing here
/// is cleaned up and every test is written to give the same answer on its thousandth run. Each takes a
/// sequence that has never been used, via <see cref="Runs.NextSequenceAsync"/>, which reads
/// <c>MAX (Sequence) + 1</c> including soft-deleted rows.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class TruncationDetectionTests
{
    /// <summary>The reserved handler each test writes under. One apiece, so no test reads another's row.</summary>
    private const int PlainOrdinal = 19;
    private const int WideOrdinal = 20;
    private const int SilentOrdinal = 21;
    private const int KeyOrdinal = 22;

    /// <summary>
    /// The character appended to make a value one too wide. Appended rather than substituted — see the
    /// class remarks — and chosen so it cannot be mistaken for part of the generator's own sentinel.
    /// </summary>
    private const string OneTooWide = "!";

    /// <summary>
    /// The audit columns, which are <c>NVARCHAR (128)</c> on the table and absent from the payload.
    /// Named so that <see cref="TheDetectorCoversEveryStringColumnOfTheTable"/> can say what it excludes
    /// rather than merely arriving at the right total.
    /// </summary>
    private static readonly string[] AuditStringColumns =
        ["auditCreatedBy", "auditDeletedBy", "auditModifiedBy"];

    /// <summary>
    /// The three natural-key columns, excluded from the over-wide payload because script 400 rejects an
    /// over-wide key outright — see <see cref="ANaturalKeyOneCharacterTooWideIsRefusedOutright"/>.
    /// </summary>
    private static readonly string[] KeyColumns = ["HandlerId", "ActivityLocation", "SourceType"];

    /// <summary>Pinned rather than defaulted to now, so two merges of one version differ only where intended.</summary>
    private static readonly DateTimeOffset Retrieved = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>The divergence predicate, as one expression so both aggregates below use the same one.</summary>
    /// <remarks>
    /// <c>DATALENGTH</c> is compared as well as the value, and not for symmetry. <c>=</c> on
    /// <c>NVARCHAR</c> ignores trailing spaces, so a truncation that removed nothing but trailing
    /// whitespace would compare equal — and EPA free text is exactly where a trailing space is
    /// plausible. <c>IS DISTINCT FROM</c> is SQL Server 2022 and therefore in scope for the target
    /// platform.
    /// </remarks>
    private const string Divergent =
        "(d.Stored IS DISTINCT FROM d.Sent "
        + "OR DATALENGTH (d.Stored) IS DISTINCT FROM DATALENGTH (d.Sent))";

    /// <summary>Every <c>NVARCHAR</c> payload column: the 132 the detector compares.</summary>
    private static IReadOnlyList<FixtureColumn> StringColumns { get; } =
        [.. TestData.Columns.Where(c => c.Width is not null)];

    /// <summary>The 129 that can be made over-wide without the batch being refused for its key.</summary>
    private static IReadOnlyList<FixtureColumn> Widenable { get; } =
        [.. StringColumns.Where(c => !KeyColumns.Contains(c.Name, StringComparer.Ordinal))];

    /// <summary>
    /// The positive control: with every string at exactly its declared width, no column diverges from
    /// the payload the merge stored.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// This is the test that makes the negative control mean something. A comparison that fired here
    /// would be reporting the shredding's normal behaviour as a defect, and the two tests together would
    /// still both pass. It also proves all 132 paths resolve: a mis-typed path in the detector's own
    /// <c>OPENJSON</c> list yields <c>NULL</c> for <c>Sent</c> against a populated <c>Stored</c>, which
    /// is a divergence and fails here.
    /// </remarks>
    [IntegrationFact]
    public async Task EveryStringColumnAgreesWithTheStoredPayload()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(PlainOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, sequence, currentRecord: false, Retrieved)],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Divergence found = await DetectAsync(probe, handlerId, sequence, StringColumns);

        Assert.Equal(StringColumns.Count, found.Examined);
        Assert.True(
            found.Divergent == 0,
            "The fixture sends every string at exactly its declared width, so nothing should differ " +
            $"from the stored payload. Diverged: {found.Describe()}. Either the projection is losing " +
            "characters the fixture sent, or this detector's own OPENJSON paths do not match the ones " +
            "script 400 uses -- a path that matches nothing yields NULL, which reads as a divergence.");
    }

    /// <summary>
    /// The negative control: one character appended to each string, and every one of the 129 columns is
    /// reported as diverging from the payload.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The merge succeeds. No error, no warning, no reduced row count — the batch reports exactly what a
    /// clean one reports, which is G36 in one sentence. The assertion is on the set of column names, not
    /// only on the count, so a detector that fired for 129 columns while missing the five
    /// <c>NVARCHAR (4000)</c> ones and double-reporting five others could not pass.
    /// </remarks>
    [IntegrationFact]
    public async Task EveryStringColumnDivergesWhenItsValueWasOneCharacterTooWide()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(WideOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        MergeBatchResult result = await scope.Context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, sequence, currentRecord: false, Retrieved, Widen)],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        // Stated before the divergence assertion, because it is the premise: the over-wide batch was
        // ACCEPTED. If it had been refused there would be nothing to compare and the test would be
        // measuring a gate instead of a truncation.
        Assert.Equal(1, result.Batch.ElementCount);
        Assert.Single(result.Outcomes);
        Assert.Equal("Inserted", result.Outcomes[0].Outcome, StringComparer.Ordinal);

        Divergence found = await DetectAsync(probe, handlerId, sequence, Widenable);

        string[] missed =
            [.. Widenable.Select(c => c.Name).Except(found.Columns, StringComparer.Ordinal).Order()];
        string[] unexpected =
            [.. found.Columns.Except(Widenable.Select(c => c.Name), StringComparer.Ordinal).Order()];

        Assert.Equal(Widenable.Count, found.Examined);
        Assert.True(
            missed.Length == 0,
            "Every one of these columns was sent one character too wide, so every one must be caught. " +
            $"Missed {missed.Length}: {string.Join(", ", missed)}");

        Assert.True(
            unexpected.Length == 0,
            $"The detector named {unexpected.Length} column(s) it was not given: " +
            string.Join(", ", unexpected));

        Assert.Equal(Widenable.Count, found.Divergent);

        // The assertion that defends OPENJSON ... WITH (... NVARCHAR (MAX)) over JSON_VALUE, and the
        // reason it is on the LENGTH rather than on the fact of disagreement: both forms disagree here.
        // Only this one can say the payload holds 4001 characters. JSON_VALUE says it holds none, which
        // is a different defect's signature and would send an operator to check the path.
        Assert.All(
            Widenable,
            column =>
            {
                int? holds = found.Diverged[column.Name];

                Assert.True(
                    holds == column.Width + OneTooWide.Length,
                    $"{column} was sent {column.Width + OneTooWide.Length} character(s) and the stored " +
                    $"payload reports {holds?.ToString(CultureInfo.InvariantCulture) ?? "no value at all"}. " +
                    "A detector that cannot read back what EPA sent cannot tell a truncation from a " +
                    "renamed property, which is the one distinction the finding has to make.");
            });
    }

    /// <summary>
    /// The merge's own reporting cannot see a truncation: the batch digest changes, and the merge reports
    /// the version as unchanged.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The sharpest statement of the gap, and the reason the detector had to be built outside the
    /// procedure. The parent <c>MERGE</c> compares <c>tgt.Col IS DISTINCT FROM src.Col</c> where
    /// <c>src.Col</c> has <i>already been truncated</i> by the <c>OPENJSON … WITH</c> list, so a payload
    /// that lost a character from all 129 strings compares identical to the one before it: no
    /// <c>OUTPUT</c> row, no outcome, and <c>logs.HandlerLoadStatus.Outcome</c> reads
    /// <c>Unchanged</c> — on the monitoring web page an operator will one day read.
    /// </para>
    /// <para>
    /// The digest is asserted to have <b>changed</b> in the same test, and that contrast is the point:
    /// the difference is plainly visible in the payload the loader sent, so nothing about it is
    /// undetectable in principle. It is invisible only to the mechanism that would have to report it.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task TruncationIsInvisibleToTheMergesOwnReporting()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(SilentOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        MergeBatchResult clean = await scope.Context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, sequence, currentRecord: false, Retrieved)],
            loadRunId);

        Assert.Single(clean.Outcomes);
        Assert.Equal(0, (await DetectAsync(probe, handlerId, sequence, StringColumns)).Divergent);

        MergeBatchResult wide = await scope.Context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, sequence, currentRecord: false, Retrieved, Widen)],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.NotEqual(clean.Batch.Sha256, wide.Batch.Sha256, StringComparer.Ordinal);
        Assert.Equal(1, wide.Batch.ElementCount);

        Assert.Empty(wide.Outcomes);

        Divergence found = await DetectAsync(probe, handlerId, sequence, Widenable);

        Assert.Equal(Widenable.Count, found.Divergent);
    }

    /// <summary>
    /// A natural-key value one character too wide is refused outright, rather than truncated into
    /// another handler's key.
    /// </summary>
    /// <param name="path">The path within the handler object to widen.</param>
    /// <param name="width">The declared width of the column that stores it.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The three columns the detector above cannot cover, covered here instead — and the reason it
    /// cannot cover them is the reason they need their own guard. A truncated <c>Comments</c> is a lost
    /// paragraph; a truncated <c>HandlerId</c> is <i>a different handler</i>, and the merge would file
    /// EPA's data for <c>ZZTEST0000209</c> against the row for <c>ZZTEST000020</c>, correctly reporting
    /// it as an update. So this is checked with an explicit <c>LEN</c> before the shredding rather than
    /// left to the detector, and the batch is rejected whole.
    /// </remarks>
    [IntegrationTheory]
    [InlineData("handlerId", 12)]
    [InlineData("activityLocation", 2)]
    [InlineData("type.code", 1)]
    public async Task ANaturalKeyOneCharacterTooWideIsRefusedOutright(string path, int width)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = TestData.HandlerId(KeyOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            SqlException error = await Assert.ThrowsAsync<SqlException>(
                () => scope.Context.MergeHandlerSourceBatchAsync(
                    [TestData.Envelope(
                        handlerId,
                        sequence,
                        currentRecord: false,
                        Retrieved,
                        h => Set(h, path, new string('W', width + 1)))],
                    loadRunId));

            Assert.True(
                SqlErrorNumbers.IsProcedureRefusal(error),
                $"An over-wide '{path}' failed with error {error.Number} rather than script 400's own " +
                $"refusal ({SqlErrorNumbers.ProcedureRefusal}), which means the width check did not " +
                $"reach it: '{error.Message}'");

            Assert.Contains("natural-key", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }

        // Nothing was written under the truncated key either. The refusal is only worth having if it
        // happened before the shredding, and a row here would mean it did not.
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int? rows = await IntegrationServer.ScalarAsync<int>(
            probe,
            string.Create(CultureInfo.InvariantCulture, $"""
                 SELECT COUNT (*)
                   FROM dbo.HandlerSource AS hs
                  WHERE {Version(handlerId, sequence)};
                 """));

        Assert.Equal(0, rows);
    }

    /// <summary>
    /// The detector's column set is every <c>NVARCHAR</c> column of <c>dbo.HandlerSource</c> except the
    /// audit ones, checked against the catalog rather than against the fixture that produced it.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Both tests above derive their columns from <see cref="TestData.Columns"/>, so neither can notice a
    /// column that exists on the table and not in the fixture — and that column would be one this class
    /// has silently stopped covering while still reporting a comfortable 132. The catalog is the
    /// independent side of that comparison. <c>auditDeletedBy</c>, <c>auditCreatedBy</c> and
    /// <c>auditModifiedBy</c> are named as the exclusions rather than subtracted as a count, so a fourth
    /// audit column would fail this test instead of being absorbed by it.
    /// </remarks>
    [IntegrationFact]
    public async Task TheDetectorCoversEveryStringColumnOfTheTable()
    {
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string? catalog = await IntegrationServer.TextAsync(
            probe,
            """
            SELECT STRING_AGG (CAST (c.name AS NVARCHAR (MAX)), N',') WITHIN GROUP (ORDER BY c.name)
              FROM sys.columns AS c
             WHERE c.object_id      = OBJECT_ID (N'dbo.HandlerSource')
               AND c.system_type_id = TYPE_ID (N'nvarchar');
            """);

        Assert.False(string.IsNullOrEmpty(catalog));

        string[] onTheTable = [.. catalog!.Split(',', StringSplitOptions.RemoveEmptyEntries).Order()];
        string[] expected = [.. StringColumns.Select(c => c.Name).Concat(AuditStringColumns).Order()];

        Assert.Equal(expected, onTheTable, StringComparer.Ordinal);

        // The split of the payload set into "widenable" and "key" has to account for all of it, or the
        // negative control could be skipping columns the positive control still reports as examined.
        Assert.Equal(
            StringColumns.Count,
            Widenable.Count + KeyColumns.Length);

        Assert.All(
            KeyColumns,
            name => Assert.Contains(name, StringColumns.Select(c => c.Name), StringComparer.Ordinal));
    }

    /// <summary>Appends one character to every widenable string in the handler object.</summary>
    private static void Widen(JsonObject handler)
    {
        foreach (FixtureColumn column in Widenable)
        {
            // The fixture's own contract, asserted where it is relied upon rather than assumed: a value
            // shorter than its column would not truncate when widened by one, and this test would then
            // require a divergence that cannot occur.
            Assert.Equal(JsonValueKind.String, column.Expected.ValueKind);

            string sent = Read(handler, column.Path);

            Assert.Equal(column.Width, sent.Length);

            Set(handler, column.Path, sent + OneTooWide);
        }
    }

    /// <summary>Reads a dotted path out of the handler object.</summary>
    private static string Read(JsonObject handler, string path)
    {
        JsonNode? node = Walk(handler, path, out string leaf)[leaf];

        Assert.NotNull(node);
        return node!.GetValue<string>();
    }

    /// <summary>Writes a dotted path into the handler object, creating nothing.</summary>
    /// <remarks>
    /// Every parent object exists already — the fixture is fully populated — and the walk asserts that
    /// rather than creating what is missing. A path that had drifted would otherwise be written to a
    /// freshly-created subtree, where it would round-trip perfectly and correspond to nothing.
    /// </remarks>
    private static void Set(JsonObject handler, string path, string value) =>
        Walk(handler, path, out string leaf)[leaf] = value;

    private static JsonObject Walk(JsonObject handler, string path, out string leaf)
    {
        string[] segments = path.Split('.');
        JsonObject parent = handler;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            JsonNode? next = parent[segments[i]];

            Assert.True(
                next is JsonObject,
                $"'{path}' expects an object at '{segments[i]}', and the fixture has " +
                $"{(next is null ? "no such property" : next.GetType().Name)}. The path and the fixture " +
                "have drifted apart.");

            parent = (JsonObject)next!;
        }

        leaf = segments[^1];
        return parent;
    }

    /// <summary>
    /// Compares each given column against the same value re-extracted from the stored payload.
    /// </summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="handlerId">The version's identifier.</param>
    /// <param name="sequence">The version's sequence.</param>
    /// <param name="columns">The columns to compare.</param>
    /// <returns>How many were examined, how many diverged, and which.</returns>
    /// <remarks>
    /// One query rather than one per column. The alternative — a theory case per column — would name the
    /// failing column in the test name, which is worth having; but it would also run 129 merges' worth of
    /// round trips to compare values a single <c>OPENJSON</c> already produces side by side, and the
    /// column name is in the failure message either way. The examined count is returned alongside the
    /// divergent one because they are what distinguish "nothing was wrong" from "nothing was looked at",
    /// and those two produce the same empty list.
    /// </remarks>
    private static async Task<Divergence> DetectAsync(
        SqlConnection probe, string handlerId, int sequence, IReadOnlyList<FixtureColumn> columns)
    {
        Assert.NotEmpty(columns);

        string? line = await IntegrationServer.TextAsync(probe, DetectorSql(handlerId, sequence, columns));

        Assert.False(
            string.IsNullOrEmpty(line),
            $"The detector returned nothing for {handlerId}/{sequence}, which means the version or its " +
            "stored payload is missing rather than that no column diverged.");

        string[] parts = line!.Split('|');

        Assert.Equal(2, parts.Length);

        string[] counts = parts[0].Split('/');

        Assert.Equal(2, counts.Length);

        Dictionary<string, int?> diverged = new(StringComparer.Ordinal);

        foreach (string finding in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] halves = finding.Split(':');

            Assert.Equal(2, halves.Length);

            diverged.Add(
                halves[0],
                halves[1] == "null" ? null : int.Parse(halves[1], CultureInfo.InvariantCulture));
        }

        return new Divergence(
            int.Parse(counts[0], CultureInfo.InvariantCulture),
            int.Parse(counts[1], CultureInfo.InvariantCulture),
            diverged);
    }

    /// <summary>Builds the detector query for a given set of columns.</summary>
    /// <remarks>
    /// <para>
    /// Interpolated, and safe for the same reason <see cref="Runs.NextSequenceAsync"/> is: every value
    /// spliced in comes from the generated fixture or from this suite's own reserved identifiers, and
    /// <see cref="Version"/> asserts the identifier's shape before it is used. There is no
    /// caller-supplied text on this path.
    /// </para>
    /// <para>
    /// <c>NVARCHAR (MAX)</c> in the <c>WITH</c> list for every column regardless of the column's own
    /// width, which is the whole mechanism: extracting at the declared width would truncate the witness
    /// the same way the projection truncated the value, and the comparison would agree perfectly on a
    /// value both sides had lost.
    /// </para>
    /// </remarks>
    private static string DetectorSql(
        string handlerId, int sequence, IReadOnlyList<FixtureColumn> columns)
    {
        string sent = string.Join(
            $"{Environment.NewLine}                        , ",
            columns.Select(c => $"[{c.Name}] NVARCHAR (MAX) '$.{c.Path}'"));

        string compared = string.Join(
            $"{Environment.NewLine}                             , ",
            columns.Select(c => $"(N'{c.Name}', hs.[{c.Name}], sent.[{c.Name}])"));

        // CAST to NVARCHAR (MAX) before aggregating: STRING_AGG over an NVARCHAR (n) input returns
        // NVARCHAR (4000), and 129 column names come to roughly 3,600 characters. A detector for silent
        // truncation that silently truncated its own findings is not a joke worth risking.
        //
        // DATALENGTH / 2 rather than LEN for the reported length, for the reason the divergence
        // predicate gives: LEN does not count trailing spaces, so a value that ended in one would be
        // reported a character short and the caller's length assertion would fail for the wrong reason.
        // 'null' is spelt out rather than left as an empty field, because "the payload holds no value
        // here" is a finding in its own right and must not read as a missing measurement.
        return string.Create(CultureInfo.InvariantCulture, $"""
             SELECT CONCAT (COUNT (*), N'/'
                          , COALESCE (SUM (CASE WHEN {Divergent} THEN 1 ELSE 0 END), 0), N'|'
                          , COALESCE (STRING_AGG (CASE WHEN {Divergent}
                                                       THEN CAST (CONCAT (d.ColumnName, N':'
                                                                        , COALESCE (CAST (DATALENGTH (d.Sent) / 2
                                                                                          AS NVARCHAR (20))
                                                                                  , N'null'))
                                                                  AS NVARCHAR (MAX))
                                                  END, N',')
                                        WITHIN GROUP (ORDER BY d.ColumnName), N''))
               FROM dbo.HandlerSource AS hs
               JOIN dbo.HandlerSourceRawJson AS r
                 ON r.HandlerSourceId = hs.HandlerSourceId
                AND r.IsDeleted       = 0
              CROSS APPLY OPENJSON (r.RawJson)
                   WITH ( {sent}
                        ) AS sent
              CROSS APPLY (VALUES {compared}
                          ) AS d (ColumnName, Stored, Sent)
              WHERE {Version(handlerId, sequence)}
                AND hs.IsDeleted = 0;
             """);
    }

    /// <summary>The predicate identifying one version, with the identifier's shape asserted first.</summary>
    private static string Version(string handlerId, int sequence)
    {
        Assert.StartsWith(TestData.HandlerIdPrefix, handlerId, StringComparison.Ordinal);
        Assert.Equal(12, handlerId.Length);
        Assert.All(handlerId[TestData.HandlerIdPrefix.Length..], c => Assert.True(char.IsAsciiDigit(c)));

        return string.Create(CultureInfo.InvariantCulture, $"""
             hs.HandlerId  = N'{handlerId}'
                AND hs.SourceType = N'{TestData.SourceType}'
                AND hs.Sequence   = {sequence}
             """);
    }

    /// <summary>What the detector found.</summary>
    /// <param name="Examined">How many columns were compared. Zero means the version was not found.</param>
    /// <param name="Divergent">How many differed from the stored payload.</param>
    /// <param name="Diverged">
    /// Each diverging column, against the number of characters the stored payload holds at its path —
    /// <see langword="null"/> where the payload reported no value there at all.
    /// </param>
    private sealed record Divergence(
        int Examined, int Divergent, IReadOnlyDictionary<string, int?> Diverged)
    {
        /// <summary>The diverging column names.</summary>
        internal IEnumerable<string> Columns => Diverged.Keys;

        /// <summary>A short description for a failure message.</summary>
        internal string Describe() =>
            Divergent == 0
                ? $"none, of {Examined} examined"
                : $"{Divergent} of {Examined} examined -- " +
                  string.Join(
                      ", ",
                      Diverged.OrderBy(p => p.Key, StringComparer.Ordinal)
                              .Select(p => $"{p.Key} holds {p.Value?.ToString(CultureInfo.InvariantCulture) ?? "no value"}"));
    }
}
