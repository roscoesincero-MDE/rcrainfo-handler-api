using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The run's first stage. Every assertion here is about one asymmetry: <b>a failed lookup changes nothing, a
/// successful one can soft-delete reference data.</b>
/// </summary>
/// <remarks>
/// Script 523 in <c>Full</c> mode retires every code absent from the payload, within the activity locations
/// the payload mentions, and reports the count beside the write count. No status code says a payload was
/// narrower than intended and no constraint refuses it. So the tests that matter are the ones about what the
/// stage refuses to send.
/// </remarks>
public class LookupRefreshTests
{
    private const int RunId = 77;

    [Fact]
    public async Task AllTwentyThreeListsAreFetchedAndRefreshedInCatalogOrder()
    {
        (LookupRefresh stage, StubDataClient client, RecordingLookupWriter writer, _) = Build();

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        Assert.Equal(RcraInfoLookups.All.Count, report.Reports.Count);
        Assert.Equal(RcraInfoLookups.All.Count, report.RefreshedCount);
        Assert.Equal(0, report.UnrefreshedCount);
        Assert.True(report.MayContinue);
        Assert.Null(report.FatalOutcome);
        Assert.False(report.WasCancelled);

        Assert.Equal(
            [.. RcraInfoLookups.All.Select(l => l.Name)],
            writer.Calls.Select(c => c.LookupName));

        Assert.Equal(
            [.. RcraInfoLookups.All.Select(l => l.Path)],
            client.RequestedUris.Select(uri => uri.Split('?')[0]));
    }

    [Fact]
    public async Task EveryRefreshIsSentInFullModeAndNeverInUpsert()
    {
        // The load-bearing assertion of this file. Upsert refreshes every list and retires nothing, so a code
        // EPA has withdrawn stays live and current forever -- no error, no observation, no status code. The
        // only place that mistake is visible is in this argument, before the call.
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build();

        await stage.RefreshAllAsync(RunId);

        Assert.All(writer.Calls, call => Assert.Equal("Full", call.Mode));
    }

