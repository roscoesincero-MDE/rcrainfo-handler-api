using Microsoft.Extensions.Logging;

using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>One call the journal made to the database, flattened for assertion.</summary>
/// <param name="Procedure">Either <c>520</c> or <c>524</c>, by the short name used in the scripts.</param>
/// <param name="Mode">Script 520's mode, or null for 524.</param>
/// <param name="Count">How many elements the call carried.</param>
internal readonly record struct JournalCall(string Procedure, string? Mode, int Count)
{
    public override string ToString() => Mode is null ? $"{Procedure}({Count})" : $"{Procedure}:{Mode}({Count})";
}

/// <summary>
/// A writer that records what it was asked to write, in order, and can be made to fail or to report
/// defects.
/// </summary>
/// <remarks>
/// The ordering assertions are the point of this double. Flush order is the journal's single most
/// consequential behaviour — script 524 resolves attempts against status rows the same run enumerated, and
/// script 520's later modes update the row <c>Enumerate</c> creates — and nothing about it is observable
/// from the outside except the sequence of calls.
/// </remarks>
internal sealed class RecordingJournalWriter : ILoadJournalWriter
{
    public int MaxElementsPerCall { get; set; } = 500;

    /// <summary>Every call, in the order it was made.</summary>
    public List<JournalCall> Calls { get; } = [];

    /// <summary>Every status element received, by mode.</summary>
    public Dictionary<string, List<HandlerLoadStatusElement>> Status { get; } = [];

    /// <summary>
    /// Each 520 call's elements, kept separately from <see cref="Status"/>. Script 520 refuses a
    /// <i>single</i> payload naming one version twice, so the duplicate rule is per call and asserting it
    /// needs the call boundaries that a flattened list throws away.
    /// </summary>
    public List<(string Mode, HandlerLoadStatusElement[] Elements)> StatusBatches { get; } = [];

    /// <summary>Every attempt element received.</summary>
    public List<HandlerLoadAttemptElement> Attempts { get; } = [];

    /// <summary>Thrown by the next call, if set. Cleared once thrown.</summary>
    public Exception? ThrowOnce { get; set; }

    /// <summary>Reported by every 524 call.</summary>
    public int OrphanPerCall { get; set; }

    /// <summary>Reported by every 524 call.</summary>
    public int WithheldPerCall { get; set; }

    public Task<int> UpsertStatusAsync(
        int loadRunId,
        string mode,
        IReadOnlyCollection<HandlerLoadStatusElement> elements,
        CancellationToken cancellationToken = default)
    {
        Throw();

        Calls.Add(new JournalCall("520", mode, elements.Count));
        StatusBatches.Add((mode, [.. elements]));

        if (!Status.TryGetValue(mode, out List<HandlerLoadStatusElement>? received))
        {
            received = [];
            Status[mode] = received;
        }

        received.AddRange(elements);

        return Task.FromResult(elements.Count);
    }

    public Task<AttemptWriteCounts> RecordAttemptsAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerLoadAttemptElement> elements,
        CancellationToken cancellationToken = default)
    {
        Throw();

        Calls.Add(new JournalCall("524", null, elements.Count));
        Attempts.AddRange(elements);

        return Task.FromResult(
            new AttemptWriteCounts(elements.Count, OrphanPerCall, WithheldPerCall));
    }

    /// <summary>The modes seen, in order, ignoring the 524 calls.</summary>
    public string[] Modes => [.. Calls.Where(c => c.Mode is not null).Select(c => c.Mode!)];

    private void Throw()
    {
        if (ThrowOnce is null)
        {
            return;
        }

        Exception error = ThrowOnce;
        ThrowOnce = null;

        throw error;
    }
}

/// <summary>A logger that keeps what it was given, so the swallowed-dispose path is observable.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>One refresh the stage asked script 523 for, flattened for assertion.</summary>
/// <param name="LookupName">Script 523's <c>@LookupName</c>.</param>
/// <param name="Mode">The mode sent. Should always be <c>Full</c>.</param>
/// <param name="Count">How many codes the payload carried.</param>
internal readonly record struct LookupCall(string LookupName, string Mode, int Count)
{
    public override string ToString() => $"{LookupName}:{Mode}({Count})";
}

/// <summary>
/// A script 523 seam that records what it was asked to refresh and can be made to fail.
/// </summary>
/// <remarks>
/// The recorded <i>mode</i> is the assertion that matters. <c>Upsert</c> would refresh every list and retire
/// nothing, so a list EPA has withdrawn a code from would keep it live and current forever, with no error
/// anywhere — the only place that mistake is visible is in the argument, before the call.
/// </remarks>
internal sealed class RecordingLookupWriter : ILookupRefreshWriter
{
    public int MaxElementsPerCall { get; set; } = 500;

