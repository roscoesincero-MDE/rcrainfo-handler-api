using System.Globalization;

using Microsoft.Data.SqlClient;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// Opens and closes the load runs the write procedures require, and keeps the permanent corpus
/// re-runnable.
/// </summary>
/// <remarks>
/// <para>
/// Every write in this database belongs to a <c>logs.LoadRun</c>, so a test that writes has to open one
/// first. <see cref="StartAsync"/> opens it under the reserved <c>ZZ</c> activity location, which keeps
/// the suite's runs out of the monitoring grid's real ones and — because
/// <c>logs.uspStartLoadRun</c>'s concurrency refusal is scoped by activity location — means a test run
/// can never refuse a genuine <c>MD</c> load or be refused by one.
/// </para>
/// <para>
/// <b>The re-runnability problem is the interesting part of this class.</b> The merge procedure returns
/// one row per record it inserted or updated and <i>nothing</i> for a record that was already current —
/// that subtraction is how the loader counts unchanged records. It also means a test that merges the
/// same fixture twice sees one outcome row the first time the suite ever runs and none on every run
/// after, because nothing changed. Since <b>there is no hard delete anywhere in this database</b>, the
/// obvious repair — clear the corpus between runs — does not exist.
/// </para>
/// <para>
/// So <see cref="RetireAsync"/> soft-deletes the keys a test is about to write. The merge's
/// <c>WHEN MATCHED</c> condition tests <c>tgt.IsDeleted = 1</c> first, deliberately, so that a record
/// EPA has published again is revived rather than left deleted — which means a retired row is
/// guaranteed to be updated, and the outcome the test asserts is the same on the first run and the
/// thousandth. It is not a workaround for the procedure: it exercises the revival path that comment
/// exists for.
/// </para>
/// <para>
/// <see cref="NextSequenceAsync"/> covers the one case retirement cannot: a test that needs a genuine
/// <c>WHEN NOT MATCHED</c> insert, to prove the not-matched branch stamps creation. A retired row still
/// matches, so the only way to get an insert is a key that has never existed. It reads the maximum
/// sequence <b>including soft-deleted rows</b> and adds one — a version count taken from a read
/// procedure would skip the deleted ones and hand back a key that matches after all.
/// </para>
/// </remarks>
internal static class Runs
{
    /// <summary>The mode every run this suite opens is recorded under.</summary>
    /// <remarks>
    /// <c>Full</c> rather than <c>Reconcile</c> because these runs do merge work, and a run mode that
    /// misdescribed what the run did would be a lie in the same table the suite is testing.
    /// </remarks>
    public const string RunMode = "Full";

    /// <summary>Opens a load run for the reserved activity location.</summary>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new run's identifier.</returns>
    /// <remarks>
    /// <c>AllowConcurrent</c> is set because a run this suite opened and did not close — a test that
    /// failed part-way, most likely — would otherwise refuse every subsequent test until the
    /// abandonment sweep caught up, turning one failure into a suite of them. The refusal itself is
    /// tested directly, in <see cref="LoadRunTests"/>, rather than left to be observed as a side
    /// effect here.
    /// </remarks>
    public static Task<int> StartAsync(
        RCRAInfoContext context,
        CancellationToken cancellationToken = default) =>
        context.StartLoadRunAsync(
            new LoadRunRequest
            {
                RunMode = RunMode,
                ActivityLocation = TestData.ActivityLocation,
                ApplicationVersion = "DA5 tests",
                AllowConcurrent = true,
            },
            cancellationToken);

    /// <summary>Closes a load run as succeeded.</summary>
    /// <param name="context">The context.</param>
    /// <param name="loadRunId">The run to close.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static Task CompleteAsync(
        RCRAInfoContext context,
        int loadRunId,
        CancellationToken cancellationToken = default) =>
        context.CompleteLoadRunAsync(
            loadRunId, "Succeeded", failureMessage: null, new LoadRunCounters(), cancellationToken);

    /// <summary>
    /// Soft-deletes the given keys, so that the merge that follows is guaranteed to report them.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="loadRunId">The run to attribute the retirement to.</param>
    /// <param name="handlerId">The handler.</param>
    /// <param name="sequences">The versions to retire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// A no-op the first time a key is used, which is correct: a key that does not exist yet will be
    /// inserted, and an insert is reported too.
    /// </remarks>
    public static async Task RetireAsync(
        RCRAInfoContext context,
        int loadRunId,
        string handlerId,
        IEnumerable<int> sequences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sequences);

        await context.SoftDeleteHandlerSourceSetAsync(
            loadRunId,
            [.. sequences.Select(s => TestData.Key(handlerId, s))],
            TestData.SoftDeleteReason,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A sequence number this handler has never used, counting soft-deleted versions.</summary>
    /// <param name="handlerId">The handler. Must be one of the reserved identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One more than the highest sequence on record.</returns>
    /// <remarks>
    /// Read straight from the table rather than through a procedure, because every read procedure
    /// filters <c>IsDeleted = 0</c> and the whole point of this value is that it must not collide with
    /// a soft-deleted row. Windows authentication, so this runs as the developer rather than as either
    /// application login — the two logins have no rights beyond EXECUTE and this is not a query either
    /// application is entitled to make.
    /// </remarks>
    public static async Task<int> NextSequenceAsync(
        string handlerId,
        CancellationToken cancellationToken = default)
    {
        // Interpolated rather than parameterised, and safe because the value is asserted to be one of
        // this suite's own reserved identifiers first: TestData.HandlerId produces 'ZZTEST' followed by
        // six digits and nothing else. An identifier that failed that assertion would be a defect in
        // the test, not an injection -- but the assertion is what makes the interpolation defensible,
        // so it stays ahead of the string.
        Assert.StartsWith(TestData.HandlerIdPrefix, handlerId, StringComparison.Ordinal);
        Assert.Equal(12, handlerId.Length);
        Assert.All(handlerId[TestData.HandlerIdPrefix.Length..], c => Assert.True(char.IsAsciiDigit(c)));

        string sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             SELECT ISNULL (MAX (Sequence), 0) + 1
               FROM dbo.HandlerSource
              WHERE HandlerId  = N'{handlerId}'
                AND SourceType = N'{TestData.SourceType}';
             """);

        await using SqlConnection connection =
            await IntegrationServer.OpenAsync(cancellationToken).ConfigureAwait(false);

        int? next = await IntegrationServer
            .ScalarAsync<int>(connection, sql, cancellationToken).ConfigureAwait(false);

        // ISNULL means the aggregate cannot come back null, so a null here is the connection returning
        // nothing at all rather than an empty table.
        Assert.NotNull(next);
        return next.Value;
    }
}
