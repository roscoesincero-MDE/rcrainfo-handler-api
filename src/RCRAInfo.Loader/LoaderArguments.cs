using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader;

/// <summary>
/// The console application's command line, parsed once and refused as a whole if any part of it is wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unrecognised switch is refused, and that is the reason this type exists rather than three
/// <c>args.Any</c> calls.</b> Every switch here selects something <i>narrower</i> than the default, so a typo
/// does not fail — it is ignored, and the default runs. <c>--handerid MDD000000001</c> would silently start a
/// full scheduled load, against the watermark, from an operator who meant to look at one site; and on a fresh
/// database that is the initial load. So an argument this application does not recognise stops it.
/// </para>
/// <para>
/// <b>Neither scope switch has a default, and asking for a handler without one is refused.</b> The two paths
/// cost different amounts and answer different questions (see <see cref="TargetedVersionScope"/>), and a
/// default would be silently wrong in whichever direction it was set: defaulting to the latest record hides a
/// history the operator asked to see, and defaulting to the whole history spends thirty requests to answer a
/// question about one.
/// </para>
/// <para>
/// <b><c>--probe-summaries</c> is the one mode that is not a load, and it is refused in combination with
/// either of the others.</b> It reports on a single summaries window and writes nothing anywhere — no run
/// row, no status row, no watermark — so combining it with a switch that loads something would produce one
/// console block for a mode that measured and another for a mode that wrote, with nothing distinguishing
/// them. The two dates are both required and neither has a default: the window is the only thing the probe
/// measures, so inferring one would be inventing the measurement.
/// </para>
/// <para>
/// <b>Nothing here reads or reports a credential.</b> The only values it echoes are its own switches, the
/// handler identifier and a date — and the handler identifier is not a secret, as script 506 and the plan
/// both say.
/// </para>
/// </remarks>
/// <param name="SeedOnly">The AR4 seeding run: prove and seal the credentials, load nothing.</param>
/// <param name="Targeted">
/// The single-handler run, or <see langword="null"/> for the scheduled load the watermark governs.
/// </param>
/// <param name="Probe">
/// The summaries window to report on, or <see langword="null"/>. Set only for <c>--probe-summaries</c>, one of
/// the two modes here that open no run row and write nothing at all.
/// </param>
/// <param name="SourceProbe">
/// The handler whose record detail to report on, or <see langword="null"/>. Set only for
/// <c>--probe-source</c>, the other mode that writes nothing. It answers the open half of G25.
/// </param>
/// <param name="Problems">
/// Everything wrong with the command line, in a form safe to print. Non-empty means nothing should be
/// attempted — not even the credential bootstrap, which writes to the credential file.
/// </param>
internal sealed record LoaderArguments(
    bool SeedOnly,
    TargetedLoadRequest? Targeted,
    SummaryProbeRequest? Probe,
    SourceProbeRequest? SourceProbe,
    IReadOnlyList<string> Problems)
{
    private const string SeedOnlySwitch = "--seed-only";
    private const string HandlerIdSwitch = "--handler-id";
    private const string CurrentRecordSwitch = "--current-record";
    private const string EveryVersionSwitch = "--every-version";
    private const string ProbeSummariesSwitch = "--probe-summaries";
    private const string ProbeSourceSwitch = "--probe-source";

    /// <summary>One line per switch, for a refusal that has to be actionable at 2am.</summary>
    public const string Usage =
        "Usage:\n"
        + "  RCRAInfo.Loader                                     the scheduled load, scoped by "
        + "config.LoadWatermark\n"
        + "  RCRAInfo.Loader --seed-only                         prove and seal the credentials; load "
        + "nothing\n"
        + "  RCRAInfo.Loader --handler-id <id> --current-record   one handler, the version EPA marks current\n"
        + "  RCRAInfo.Loader --handler-id <id> --every-version    one handler, its entire history\n"
        + "  RCRAInfo.Loader --probe-summaries <from> <to>        report on one summaries window "
        + "(yyyy-MM-dd); writes nothing\n"
        + "  RCRAInfo.Loader --probe-source <id>                  report EPA's raw date text for one "
        + "handler's record detail; writes nothing";

    /// <summary>Parses the command line.</summary>
    /// <param name="args">What <c>Main</c> was given.</param>
    /// <returns>The parse, including its problems. Never throws and never null.</returns>
    public static LoaderArguments Parse(string[] args)
    {
        List<string> problems = [];
        bool seedOnly = false;
        bool currentRecord = false;
        bool everyVersion = false;
        string? handlerId = null;
        bool handlerIdGiven = false;
        bool probeGiven = false;
        string? probeFrom = null;
        string? probeTo = null;
        bool sourceProbeGiven = false;
        string? sourceProbeHandlerId = null;

        for (int index = 0; index < (args?.Length ?? 0); index++)
        {
            // Ordinal, because a switch is not text a user typed in a locale. Case-insensitive, because a
            // Task Scheduler action's argument list is edited in a dialog box.
            string argument = args![index];

            if (Is(argument, SeedOnlySwitch))
            {
                seedOnly = true;
            }
            else if (Is(argument, CurrentRecordSwitch))
            {
                currentRecord = true;
            }
            else if (Is(argument, EveryVersionSwitch))
            {
                everyVersion = true;
            }
            else if (Is(argument, HandlerIdSwitch))
            {
                handlerIdGiven = true;

                // The next argument, unless it is itself a switch -- otherwise "--handler-id --every-version"
                // would take the scope switch as the identifier and then report the scope as missing, which
                // sends the operator to fix the wrong half.
                handlerId = NextValue(args, ref index);
            }
            else if (Is(argument, ProbeSummariesSwitch))
            {
                probeGiven = true;

                // Two values, and both are taken the same way --handler-id takes its one: a following
                // argument that is itself a switch is left alone, so "--probe-summaries --seed-only" reports
                // the missing dates rather than trying to parse a switch as one.
                probeFrom = NextValue(args, ref index);
                probeTo = NextValue(args, ref index);
            }
            else if (Is(argument, ProbeSourceSwitch))
            {
                sourceProbeGiven = true;

                // One value, taken the way --handler-id takes its one.
                sourceProbeHandlerId = NextValue(args, ref index);
            }
            else if (argument.StartsWith(ProbeSourceSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                sourceProbeGiven = true;
                sourceProbeHandlerId = argument[(ProbeSourceSwitch.Length + 1)..];
            }
            else if (argument.StartsWith(HandlerIdSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                handlerIdGiven = true;
                handlerId = argument[(HandlerIdSwitch.Length + 1)..];
            }
            else
            {
                // THE LINE THIS TYPE EXISTS FOR. Every switch narrows what the application does, so an
                // unrecognised one is ignored by an args.Any check and the full scheduled load runs instead.
                problems.Add(
                    $"'{argument}' is not an argument this application recognises, so nothing was attempted. "
                    + "It is refused rather than ignored because every switch here asks for LESS than the "
                    + "default: a mistyped one would start the full scheduled load, which on a fresh database "
                    + "is the initial load.");
            }
        }

        if (handlerIdGiven && handlerId is null)
        {
            problems.Add($"{HandlerIdSwitch} was given no value.");
        }

        if (probeGiven && seedOnly)
        {
            problems.Add(
                $"{ProbeSummariesSwitch} and {SeedOnlySwitch} are separate errands and neither was run. The "
                + "probe needs a proved credential, so run the seeding step first and then the probe.");
        }

        if (probeGiven && handlerIdGiven)
        {
            problems.Add(
                $"{ProbeSummariesSwitch} and {HandlerIdSwitch} ask different questions of the same feed, so "
                + "neither was asked. The probe reports on a date window across every handler; "
                + $"{HandlerIdSwitch} loads one handler and takes no dates.");
        }

        if (seedOnly && handlerIdGiven)
        {
            problems.Add(
                $"{SeedOnlySwitch} and {HandlerIdSwitch} ask for opposite things -- one loads nothing and the "
                + "other loads one handler -- so neither was done. Run the seeding step first, then the "
                + "targeted load.");
        }

        if (sourceProbeGiven && seedOnly)
        {
            problems.Add(
                $"{ProbeSourceSwitch} and {SeedOnlySwitch} are separate errands and neither was run. The probe "
                + "needs a proved credential, so run the seeding step first and then the probe.");
        }

        if (sourceProbeGiven && handlerIdGiven)
        {
            problems.Add(
                $"{ProbeSourceSwitch} and {HandlerIdSwitch} both name a handler but do opposite things, so "
                + $"neither was done: {ProbeSourceSwitch} reports EPA's raw date text and writes NOTHING, "
                + $"while {HandlerIdSwitch} loads the handler into the database. Combining them would produce "
                + "one console block for a mode that measured and another for a mode that wrote.");
        }

        if (sourceProbeGiven && probeGiven)
        {
            problems.Add(
                $"{ProbeSourceSwitch} and {ProbeSummariesSwitch} are two probes of two different feeds and "
                + "neither was run. Run them one at a time, or the two reports arrive interleaved with nothing "
                + $"saying which feed each line came from -- which is the one distinction {ProbeSourceSwitch} "
                + "exists to draw.");
        }

        if (currentRecord && everyVersion)
        {
            problems.Add(
                $"{CurrentRecordSwitch} and {EveryVersionSwitch} are alternatives. Pass one: the entire "
                + "history includes the current record, so asking for both is a request whose intent cannot "
                + "be guessed.");
        }

        if (!handlerIdGiven && (currentRecord || everyVersion))
        {
            problems.Add(
                $"{CurrentRecordSwitch} and {EveryVersionSwitch} only apply to {HandlerIdSwitch}. Passed "
                + "alone they would be silently ignored and the full scheduled load would run.");
        }

        if (handlerIdGiven && !currentRecord && !everyVersion)
        {
            problems.Add(
                $"{HandlerIdSwitch} needs a scope: {CurrentRecordSwitch} for the version EPA marks as its "
                + $"current record, or {EveryVersionSwitch} for the handler's entire history. There is "
                + "deliberately no default -- the two cost different amounts and answer different questions, "
                + "and a wrong guess either hides history or spends a request per version to answer a "
                + "question about one.");
        }

        SummaryProbeRequest? probe = null;

        if (probeGiven)
        {
            // Parsed even when the combination refusals above already fired, so that an operator who passed
            // both a bad date and a bad combination is told about both in one run rather than fixing one and
            // discovering the other.
            problems.AddRange(
                SummaryProbeRequest.TryParse(probeFrom, probeTo, ProbeSummariesSwitch, out probe));
        }

        SourceProbeRequest? sourceProbe = null;

        if (sourceProbeGiven)
        {
            // Built even when a combination refusal above already fired, for the reason the summaries window
            // is parsed anyway: an operator who passed both a bad identifier and a bad combination is told
            // about both in one run rather than fixing one and discovering the other.
            sourceProbe = new SourceProbeRequest(sourceProbeHandlerId ?? string.Empty);
            problems.AddRange(sourceProbe.Validate());
        }

        TargetedLoadRequest? targeted = handlerId is not null && currentRecord != everyVersion
            ? new TargetedLoadRequest(
                handlerId,
                everyVersion ? TargetedVersionScope.EveryVersion : TargetedVersionScope.CurrentRecord)
            : null;

        if (targeted is not null)
        {
            // The request validates itself, so the identifier's width and shape are refused here -- before a
            // credential is proved and before a run row is opened -- rather than by the orchestrator's throw.
            problems.AddRange(targeted.Validate());
        }

        return new LoaderArguments(
            seedOnly,
            problems.Count == 0 ? targeted : null,
            problems.Count == 0 ? probe : null,
            problems.Count == 0 ? sourceProbe : null,
            problems);
    }

    private static bool Is(string argument, string name) =>
        string.Equals(argument, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes the next argument as a value, unless it is itself a switch.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="index">The current position, advanced only when a value is actually taken.</param>
    /// <returns>The value, or <see langword="null"/> when the next argument is a switch or there is none.</returns>
    /// <remarks>
    /// Leaving a switch where it is rather than swallowing it is what makes the refusals point at the right
    /// half of the command line: <c>--handler-id --every-version</c> reports a missing identifier, not a
    /// missing scope, and <c>--probe-summaries 2026-09-01</c> reports one missing date rather than parsing
    /// whatever follows.
    /// </remarks>
    private static string? NextValue(string[] args, ref int index) =>
        index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++index]
            : null;
}