    /// <summary>Every call, in order.</summary>
    public List<LookupCall> Calls { get; } = [];

    /// <summary>Every payload received, by lookup name.</summary>
    public Dictionary<string, LookupElement[]> Payloads { get; } = [];

    /// <summary>Thrown by every call whose lookup name is in here.</summary>
    public Dictionary<string, Exception> ThrowFor { get; } = [];

    /// <summary>Reported by every call.</summary>
    public int RetirePerCall { get; set; }

    public Task<LookupWriteCounts> RefreshAsync(
        int loadRunId,
        string lookupName,
        string mode,
        IReadOnlyCollection<LookupElement> elements,
        CancellationToken cancellationToken = default)
    {
        if (ThrowFor.TryGetValue(lookupName, out Exception? error))
        {
            throw error;
        }

        Calls.Add(new LookupCall(lookupName, mode, elements.Count));
        Payloads[lookupName] = [.. elements];

        int childRows = elements.Sum(element => element.Counties?.Count ?? 0);

        return Task.FromResult(new LookupWriteCounts(elements.Count, RetirePerCall, childRows));
    }
}

/// <summary>A data client that answers from a table of canned responses, and records what was asked.</summary>
/// <remarks>
/// Keyed by <c>RelativeUri</c> and not by path, because the query string is the thing under test: the
/// <c>stateCode</c> belongs on exactly seven of the 23 endpoints, and a stage that sent it everywhere or
/// nowhere would still fetch 23 payloads and still report success.
/// </remarks>
internal sealed class StubDataClient : IRcraInfoDataClient
{
    private readonly Func<RcraInfoDataRequest, ApiFetchResult> answer;

    public StubDataClient(Func<RcraInfoDataRequest, ApiFetchResult> answer) => this.answer = answer;

    /// <summary>Every relative URI requested, in order.</summary>
    public List<string> RequestedUris { get; } = [];

    /// <summary>Only the per-version payload fetches, in order — no summaries call.</summary>
    /// <remarks>
    /// <b>The number a request-count assertion almost always means.</b> A scheduled run also asks
    /// <c>/hd/sources/summaries?handlerId=…</c> once per handler for the <c>CurrentRecord</c> reconciliation
    /// (§D4), and that call is the reconcile's cost rather than the fetch's — so a test asserting that one
    /// version was asked for, or that <c>other-ids</c> was never asked for, wants the payload fetches alone.
    /// Filtered by the marker rather than by <c>RcraInfoDataEndpoint</c> because what is recorded is the URI,
    /// which is also what such a test is about.
    /// </remarks>
    public IReadOnlyList<string> VersionUris =>
        [.. RequestedUris.Where(uri => !uri.Contains("summaries", StringComparison.Ordinal))];

    public Task<ApiFetchResult> FetchAsync(
        RcraInfoDataRequest request,
        CancellationToken cancellationToken = default)
    {
        // Locked because LoadRun's fetch loop is parallel, and the lookup stages that also use this double
        // are not. An unsynchronised List.Add across threads corrupts the list rather than reordering it,
        // and it does so intermittently -- which is the worst way for a test suite to be wrong.
        lock (RequestedUris)
        {
            RequestedUris.Add(request.RelativeUri);
        }

        return Task.FromResult(answer(request));
    }
}

/// <summary>
/// A script 525 seam that answers with canned rows and records the four arguments it was given.
/// </summary>
/// <remarks>
/// <b>The recorded arguments are the assertion that matters.</b> The threshold is the one value that has
/// to reach two procedures identically, and nothing downstream can notice a mismatch: a resume that
/// quietly did not happen looks exactly like a first run. So the only place it is observable is in the
/// argument, before the call — the same reasoning as <see cref="RecordingLookupWriter"/>'s mode.
/// </remarks>
internal sealed class RecordingResumeReader : ILoadResumeReader
{
    /// <summary>Every call's arguments, in order.</summary>
    public List<(string ActivityLocation, int? LoadRunId, int? AbandonAfterMinutes, int? MaxAgeHours)> Calls
    { get; } = [];

    /// <summary>What the next call answers with.</summary>
    public List<HandlerLoadResumeRow> Rows { get; set; } = [];

