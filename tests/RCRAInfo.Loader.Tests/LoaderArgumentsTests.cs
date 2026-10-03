using RCRAInfo.Loader;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests;

/// <summary>
/// <see cref="LoaderArguments"/>: the command line, and the reason a wrong one stops the application.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every switch this application has asks for something narrower than the default, and that inverts the
/// usual argument-parsing risk.</b> A typo in a switch that turns a feature <i>on</i> fails safely — the
/// feature is off. Here the default is the full scheduled load, so <c>--handerid MDD000000001</c> ignored
/// would start a population run against the watermark on behalf of an operator who meant to look at one site,
/// and on a fresh database that is the initial load: decades of history, thousands of requests, at whatever
/// hour it was typed. So an unrecognised argument is refused, and that refusal is what most of this suite is
/// about.
/// </para>
/// <para>
/// <b>The second theme is that a refusal must send the operator to the right half of the problem.</b>
/// <c>--handler-id --every-version</c> is missing an identifier, not a scope, and a parser that consumed the
/// scope switch as the identifier would report the scope missing instead — sending someone to fix the part
/// that was already right.
/// </para>
/// </remarks>
public sealed class LoaderArgumentsTests
{
    private const string Handler = "MDD000000001";

    /// <summary>No arguments is the scheduled load, and it is not a problem.</summary>
    /// <remarks>
    /// This is what Windows Task Scheduler invokes, so it has to be the one shape that needs nothing said.
    /// </remarks>
    [Fact]
    public void NoArgumentsIsTheScheduledLoad()
    {
        LoaderArguments arguments = LoaderArguments.Parse([]);

        Assert.Empty(arguments.Problems);
        Assert.False(arguments.SeedOnly);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>A null array is treated as none, rather than throwing before anything can be reported.</summary>
    [Fact]
    public void ANullArgumentArrayIsTreatedAsNone()
    {
        LoaderArguments arguments = LoaderArguments.Parse(null!);

        Assert.Empty(arguments.Problems);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>The seeding switch, in whatever casing a Task Scheduler dialog box produced.</summary>
    /// <param name="argument">The switch as typed.</param>
    [Theory]
    [InlineData("--seed-only")]
    [InlineData("--SEED-ONLY")]
    [InlineData("--Seed-Only")]
    public void TheSeedingSwitchIsRecognisedWhateverTheCasing(string argument)
    {
        LoaderArguments arguments = LoaderArguments.Parse([argument]);

        Assert.Empty(arguments.Problems);
        Assert.True(arguments.SeedOnly);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>Both scopes, in both the spaced and the equals form.</summary>
    /// <param name="first">The identifier switch, spaced or joined.</param>
    /// <param name="second">The value, or the scope where the first argument carried the value.</param>
    /// <param name="third">The scope, where there is a third argument.</param>
    /// <param name="expected">Which scope the parse should carry.</param>
    [Theory]
    [InlineData("--handler-id", Handler, "--current-record", TargetedVersionScope.CurrentRecord)]
    [InlineData("--handler-id", Handler, "--every-version", TargetedVersionScope.EveryVersion)]
    [InlineData("--handler-id=" + Handler, "--current-record", null, TargetedVersionScope.CurrentRecord)]
    [InlineData("--handler-id=" + Handler, "--every-version", null, TargetedVersionScope.EveryVersion)]
    [InlineData("--CURRENT-RECORD", "--Handler-Id", Handler, TargetedVersionScope.CurrentRecord)]
    public void AHandlerAndAScopeParseIntoATargetedRequest(
        string first,
        string second,
        string? third,
        TargetedVersionScope expected)
    {
        string[] args = third is null ? [first, second] : [first, second, third];

        LoaderArguments arguments = LoaderArguments.Parse(args);

        Assert.Empty(arguments.Problems);
        Assert.False(arguments.SeedOnly);

        TargetedLoadRequest targeted = Assert.IsType<TargetedLoadRequest>(arguments.Targeted);

        Assert.Equal(Handler, targeted.Normalized());
        Assert.Equal(expected, targeted.Scope);
    }

    /// <summary>
    /// An argument this application does not recognise stops it, and nothing is attempted.
    /// </summary>
    /// <remarks>
    /// <b>The line this whole type exists for.</b> Note the third case: the typo sits beside a perfectly good
    /// <c>--handler-id</c>, and the parse still refuses — because <see cref="LoaderArguments.Targeted"/> is
    /// withheld whenever there are problems at all. Half-honouring a command line is how an operator ends up
    /// having asked for one thing and got another.
    /// </remarks>
    /// <param name="args">The command line.</param>
    /// <param name="offending">The token the message must name.</param>
    [Theory]
    [InlineData(new[] { "--handerid" }, "--handerid")]
    [InlineData(new[] { "--handler_id", Handler }, "--handler_id")]
    [InlineData(new[] { "-h", Handler }, "-h")]
    [InlineData(new[] { "--full" }, "--full")]
    [InlineData(new[] { "--bogus", "--handler-id", Handler, "--current-record" }, "--bogus")]
    public void AnUnrecognisedArgumentIsRefusedRatherThanIgnored(string[] args, string offending)
    {
        LoaderArguments arguments = LoaderArguments.Parse(args);

        Assert.NotEmpty(arguments.Problems);
        Assert.Null(arguments.Targeted);

        string problem = Assert.Single(
            arguments.Problems,
            candidate => candidate.Contains(offending, StringComparison.Ordinal));

        Assert.Contains("nothing was attempted", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare value with no switch in front of it is unrecognised too.
    /// </summary>
    /// <remarks>
    /// The likeliest single mistake: an operator who remembers the handler but not the switch. Accepting it
    /// positionally would mean guessing the scope, which is the one thing the scope switches exist to prevent.
    /// </remarks>
    [Fact]
    public void AHandlerIdentifierWithNoSwitchInFrontOfItIsRefused()
    {
        LoaderArguments arguments = LoaderArguments.Parse([Handler]);

        Assert.Single(arguments.Problems);
        Assert.Null(arguments.Targeted);
        Assert.Contains(Handler, arguments.Problems[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--handler-id</c> followed by another switch reports the missing identifier, not a missing scope.
    /// </summary>
    /// <remarks>
    /// <b>One problem, and it is the right one.</b> A parser that took <c>--every-version</c> as the value
    /// would report "needs a scope" — and the operator would add the scope switch that was already there.
    /// </remarks>
    [Fact]
    public void AnIdentifierSwitchDoesNotSwallowTheScopeSwitchThatFollowsIt()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--handler-id", "--every-version"]);

        string problem = Assert.Single(arguments.Problems);

        Assert.Contains("--handler-id was given no value", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("needs a scope", problem, StringComparison.Ordinal);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>A handler with no scope is refused, and the message says there is no default on purpose.</summary>
    /// <remarks>
    /// Defaulting to the current record hides a history the operator asked to see; defaulting to the whole
    /// history spends a request per version to answer a question about one. Neither is a safe guess, so the
    /// scope is always stated.
    /// </remarks>
    [Fact]
    public void AHandlerWithNoScopeIsRefusedAndTheMessageSaysWhyThereIsNoDefault()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--handler-id", Handler]);

        string problem = Assert.Single(arguments.Problems);

        Assert.Contains("needs a scope", problem, StringComparison.Ordinal);
        Assert.Contains("--current-record", problem, StringComparison.Ordinal);
        Assert.Contains("--every-version", problem, StringComparison.Ordinal);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>Both scopes at once is refused, because the intent cannot be guessed.</summary>
    [Fact]
    public void BothScopesAtOnceIsRefused()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--handler-id", Handler, "--current-record", "--every-version"]);

        Assert.Contains(
            "alternatives", Assert.Single(arguments.Problems), StringComparison.Ordinal);

        Assert.Null(arguments.Targeted);
    }

    /// <summary>
    /// A scope switch with no handler is refused, rather than starting the scheduled load.
    /// </summary>
    /// <remarks>
    /// This is the unrecognised-switch hazard wearing a recognised switch's clothes: both scope switches parse,
    /// so an <c>args.Any</c> check would find no <c>--handler-id</c>, take the default path, and run the
    /// population load.
    /// </remarks>
    /// <param name="scope">Which scope was passed alone.</param>
    [Theory]
    [InlineData("--current-record")]
    [InlineData("--every-version")]
    public void AScopeWithNoHandlerIsRefused(string scope)
    {
        LoaderArguments arguments = LoaderArguments.Parse([scope]);

        string problem = Assert.Single(arguments.Problems);

        Assert.Contains("only apply to --handler-id", problem, StringComparison.Ordinal);
        Assert.Contains("silently ignored", problem, StringComparison.Ordinal);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>The seeding run and the targeted run ask for opposite things, so neither is done.</summary>
    /// <remarks>
    /// Honouring one of the two would be worse than refusing both: the seeding run rewrites the credential
    /// file, and an operator who typed both cannot be assumed to have wanted that as well as the load.
    /// </remarks>
    [Fact]
    public void SeedOnlyAndAHandlerTogetherAreRefused()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--seed-only", "--handler-id", Handler, "--current-record"]);

        Assert.Contains(
            "opposite things", Assert.Single(arguments.Problems), StringComparison.Ordinal);

        Assert.Null(arguments.Targeted);
    }

    /// <summary>
    /// The request validates itself here, before a credential is proved or a run row is opened.
    /// </summary>
    /// <remarks>
    /// <b>The width case is the one that matters.</b> A clipped identifier names a <i>different</i> handler
    /// rather than none (G36), so it would produce a plausible run against the wrong site. Refused at the
    /// command line, the operator sees the value and its length.
    /// </remarks>
    /// <param name="handlerId">The identifier as typed.</param>
    /// <param name="expected">Text the refusal must carry.</param>
    [Theory]
    [InlineData("MDD0000000012345", "wider than the 12")]
    // Twelve characters, so this case provokes the space refusal alone -- a thirteen-character value with a
    // space in it is refused twice over, which would make the assertion below pass for the wrong reason.
    [InlineData("MDD 00000001", "contains a space")]
    public void AnIdentifierTheDatabaseCannotHoldIsRefusedAtTheCommandLine(
        string handlerId,
        string expected)
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--handler-id", handlerId, "--current-record"]);

        string problem = Assert.Single(arguments.Problems);

        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Contains(handlerId.Trim(), problem, StringComparison.Ordinal);
        Assert.Null(arguments.Targeted);
    }

    /// <summary>An identifier of only whitespace is the blank case, not the space case.</summary>
    /// <remarks>
    /// It reaches <c>TargetedLoadRequest.Validate</c> as a value rather than as an absent one — the parser saw
    /// a token that did not start with <c>--</c> — and a blank identifier on
    /// <c>/hd/sources/summaries</c> does not fail: it asks EPA for every handler it has.
    /// </remarks>
    [Fact]
    public void AWhitespaceIdentifierIsRefusedBecauseABlankOneAsksForEveryHandler()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--handler-id", "   ", "--current-record"]);

        Assert.Contains(
            "asks EPA for every handler", Assert.Single(arguments.Problems), StringComparison.Ordinal);

        Assert.Null(arguments.Targeted);
    }

    /// <summary>The usage text names every switch, since it is what a refusal prints.</summary>
    /// <remarks>
    /// A refusal that does not say what the acceptable arguments are leaves the operator guessing at 2am, and
    /// guessing here means possibly starting the population load.
    /// </remarks>
    [Fact]
    public void TheUsageTextNamesEverySwitchAndNoSecret()
    {
        Assert.Contains("--seed-only", LoaderArguments.Usage, StringComparison.Ordinal);
        Assert.Contains("--handler-id", LoaderArguments.Usage, StringComparison.Ordinal);
        Assert.Contains("--current-record", LoaderArguments.Usage, StringComparison.Ordinal);
        Assert.Contains("--every-version", LoaderArguments.Usage, StringComparison.Ordinal);
        Assert.Contains("--probe-summaries", LoaderArguments.Usage, StringComparison.Ordinal);
        Assert.Contains("--probe-source", LoaderArguments.Usage, StringComparison.Ordinal);

        // The scheduled load is a line of its own, because "no arguments" is the shape most invocations use
        // and the one an operator is least likely to think of as a documented option.
        Assert.Contains("config.LoadWatermark", LoaderArguments.Usage, StringComparison.Ordinal);

        // Both probes say so on their own line. The one thing an operator has to know before running either is
        // that it does not touch the database, and a usage line is where they will read it.
        Assert.Equal(
            2,
            LoaderArguments.Usage.Split("writes nothing", StringSplitOptions.None).Length - 1);
    }

    /// <summary>The probe takes two dates and produces a request, with no targeted load alongside it.</summary>
    [Fact]
    public void TheProbeSwitchTakesTwoDates()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-summaries", "2026-09-01", "2026-09-07"]);

        Assert.Empty(arguments.Problems);
        Assert.NotNull(arguments.Probe);
        Assert.Equal(new DateOnly(2026, 9, 1), arguments.Probe.FromDate);
        Assert.Equal(new DateOnly(2026, 9, 7), arguments.Probe.ToDate);

        // Neither of the other two modes, and not by accident: the probe is the one invocation that writes
        // nothing, so anything else set here would make it write something.
        Assert.Null(arguments.Targeted);
        Assert.False(arguments.SeedOnly);
    }

    /// <summary>The probe switch is recognised in whatever casing a Task Scheduler dialog produced.</summary>
    /// <param name="argument">The switch as typed.</param>
    [Theory]
    [InlineData("--probe-summaries")]
    [InlineData("--PROBE-SUMMARIES")]
    [InlineData("--Probe-Summaries")]
    public void TheProbeSwitchIsRecognisedWhateverTheCasing(string argument)
    {
        LoaderArguments arguments = LoaderArguments.Parse([argument, "2026-09-01", "2026-09-07"]);

        Assert.Empty(arguments.Problems);
        Assert.NotNull(arguments.Probe);
    }

    /// <summary>A missing second date is reported as a missing date, not parsed from the next switch.</summary>
    /// <remarks>
    /// The same rule <c>--handler-id --every-version</c> follows: a following argument that is itself a switch
    /// is left where it is, so the refusal points at the half that is actually wrong.
    /// </remarks>
    [Fact]
    public void ASwitchFollowingTheProbeIsNotTakenAsOneOfItsDates()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-summaries", "2026-09-01", "--seed-only"]);

        Assert.Null(arguments.Probe);

        // Two refusals, and both are true at once: the second date is missing, AND the two modes do not
        // combine. Reporting only one would send the operator back for a second attempt.
        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("second one is missing", StringComparison.Ordinal));

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("--seed-only", StringComparison.Ordinal)
                && problem.Contains("separate errands", StringComparison.Ordinal));
    }

    /// <summary>The probe and a targeted load are refused together, and neither is attempted.</summary>
    /// <remarks>
    /// <b>They are not merely redundant, they measure different things.</b> The probe reports on a date window
    /// across every handler in the state and writes nothing; <c>--handler-id</c> loads one handler and takes
    /// no dates. Running both from one invocation would produce two console blocks with nothing to say which
    /// of them touched the database.
    /// </remarks>
    [Fact]
    public void TheProbeAndATargetedLoadAreRefusedTogether()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-summaries", "2026-09-01", "2026-09-07", "--handler-id", Handler, "--current-record"]);

        Assert.Null(arguments.Probe);
        Assert.Null(arguments.Targeted);

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("different questions", StringComparison.Ordinal));
    }