    [Fact]
    public async Task TheStateCodeIsSentOnExactlySevenListsAndTheEndpointDecidesWhich()
    {
        // Scope is fixed by the endpoint, not by configuration: sixteen definitions carry a required
        // activityLocation but only seven endpoints accept a stateCode, so nine scoped lists are fetched
        // nationally and merged unfiltered. Filtering them here would leave every other jurisdiction's stored
        // codes never refreshed and never retired.
        (LookupRefresh stage, StubDataClient client, _, _) = Build();

        await stage.RefreshAllAsync(RunId);

        Assert.Equal(7, client.RequestedUris.Count(uri => uri.Contains("stateCode=MD", StringComparison.Ordinal)));
        Assert.Equal(16, client.RequestedUris.Count(uri => !uri.Contains('?', StringComparison.Ordinal)));
        Assert.DoesNotContain(client.RequestedUris, uri => uri.Contains("federal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnEmptyArrayIsRefusedRatherThanRetiringTheWholeList()
    {
        // EPA answering 200 with [] is a successful, well-formed response. In Full mode it means "retire
        // every code in this list", and script 523 refuses it as well -- refused here too so the message can
        // name the endpoint that returned nothing, which 523 cannot know.
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build(
            request => Journalled.LookupResult(request, request.Path.EndsWith("naics", StringComparison.Ordinal) ? "[]" : Journalled.OneCode()));

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        LookupRefreshReport naics = Single(report, "Naics");

        Assert.Equal(LookupRefreshStatus.EmptyPayload, naics.Status);
        Assert.DoesNotContain(writer.Calls, call => call.LookupName == "Naics");

        // And the other 22 still refreshed. One list's refusal must not cost the rest.
        Assert.Equal(22, report.RefreshedCount);
        Assert.True(report.MayContinue);
    }

    [Fact]
    public async Task APayloadTooLargeForOneCallIsRefusedRatherThanSplit()
    {
        // No /lookup/hd endpoint pages, so a split is not a page -- it is two Full-mode calls, and the second
        // retires everything the first wrote.
        string big = "["
            + string.Join(",", Enumerable.Range(1, 12).Select(i => $$"""{"code":"{{i:000}}"}"""))
            + "]";

        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build(
            request => Journalled.LookupResult(request, big));

        writer.MaxElementsPerCall = 10;

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        Assert.Empty(writer.Calls);
        Assert.All(report.Reports, r => Assert.Equal(LookupRefreshStatus.TooLarge, r.Status));
        Assert.Contains("cannot be split", Single(report, "Naics").Problem, StringComparison.Ordinal);

        // Still not fatal: no code was retired, and every list is one run stale rather than wrong.
        Assert.True(report.MayContinue);
    }

    [Fact]
    public async Task AFetchThatFailsLeavesTheMirrorUntouchedAndTheRunContinues()
    {
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build(
            request => request.Path.EndsWith("counties", StringComparison.Ordinal)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.ServiceFailure, 503)
                : Journalled.LookupResult(request, Journalled.OneCode()));

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        LookupRefreshReport counties = Single(report, "County");

        Assert.Equal(LookupRefreshStatus.FetchFailed, counties.Status);
        Assert.Equal(ApiFetchOutcome.ServiceFailure, counties.FetchOutcome);
        Assert.DoesNotContain(writer.Calls, call => call.LookupName == "County");

        // G15's reasoning: refreshing the lookups first makes an unknown code rare, not impossible, and
        // logs.DataQualityObservation handles the rest. Stopping the load would trade a handful of
        // observations for a night's worth of handler data.
        Assert.True(report.MayContinue);
        Assert.Equal(22, report.RefreshedCount);
    }

    [Fact]
    public async Task ARejectedPayloadIsReportedAndNothingIsSentToScript523()
    {
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build(
            request => Journalled.LookupResult(
                request,
                request.Path.EndsWith("states", StringComparison.Ordinal)
                    ? """{ "items": [] }"""
                    : Journalled.OneCode()));

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        Assert.Equal(LookupRefreshStatus.PayloadRejected, Single(report, "State").Status);
        Assert.DoesNotContain(writer.Calls, call => call.LookupName == "State");
        Assert.True(report.MayContinue);
    }

    [Theory]
    [InlineData(ApiFetchOutcome.Unauthorized, 401)]
    [InlineData(ApiFetchOutcome.AccessDenied, 403)]
    public async Task AFatalOutcomeStopsTheStageAndEveryRemainingListStillGetsAReport(
        ApiFetchOutcome outcome,
        int status)
    {
        // A credential or scope problem fails identically on the next list and on every handler after it, so
        // continuing would turn one refusal into 23 and then into several hundred thousand.
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build(
            request => request.Path.EndsWith("contact-types", StringComparison.Ordinal)
                ? Journalled.LookupFailure(request, outcome, status)
                : Journalled.LookupResult(request, Journalled.OneCode()));

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        Assert.False(report.MayContinue);
        Assert.Equal(outcome, report.FatalOutcome);

        // ContactType is second in the catalog: Accessibility refreshed, ContactType failed, 21 not attempted.
        Assert.Equal(RcraInfoLookups.All.Count, report.Reports.Count);
        Assert.Equal(1, report.RefreshedCount);
        Assert.Single(writer.Calls);
        Assert.Equal(
            21,
            report.Reports.Count(r => r.Status == LookupRefreshStatus.NotAttempted));
        Assert.Contains(
            "fatal to the run",
            Single(report, "WasteCode").Problem,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteThatThrowsCostsOneListAndNotTheOtherTwentyTwo()
    {
        (LookupRefresh stage, _, RecordingLookupWriter writer, RecordingLogger<LookupRefresh> logger) = Build();

        writer.ThrowFor["Language"] = new InvalidOperationException(
            "Login failed for user 'RCRAInfoLoader' on server sql-uat-01.");

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        LookupRefreshReport language = Single(report, "Language");

        Assert.Equal(LookupRefreshStatus.WriteFailed, language.Status);
        Assert.Equal(22, report.RefreshedCount);
        Assert.True(report.MayContinue);

        // The exception TYPE, never its message: this string reaches logs.LoadRun.FailureMessage, which the
        // monitoring web application reads, and a connection failure's message names the server and the
        // login. The exception itself is attached to the log entry, where the destination is the operator's.
        Assert.Contains("InvalidOperationException", language.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("sql-uat-01", language.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("RCRAInfoLoader", language.Problem, StringComparison.Ordinal);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Error is InvalidOperationException);
    }

    [Fact]
    public async Task TheRetirementCountIsCarriedUpBecauseNothingElseReportsIt()
    {
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build();

        writer.RetirePerCall = 4;

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        Assert.Equal(4 * RcraInfoLookups.All.Count, report.TotalRetired);
    }

    [Fact]
    public async Task AnUnexpectedPropertyIsCarriedUpAndLoggedWithItsName()
    {
        (LookupRefresh stage, _, _, RecordingLogger<LookupRefresh> logger) = Build(
            request => Journalled.LookupResult(
                request, """[{ "activityLocation": "MD", "code": "01", "federalOnly": true }]"""));

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        // Every list refreshed -- the codes all arrived. What is being reported is that something about them
        // was discarded, which is invisible from the data.
        Assert.Equal(RcraInfoLookups.All.Count, report.RefreshedCount);
        Assert.Equal(RcraInfoLookups.All.Count, report.WithUnexpectedProperties.Count);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("federalOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNestedCountiesReachScript523InTheSameCallAsTheirDistrict()
    {
        // dbo.LookupStateDistrictCounty is the reason 23 definitions became 24 tables. Merged in the same
        // call and the same transaction as the parent, and retired with it: a county left live under a
        // district EPA no longer publishes would read as current.
        (LookupRefresh stage, _, RecordingLookupWriter writer, _) = Build(
            request => Journalled.LookupResult(
                request,
                request.Path.EndsWith("state-districts", StringComparison.Ordinal)
                    ? """
                      [{ "activityLocation": "MD", "code": "SW",
                         "counties": [{ "activityLocation": "MD", "code": "003" },
                                      { "activityLocation": "MD", "code": "005" }] }]
                      """
                    : Journalled.OneCode()));

        await stage.RefreshAllAsync(RunId);

        LookupElement district = Assert.Single(writer.Payloads["StateDistrict"]);

        Assert.Equal("SW", district.Code);
        Assert.Equal(["003", "005"], district.Counties!.Select(c => c.Code));
    }

    [Fact]
    public async Task ACancelledRunStopsAndIsNotReportedAsAFatalOutcome()
    {
        // An orderly shutdown and a credential problem both stop the stage; folding them together would put
        // a shutdown in front of an operator as something to fix.
        (LookupRefresh stage, _, _, _) = Build(
            request => request.Path.EndsWith("countries", StringComparison.Ordinal)
                ? Journalled.LookupFailure(request, ApiFetchOutcome.Cancelled)
                : Journalled.LookupResult(request, Journalled.OneCode()));

        LookupStageReport report = await stage.RefreshAllAsync(RunId);

        Assert.True(report.WasCancelled);
        Assert.Null(report.FatalOutcome);
        Assert.False(report.MayContinue);
        Assert.Contains("cancelled", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenAttemptsNothingAndStillReportsTwentyThreeLists()
    {
        (LookupRefresh stage, StubDataClient client, RecordingLookupWriter writer, _) = Build();

        using CancellationTokenSource source = new();
        await source.CancelAsync();

        LookupStageReport report = await stage.RefreshAllAsync(RunId, source.Token);

        Assert.Empty(client.RequestedUris);
        Assert.Empty(writer.Calls);
        Assert.Equal(RcraInfoLookups.All.Count, report.Reports.Count);
        Assert.All(report.Reports, r => Assert.Equal(LookupRefreshStatus.NotAttempted, r.Status));
        Assert.True(report.WasCancelled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("M")]
    [InlineData("MDX")]
    public async Task AnUnusableActivityLocationStopsTheStageBeforeAnyCallIsMade(string location)
    {
        // Not reported as a per-list failure. Without an activity location the five lists that require a
        // stateCode cannot be requested at all, and a stage that fetched the other eighteen would look like
        // a partial success.
        (LookupRefresh stage, StubDataClient client, _, _) = Build(activityLocation: location);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stage.RefreshAllAsync(RunId));

        Assert.Contains("ActivityLocation", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.RequestedUris);
    }

    [Fact]
    public async Task TheStateCodeIsNormalisedFromWhateverConfigurationHolds()
    {
        (LookupRefresh stage, StubDataClient client, _, _) = Build(activityLocation: " md ");

        await stage.RefreshAllAsync(RunId);

        Assert.Equal(7, client.RequestedUris.Count(uri => uri.EndsWith("?stateCode=MD", StringComparison.Ordinal)));
    }

    private static LookupRefreshReport Single(LookupStageReport report, string lookupName) =>
        report.Reports.Single(r => r.Lookup.Name == lookupName);

    private static (
        LookupRefresh Stage,
        StubDataClient Client,
        RecordingLookupWriter Writer,
        RecordingLogger<LookupRefresh> Logger) Build(
        Func<RcraInfoDataRequest, ApiFetchResult>? answer = null,
        string activityLocation = "MD")
    {
        StubDataClient client = new(
            answer ?? (request => Journalled.LookupResult(request, Journalled.OneCode())));
        RecordingLookupWriter writer = new();
        RecordingLogger<LookupRefresh> logger = new();

        LookupRefresh stage = new(
            client,
            writer,
            Options.Create(new LoadRunOptions { ActivityLocation = activityLocation }),
            logger);

        return (stage, client, writer, logger);
    }
}