    /// <summary>Thrown by every call, if set — script 525's three refusals arrive this way.</summary>
    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<HandlerLoadResumeRow>> ReadAsync(
        string activityLocation,
        int? loadRunId,
        int? abandonAfterMinutes,
        int? maxAgeHours,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((activityLocation, loadRunId, abandonAfterMinutes, maxAgeHours));

        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult<IReadOnlyList<HandlerLoadResumeRow>>([.. Rows]);
    }
}

/// <summary>Builders for script 525's row shape.</summary>
internal static class Resumed
{
    /// <summary>The run every row in a set belongs to, unless a test says otherwise.</summary>
    public static readonly DateTimeOffset Started = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

    /// <summary>One row of the resume set.</summary>
    public static HandlerLoadResumeRow Row(
        string handlerId = "MDD000000001",
        int sequence = 1,
        string status = "Succeeded",
        int loadRunId = 100,
        string runStatus = "Abandoned",
        string runMode = "Full",
        DateTimeOffset? startedUtc = null,
        int attemptCount = 1) =>
        new()
        {
            ResumedFromLoadRunId = loadRunId,
            ResumedFromRunMode = runMode,
            ResumedFromStatus = runStatus,
            ResumedFromStartedDateUtc = startedUtc ?? Started,
            ResumedFromRequestedFromDate = new DateOnly(2026, 8, 30),
            ResumedFromRequestedToDate = new DateOnly(2026, 9, 5),
            HandlerId = handlerId,
            ActivityLocation = "MD",
            SourceType = "N",
            Sequence = sequence,
            Status = status,
            AttemptCount = attemptCount,
        };
}

/// <summary>Builders for the two shapes the journal takes in.</summary>
internal static class Journalled
{
    public static HandlerVersion Version(string handlerId = "MDD000000001", int sequence = 1) =>
        new(handlerId, "N", sequence);

    public static HandlerLoadStatusElement Pending(string handlerId = "MDD000000001", int sequence = 1) =>
        new()
        {
            HandlerId = handlerId,
            ActivityLocation = "MD",
            SourceType = "N",
            Sequence = sequence,
        };

    /// <summary>An <see cref="ApiFetchResult"/> of the given outcome, with whatever detail it would carry.</summary>
    public static ApiFetchResult Result(
        ApiFetchOutcome outcome,
        int? httpStatusCode = null,
        string? apiErrorCode = null,
        string? apiErrorMessage = null,
        RcraInfoDataRequest? request = null)
    {
        DateTimeOffset started = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

        return new ApiFetchResult
        {
            Outcome = outcome,
            Request = request ?? RcraInfoDataRequest.Source("MDD000000001", "N", 1),
            StartedDateUtc = started,
            CompletedDateUtc = started.AddMilliseconds(120),
            DurationMs = 120,
            Payload = outcome == ApiFetchOutcome.Succeeded ? "{}" : null,
            HttpStatusCode = httpStatusCode,
            ApiErrorCode = apiErrorCode,
            ApiErrorMessage = apiErrorMessage,
        };
    }

    /// <summary>A lookup call that succeeded, carrying the given body verbatim.</summary>
    public static ApiFetchResult LookupResult(RcraInfoDataRequest request, string payload)
    {
        DateTimeOffset started = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

        return new ApiFetchResult
        {
            Outcome = ApiFetchOutcome.Succeeded,
            Request = request,
            StartedDateUtc = started,
            CompletedDateUtc = started.AddMilliseconds(40),
            DurationMs = 40,
            Payload = payload,
            HttpStatusCode = 200,
            ResponseBytes = payload.Length,
        };
    }

    /// <summary>A lookup call that did not come back with a payload.</summary>
    public static ApiFetchResult LookupFailure(
        RcraInfoDataRequest request,
        ApiFetchOutcome outcome,
        int? httpStatusCode = null,
        string? apiErrorId = null)
    {
        DateTimeOffset started = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

        return new ApiFetchResult
        {
            Outcome = outcome,
            Request = request,
            StartedDateUtc = started,
            CompletedDateUtc = started.AddMilliseconds(40),
            DurationMs = 40,
            HttpStatusCode = httpStatusCode,
            ApiErrorId = apiErrorId,
        };
    }

    /// <summary>A minimal one-code payload, in EPA's own casing.</summary>
    public static string OneCode(string code = "01", string? activityLocation = "MD") =>
        activityLocation is null
            ? $$"""[{"code":"{{code}}","description":"A code","active":true}]"""
            : $$"""[{"activityLocation":"{{activityLocation}}","code":"{{code}}","description":"A code","active":true}]""";
}