    /// <summary>An inverted window is refused at the command line, before a credential is proved.</summary>
    /// <remarks>
    /// Refused here rather than by the probe itself so that it is exit code 9 with the switch named — the
    /// operator's argument list — instead of being reported as unusable configuration, which sends them to
    /// <c>appsettings.json</c> for a value that is fine.
    /// </remarks>
    [Fact]
    public void AnInvertedProbeWindowIsRefusedAtTheCommandLine()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-summaries", "2026-09-07", "2026-09-01"]);

        Assert.Null(arguments.Probe);
        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("empty array", StringComparison.Ordinal));
    }

    /// <summary>The probe switch with no dates at all is refused twice, once per end.</summary>
    [Fact]
    public void TheProbeSwitchAloneIsRefused()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--probe-summaries"]);

        Assert.Null(arguments.Probe);
        Assert.Equal(2, arguments.Problems.Count);
    }

    /// <summary>
    /// The record-detail probe takes one handler, in both the spaced and the equals form, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>It takes a handler and not a version triple on purpose.</b> An operator cannot supply a valid
    /// <c>(sourceType, sequence)</c> pair — sequences are scoped per source type and EPA's own numbering has gaps
    /// in 3.4% of lineages — and a wrong pair answers <c>404</c>, which is the one shape this codebase reads as a
    /// withdrawn record. So the probe asks the summaries feed which versions exist and then fetches one.
    /// </remarks>
    /// <param name="first">The switch, spaced or joined.</param>
    /// <param name="second">The value, where the switch did not carry it.</param>
    [Theory]
    [InlineData("--probe-source", Handler)]
    [InlineData("--probe-source=" + Handler, null)]
    [InlineData("--PROBE-SOURCE", Handler)]
    [InlineData("--Probe-Source=" + Handler, null)]
    public void TheRecordDetailProbeTakesOneHandler(string first, string? second)
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            second is null ? [first] : [first, second]);

        Assert.Empty(arguments.Problems);
        Assert.NotNull(arguments.SourceProbe);
        Assert.Equal(Handler, arguments.SourceProbe.Normalized());

        // None of the three modes that write anything, and not by accident: this is the other invocation that
        // touches nothing, so anything else set here would make it write something.
        Assert.Null(arguments.Targeted);
        Assert.Null(arguments.Probe);
        Assert.False(arguments.SeedOnly);
    }

    /// <summary>A switch following the record-detail probe is not taken as its handler.</summary>
    /// <remarks>
    /// The rule <c>--handler-id</c> and <c>--probe-summaries</c> both follow: a following argument that is itself
    /// a switch is left where it is, so the refusal points at the half that is actually wrong. Both refusals are
    /// true at once here and both are reported, so the operator does not fix one and discover the other.
    /// </remarks>
    [Fact]
    public void ASwitchFollowingTheRecordDetailProbeIsNotTakenAsItsHandler()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--probe-source", "--seed-only"]);

        Assert.Null(arguments.SourceProbe);

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("no default", StringComparison.Ordinal));

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("--seed-only", StringComparison.Ordinal)
                && problem.Contains("separate errands", StringComparison.Ordinal));
    }

    /// <summary>The record-detail probe with no handler at all is refused, and says there is no default.</summary>
    /// <remarks>
    /// The refusal matters more here than the shape of it: a blank <c>handlerId</c> on
    /// <c>/hd/sources/summaries</c> does not fail — it asks EPA for every handler it has — so a defaulted or
    /// omitted value would send a whole-state request in place of a two-request diagnostic.
    /// </remarks>
    [Fact]
    public void TheRecordDetailProbeWithNoHandlerIsRefused()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--probe-source"]);

        Assert.Null(arguments.SourceProbe);

        string problem = Assert.Single(arguments.Problems);

        Assert.Contains("--probe-source", problem, StringComparison.Ordinal);
        Assert.Contains("no default", problem, StringComparison.Ordinal);
    }

    /// <summary>An identifier the database could not hold is refused at the command line.</summary>
    /// <remarks>
    /// Refused here rather than by the stage so that it is exit code 9 with the switch named — the operator's
    /// argument list — instead of being reported as unusable configuration, which sends them to
    /// <c>appsettings.json</c> for a value that is fine.
    /// </remarks>
    [Fact]
    public void AnOverWideHandlerIsRefusedAtTheCommandLineForTheRecordDetailProbe()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--probe-source", "MD05700240001234"]);

        Assert.Null(arguments.SourceProbe);

        string problem = Assert.Single(arguments.Problems);

        Assert.Contains("MD05700240001234", problem, StringComparison.Ordinal);
        Assert.Contains("404", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record-detail probe and the seeding run are refused together, and neither is done.
    /// </summary>
    /// <remarks>
    /// The seeding run rewrites the credential file, and the probe needs a proved credential — so the order
    /// matters and combining them hides which of the two failed.
    /// </remarks>
    [Fact]
    public void TheRecordDetailProbeAndSeedOnlyAreRefusedTogether()
    {
        LoaderArguments arguments = LoaderArguments.Parse(["--probe-source", Handler, "--seed-only"]);

        Assert.Null(arguments.SourceProbe);

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("--probe-source", StringComparison.Ordinal)
                && problem.Contains("separate errands", StringComparison.Ordinal));
    }

    /// <summary>
    /// The record-detail probe and a targeted load are refused together, because one writes and one does not.
    /// </summary>
    /// <remarks>
    /// <b>These two are the pair most likely to be typed together, and that is exactly why.</b> Both name a
    /// handler, so the command line looks redundant rather than contradictory — but <c>--probe-source</c> reports
    /// EPA's raw date text and writes nothing, while <c>--handler-id</c> loads the handler into the database.
    /// Running both would produce one console block for a mode that measured and another for a mode that wrote,
    /// with nothing distinguishing them. <b>A diagnostic must not be able to look like a load.</b>
    /// </remarks>
    [Fact]
    public void TheRecordDetailProbeAndATargetedLoadAreRefusedTogether()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-source", Handler, "--handler-id", Handler, "--every-version"]);

        Assert.Null(arguments.SourceProbe);
        Assert.Null(arguments.Targeted);

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("writes NOTHING", StringComparison.Ordinal));
    }

    /// <summary>The two probes are refused together, because they measure two different feeds.</summary>
    /// <remarks>
    /// Both write nothing, so this refusal is not about safety — it is about the output. Interleaved, the two
    /// reports arrive with nothing saying which feed each line came from, and telling the two feeds apart is the
    /// one distinction <c>--probe-source</c> exists to draw.
    /// </remarks>
    [Fact]
    public void TheTwoProbesAreRefusedTogether()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-source", Handler, "--probe-summaries", "2026-09-01", "2026-09-07"]);

        Assert.Null(arguments.SourceProbe);
        Assert.Null(arguments.Probe);

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("two different feeds", StringComparison.Ordinal));
    }

    /// <summary>
    /// A bad identifier and a bad combination are both reported from one invocation.
    /// </summary>
    /// <remarks>
    /// The request is built even when a combination refusal has already fired, for the reason the summaries
    /// window is parsed anyway: an operator who got both wrong should not fix one, re-run, and discover the
    /// other.
    /// </remarks>
    [Fact]
    public void ABadIdentifierAndABadCombinationAreBothReported()
    {
        LoaderArguments arguments = LoaderArguments.Parse(
            ["--probe-source", "MD05700240001234", "--seed-only"]);

        Assert.Null(arguments.SourceProbe);
        Assert.Equal(2, arguments.Problems.Count);

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("MD05700240001234", StringComparison.Ordinal));

        Assert.Contains(
            arguments.Problems,
            problem => problem.Contains("separate errands", StringComparison.Ordinal));
    }
}
