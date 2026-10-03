using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Data.SqlClient;

using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The 18 repeating collections of <c>dbo.uspMergeHandlerSourceBatch</c>, and above all the rule that
/// decides when one of their rows is retired.
/// </summary>
/// <remarks>
/// <para>
/// MDE's decision, taken on 2026-09-05: <b>a stored child row is retired only when EPA actually sent
/// the property.</b> An empty array and a shorter one are real removals and do retire. An
/// <b>absent</b> property retires nothing and records a <c>logs.DataQualityObservation</c> instead,
/// because absent is not evidence of none — it is equally consistent with EPA having renamed the
/// property, which is G32's known hazard.
/// </para>
/// <para>
/// That distinction rests on one measured engine behaviour and cannot be inferred from the shredded
/// rowset: <c>CROSS APPLY OPENJSON (e.[value], '$.handler.owners')</c> yields <b>0 rows and no
/// error</b> for an absent property, for <c>[]</c>, and for a property EPA renamed — all three look
/// identical. Only an explicit <c>JSON_PATH_EXISTS</c> tells them apart, so the procedure records one
/// per collection per element in <c>@Collection</c> before any child MERGE runs, and the retire branch
/// is gated on it. <see cref="AnEmptyArrayRetiresEveryElement"/> and
/// <see cref="AnAbsentPropertyRetiresNothing"/> are the same payload shape to
/// <c>OPENJSON</c> and must come out opposite; that pair is the point of this class.
/// </para>
/// <para>
/// The generated fixture <b>does</b> populate all 18 collections, one element apiece — that is
/// <see cref="ChildCollectionRoundTripTests"/>' subject, and it is why the tests below that mean
/// absence call <c>Remove</c> explicitly rather than relying on a collection not being there. Each
/// test here then <i>replaces</i> the collection it is about with elements written for it: two or three
/// short ones, so the expected token strings stay readable. That division is deliberate — the
/// generated fixture proves that all 167 child columns shred, and these tests prove what happens when
/// an array grows, shrinks, empties or vanishes. Neither could stand in for the other.
/// </para>
/// <para>
/// One consequence worth knowing when reading the assertions: every merge in this class writes rows in
/// the other 17 collections too. Nothing here counts rows across tables, and
/// <see cref="AnAbsentPropertyWithStoredRowsIsObserved"/> removes exactly one collection, so its
/// single expected observation is still exact.
/// </para>
/// <para>
/// The corpus is permanent — <b>there is no hard delete anywhere in this database</b> — so nothing here
/// is cleaned up and every test must give the same answer on its thousandth run. Each takes a sequence
/// number the handler has never used, which guarantees a parent with no child rows to start from
/// without deleting anything, and each scopes its reads to that one version.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class ChildCollectionTests
{
    /// <summary>The reserved handler each test writes under. Ordinals 1-6 belong to the other classes.</summary>
    private const int OrderOrdinal = 10;
    private const int ShorterOrdinal = 11;
    private const int EmptyOrdinal = 12;
    private const int AbsentOrdinal = 13;
    private const int ObservedOrdinal = 14;
    private const int RevivedOrdinal = 15;
    private const int UnchangedOrdinal = 16;
    private const int GrandchildOrdinal = 17;
    private const int ReRetireOrdinal = 18;

    /// <summary>Pinned rather than defaulted to the clock, so two sends of one payload are one payload.</summary>
    private static readonly DateTimeOffset Retrieved = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    /// <summary>An array of objects is written one row per element, in the order EPA sent them.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <c>OrdinalPosition</c> comes from <c>arr.[key]</c>, which is zero-based, and the outer shred has
    /// to use <c>OPENJSON</c>'s default schema to have a <c>[key]</c> at all — <c>OPENJSON … WITH</c>
    /// supplies no ordinal. A collection whose order was not preserved would still round-trip a set of
    /// names while silently reassociating each one with a different address.
    /// </remarks>
    [IntegrationFact]
    public async Task SendingAnArrayWritesOneRowPerElementInOrder()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(OrderOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO", "CHARLIE"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "0:ALPHA:0 1:BRAVO:0 2:CHARLIE:0",
            await OwnersAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);
    }

    /// <summary>A shorter array retires the elements beyond its end, and only those.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// On the bare-string shape — <c>$.handler.waste.federalWasteCodes</c> is an array of strings, 5 of
    /// the 18 are — because that shape has no property to path to and shreds through
    /// <c>arr.[value]</c> alone, so it exercises a branch of the emitter the object shape does not.
    /// </remarks>
    [IntegrationFact]
    public async Task AShorterArrayRetiresTheElementsBeyondIt()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(ShorterOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => FederalWasteCodes(h, "D001", "D002", "D003"))],
            loadRunId);

        Assert.Equal(
            "0:D001:0 1:D002:0 2:D003:0",
            await FederalWasteCodesAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => FederalWasteCodes(h, "D001"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "0:D001:0 1:D002:1 2:D003:1",
            await FederalWasteCodesAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);
    }

    /// <summary>An empty array retires every element, because EPA sent the property and it is empty.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The near half of the decision. This payload and the one in
    /// <see cref="AnAbsentPropertyRetiresNothing"/> shred to the same 0 rows, so the only thing that
    /// can produce the opposite outcomes asserted in the two tests is the <c>JSON_PATH_EXISTS</c> gate.
    /// The second assertion is as load-bearing as the first: <c>[]</c> is a removal EPA reported, not a
    /// gap in what it reported, so it must record no observation.
    /// </remarks>
    [IntegrationFact]
    public async Task AnEmptyArrayRetiresEveryElement()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(EmptyOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO"))],
            loadRunId);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = new JsonArray())],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "0:ALPHA:1 1:BRAVO:1",
            await OwnersAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);

        Assert.Equal(string.Empty, await ObservationsAsync(probe, loadRunId), StringComparer.Ordinal);
    }

    /// <summary>An absent property retires nothing. This is the decision itself.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The most consequential assertion in this class. If EPA renames <c>owners</c> in a release, the
    /// shredded rowset is empty and an ungated <c>WHEN NOT MATCHED BY SOURCE</c> would soft-delete
    /// every owner of every Maryland handler in one unattended night, reporting a successful run.
    /// </remarks>
    [IntegrationFact]
    public async Task AnAbsentPropertyRetiresNothing()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(AbsentOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO"))],
            loadRunId);

        // What a renamed property looks like from here: the collection is simply not in the payload.
        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h.Remove("owners"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "0:ALPHA:0 1:BRAVO:0",
            await OwnersAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);
    }

    /// <summary>An absent collection with live rows stored for it is recorded as an observation.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Retiring nothing is the safe half of the decision; noticing is the other half, or a rename is
    /// absorbed in silence and the data quietly stops advancing. The observation fires only on the
    /// genuinely diagnostic combination — EPA sent the property for <b>no</b> element in the batch
    /// <b>and</b> this database holds live rows for those same parents — which is why exactly one row
    /// is expected here and none in <see cref="AnEmptyArrayRetiresEveryElement"/>.
    /// </para>
    /// <para>
    /// Scoped by <c>LoadRunId</c>, which this test opens fresh, so the count is exact rather than a
    /// lower bound on a permanent table.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task AnAbsentPropertyWithStoredRowsIsObserved()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(ObservedOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA"))],
            loadRunId);

        // Nothing is observed while the property is being sent, even though rows are stored for it.
        Assert.Equal(string.Empty, await ObservationsAsync(probe, loadRunId), StringComparer.Ordinal);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h.Remove("owners"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "CollectionAbsentButStored|Warning|HandlerSourceOwner|$.handler.owners",
            await ObservationsAsync(probe, loadRunId),
            StringComparer.Ordinal);
    }

    /// <summary>A retired element is revived when EPA sends it again — as one row, not a second one.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Script 522's header records this as the merge's obligation rather than the soft-delete
    /// procedure's: if EPA reports a withdrawn version again, the parent and every descendant row must
    /// come back. The <c>ON</c> clause is therefore unfiltered on <c>IsDeleted</c>, exactly as the
    /// parent's is.
    /// </para>
    /// <para>
    /// The count assertion is the substance. The child unique index is filtered on
    /// <c>IsDeleted = 0</c>, so a retired row does not reserve its key — a merge that inserted instead
    /// of reviving would leave two rows at ordinal 1, one deleted and one live, and every read that
    /// filters <c>IsDeleted = 0</c> would look correct while the natural key had quietly stopped being
    /// unique.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ARetiredElementIsRevivedWhenEpaSendsItAgain()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(RevivedOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO"))],
            loadRunId);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA"))],
            loadRunId);

        Assert.Equal(
            "0:ALPHA:0 1:BRAVO:1", await OwnersAsync(probe, handlerId, sequence), StringComparer.Ordinal);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "DELTA"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        // One row at ordinal 1, revived and re-valued -- not a live row beside a deleted one.
        Assert.Equal(
            "0:ALPHA:0 1:DELTA:0", await OwnersAsync(probe, handlerId, sequence), StringComparer.Ordinal);
    }

    /// <summary>An unchanged array is not re-stamped, so the audit trail keeps telling the truth.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The convergence requirement, on the child rows rather than the parent. Every incremental run
    /// re-sends collections that did not change; a merge whose <c>WHEN MATCHED</c> lacked its
    /// <c>IS DISTINCT FROM</c> comparison would move <c>auditModifiedDateUtc</c> on all of them, and
    /// "when did this owner last change" would answer "last night", every night, forever.
    /// </remarks>
    [IntegrationFact]
    public async Task AnUnchangedArrayIsNotRestamped()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(UnchangedOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO"))],
            loadRunId);

        string first = await OwnerStampsAsync(probe, handlerId, sequence);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, await OwnerStampsAsync(probe, handlerId, sequence), StringComparer.Ordinal);
    }

    /// <summary>A grandchild belongs to its own parent element, not to the handler.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Three of the 18 hang off another child rather than off <c>dbo.HandlerSource</c>. Their parent
    /// key cannot come from <c>MERGE … OUTPUT</c>, because a matched-but-unchanged row produces no
    /// OUTPUT row: on the second run of an unchanged payload the map would be empty and every
    /// grandchild would be orphaned or retired. It comes from joining the child table that was merged
    /// a statement earlier on <c>(HandlerSourceId, OrdinalPosition)</c>.
    /// </para>
    /// <para>
    /// Two activities with different waste codes, then a shrink on the first only. If the join were
    /// keyed to the handler instead of the element, the second activity's code would be retired
    /// alongside the first's — so the last assertion is what proves the threading is per element.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task AGrandchildIsKeyedToItsOwnParentElement()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(GrandchildOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => Activities(
                h,
                Activity("A", "D001", "D002"),
                Activity("B", "K002")))],
            loadRunId);

        Assert.Equal(
            "0:A:0 1:B:0", await HsmActivitiesAsync(probe, handlerId, sequence), StringComparer.Ordinal);

        Assert.Equal(
            "0.0:D001:0 0.1:D002:0 1.0:K002:0",
            await HsmWasteCodesAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => Activities(
                h,
                Activity("A", "D001"),
                Activity("B", "K002")))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "0.0:D001:0 0.1:D002:1 1.0:K002:0",
            await HsmWasteCodesAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);
    }

    /// <summary>A row retired on an earlier run is not retired again on this one.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// This was a real defect, found by probe and fixed by adding <c>AND tgt.IsDeleted = 0</c> to the
    /// retire branch. An already-retired row is <c>NOT MATCHED BY SOURCE</c> on every later run too,
    /// so without that predicate every nightly run re-stamped <c>auditDeletedDateUtc</c> on rows
    /// retired weeks ago — the audit trail claiming they were deleted last night.
    /// </para>
    /// <para>
    /// The second consequence is why the test is worth keeping rather than merely the fix: the
    /// <c>childRetired</c> figure in the run's <c>Comments</c> is an alarm, and one that never falls
    /// back to zero cannot raise anything.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task AlreadyRetiredRowsAreNotRetiredAgain()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string handlerId = TestData.HandlerId(ReRetireOrdinal);
        int sequence = await Runs.NextSequenceAsync(handlerId);
        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA", "BRAVO", "CHARLIE"))],
            loadRunId);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA"))],
            loadRunId);

        string retired = await OwnerStampsAsync(probe, handlerId, sequence);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [Envelope(handlerId, sequence, h => h["owners"] = Owners("ALPHA"))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        Assert.Equal(
            "0:ALPHA:0 1:BRAVO:1 2:CHARLIE:1",
            await OwnersAsync(probe, handlerId, sequence),
            StringComparer.Ordinal);

        Assert.False(string.IsNullOrEmpty(retired));
        Assert.Equal(retired, await OwnerStampsAsync(probe, handlerId, sequence), StringComparer.Ordinal);
    }

    /// <summary>One envelope for this handler version, with the collections a test wants.</summary>
    /// <param name="handlerId">The reserved identifier.</param>
    /// <param name="sequence">The version.</param>
    /// <param name="mutate">Adds or removes collections on the fixture's handler object.</param>
    /// <returns>The envelope.</returns>
    /// <remarks>
    /// <c>currentRecord</c> is false throughout. These tests take a fresh sequence on every run, and a
    /// version that claimed to be current would leave the handler with one more current record each
    /// time — an untruth about EPA's data, planted by a test that does not care either way.
    /// </remarks>
    private static HandlerEnvelope Envelope(string handlerId, int sequence, Action<JsonObject> mutate) =>
        TestData.Envelope(handlerId, sequence, currentRecord: false, Retrieved, mutate);

    /// <summary>An owners array: objects, the shape 13 of the 18 collections have.</summary>
    /// <param name="names">One owner per name.</param>
    /// <returns>The array.</returns>
    private static JsonArray Owners(params string[] names)
    {
        JsonArray owners = [];

        foreach (string name in names)
        {
            owners.Add(new JsonObject
            {
                ["name"] = name,
                ["type"] = new JsonObject { ["code"] = "P" },
            });
        }

        return owners;
    }

    /// <summary>Sets <c>$.handler.waste.federalWasteCodes</c>: bare strings, the other shape.</summary>
    /// <param name="handler">The handler object.</param>
    /// <param name="codes">The codes, in order.</param>
    private static void FederalWasteCodes(JsonObject handler, params string[] codes)
    {
        JsonArray array = [];

        foreach (string code in codes)
        {
            array.Add(code);
        }

        ((JsonObject)handler["waste"]!)["federalWasteCodes"] = array;
    }

    /// <summary>One <c>$.handler.hsm.activities</c> element, with its own bare-string grandchild array.</summary>
    /// <param name="facilityCode">The activity's facility code.</param>
    /// <param name="wasteCodes">The codes it manages, in order.</param>
    /// <returns>The element.</returns>
    private static JsonObject Activity(string facilityCode, params string[] wasteCodes)
    {
        JsonArray codes = [];

        foreach (string code in wasteCodes)
        {
            codes.Add(code);
        }

        return new JsonObject
        {
            ["facilityCode"] = new JsonObject { ["code"] = facilityCode },
            ["wasteCodes"] = codes,
        };
    }

    /// <summary>Sets <c>$.handler.hsm.activities</c> to the given elements.</summary>
    /// <param name="handler">The handler object.</param>
    /// <param name="activities">The elements, in order.</param>
    private static void Activities(JsonObject handler, params JsonObject[] activities)
    {
        JsonArray array = [];

        foreach (JsonObject activity in activities)
        {
            array.Add(activity);
        }

        ((JsonObject)handler["hsm"]!)["activities"] = array;
    }

    /// <summary>The owners of one handler version as <c>ordinal:name:isDeleted</c> tokens.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="handlerId">The handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>The tokens in ordinal order, or the empty string when there are none.</returns>
    /// <remarks>
    /// Unfiltered on <c>IsDeleted</c>, deliberately: a retired row is exactly what these tests are
    /// asserting about, and a helper that hid it would make every retire assertion unfalsifiable.
    /// </remarks>
    private static async Task<string> OwnersAsync(SqlConnection probe, string handlerId, int sequence) =>
        await AggregateAsync(
            probe,
            $"""
             SELECT STRING_AGG (CONCAT (c.OrdinalPosition, N':', c.Name, N':', c.IsDeleted), N' ')
                      WITHIN GROUP (ORDER BY c.OrdinalPosition, c.HandlerSourceOwnerId)
               FROM dbo.HandlerSourceOwner AS c
               JOIN dbo.HandlerSource AS h ON h.HandlerSourceId = c.HandlerSourceId
              WHERE {Version(handlerId, sequence)};
             """);

    /// <summary>The owners' audit stamps, to the tick, as <c>ordinal:modified:deleted</c> tokens.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="handlerId">The handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>The tokens in ordinal order, or the empty string when there are none.</returns>
    /// <remarks>
    /// Style 126 rather than a default conversion, so the comparison sees all seven digits of the
    /// <c>DATETIME2</c> fraction. Two merges a few milliseconds apart would agree to the second.
    /// </remarks>
    private static async Task<string> OwnerStampsAsync(
        SqlConnection probe, string handlerId, int sequence) =>
        await AggregateAsync(
            probe,
            $"""
             SELECT STRING_AGG (CONCAT (c.OrdinalPosition
                                      , N':', CONVERT (NVARCHAR (30), c.auditModifiedDateUtc, 126)
                                      , N':', CONVERT (NVARCHAR (30), c.auditDeletedDateUtc, 126)), N' ')
                      WITHIN GROUP (ORDER BY c.OrdinalPosition, c.HandlerSourceOwnerId)
               FROM dbo.HandlerSourceOwner AS c
               JOIN dbo.HandlerSource AS h ON h.HandlerSourceId = c.HandlerSourceId
              WHERE {Version(handlerId, sequence)};
             """);

    /// <summary>The federal waste codes of one handler version as <c>ordinal:code:isDeleted</c> tokens.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="handlerId">The handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>The tokens in ordinal order, or the empty string when there are none.</returns>
    private static async Task<string> FederalWasteCodesAsync(
        SqlConnection probe, string handlerId, int sequence) =>
        await AggregateAsync(
            probe,
            $"""
             SELECT STRING_AGG (
                        CONCAT (c.OrdinalPosition, N':', c.FederalWasteCode, N':', c.IsDeleted), N' ')
                      WITHIN GROUP (ORDER BY c.OrdinalPosition, c.HandlerSourceWasteFederalWasteCodeId)
               FROM dbo.HandlerSourceWasteFederalWasteCode AS c
               JOIN dbo.HandlerSource AS h ON h.HandlerSourceId = c.HandlerSourceId
              WHERE {Version(handlerId, sequence)};
             """);

    /// <summary>The HSM activities of one handler version as <c>ordinal:facilityCode:isDeleted</c> tokens.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="handlerId">The handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>The tokens in ordinal order, or the empty string when there are none.</returns>
    private static async Task<string> HsmActivitiesAsync(
        SqlConnection probe, string handlerId, int sequence) =>
        await AggregateAsync(
            probe,
            $"""
             SELECT STRING_AGG (
                        CONCAT (c.OrdinalPosition, N':', c.FacilityCodeCode, N':', c.IsDeleted), N' ')
                      WITHIN GROUP (ORDER BY c.OrdinalPosition, c.HandlerSourceHsmActivityId)
               FROM dbo.HandlerSourceHsmActivity AS c
               JOIN dbo.HandlerSource AS h ON h.HandlerSourceId = c.HandlerSourceId
              WHERE {Version(handlerId, sequence)};
             """);

    /// <summary>
    /// The HSM waste codes as <c>activityOrdinal.ordinal:code:isDeleted</c> tokens — the grandchild's
    /// parentage spelt out, which is the whole assertion.
    /// </summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="handlerId">The handler.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>The tokens in activity-then-ordinal order, or the empty string when there are none.</returns>
    private static async Task<string> HsmWasteCodesAsync(
        SqlConnection probe, string handlerId, int sequence) =>
        await AggregateAsync(
            probe,
            $"""
             SELECT STRING_AGG (CONCAT (a.OrdinalPosition, N'.', w.OrdinalPosition
                                      , N':', w.WasteCode, N':', w.IsDeleted), N' ')
                      WITHIN GROUP (ORDER BY a.OrdinalPosition, w.OrdinalPosition)
               FROM dbo.HandlerSourceHsmActivityWasteCode AS w
               JOIN dbo.HandlerSourceHsmActivity AS a
                 ON a.HandlerSourceHsmActivityId = w.HandlerSourceHsmActivityId
               JOIN dbo.HandlerSource AS h ON h.HandlerSourceId = a.HandlerSourceId
              WHERE {Version(handlerId, sequence)};
             """);

    /// <summary>This run's data quality observations as <c>type|severity|table|path</c> tokens.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="loadRunId">The run, which the caller opened for this test alone.</param>
    /// <returns>The tokens, or the empty string when the run recorded none.</returns>
    /// <remarks>
    /// <c>ObservedValue</c> and <c>Detail</c> are left out. They carry the sentence an operator reads,
    /// and pinning prose in a test buys nothing and breaks on every improvement to the wording.
    /// </remarks>
    private static async Task<string> ObservationsAsync(SqlConnection probe, int loadRunId) =>
        await AggregateAsync(
            probe,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 SELECT STRING_AGG (CONCAT (o.ObservationType, N'|', o.Severity, N'|', o.TableName
                                          , N'|', o.JsonPath), N' ')
                          WITHIN GROUP (ORDER BY o.TableName, o.JsonPath)
                   FROM logs.DataQualityObservation AS o
                  WHERE o.LoadRunId = {loadRunId}
                    AND o.IsDeleted = 0;
                 """));

    /// <summary>Reads one <c>STRING_AGG</c>, turning "no rows" into the empty string.</summary>
    /// <param name="probe">An open connection.</param>
    /// <param name="sql">The query.</param>
    /// <returns>The aggregate, or the empty string.</returns>
    /// <remarks>
    /// <c>STRING_AGG</c> over no rows returns null, and null is the answer a mistyped table name would
    /// give too. The callers all assert against an expected token string, so collapsing it here keeps
    /// "no child rows" comparable without letting it stand in for "nothing was found".
    /// </remarks>
    private static async Task<string> AggregateAsync(SqlConnection probe, string sql) =>
        await IntegrationServer.TextAsync(probe, sql) ?? string.Empty;

    /// <summary>The predicate naming one handler version, with the identifier asserted first.</summary>
    /// <param name="handlerId">The handler. Must be one of the reserved identifiers.</param>
    /// <param name="sequence">The version.</param>
    /// <returns>A <c>WHERE</c> fragment, for a query aliasing <c>dbo.HandlerSource</c> as <c>h</c>.</returns>
    /// <remarks>
    /// Interpolated rather than parameterised for the reason <see cref="Runs.NextSequenceAsync"/> gives,
    /// and with the same assertions ahead of the string: the value is one of this suite's own reserved
    /// identifiers, <c>ZZTEST</c> followed by six digits and nothing else. An identifier that failed
    /// those assertions would be a defect in the test rather than an injection — but the assertions are
    /// what make the interpolation defensible, so they stay in front of it.
    /// </remarks>
    private static string Version(string handlerId, int sequence)
    {
        Assert.StartsWith(TestData.HandlerIdPrefix, handlerId, StringComparison.Ordinal);
        Assert.Equal(12, handlerId.Length);
        Assert.All(handlerId[TestData.HandlerIdPrefix.Length..], c => Assert.True(char.IsAsciiDigit(c)));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             h.HandlerId  = N'{handlerId}'
                AND h.SourceType = N'{TestData.SourceType}'
                AND h.Sequence   = {sequence}
             """);
    }
}
