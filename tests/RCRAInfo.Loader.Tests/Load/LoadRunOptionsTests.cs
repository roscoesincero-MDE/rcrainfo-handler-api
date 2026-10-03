using System.Globalization;

using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The one setting that decides which jurisdiction this database is.
/// </summary>
public class LoadRunOptionsTests
{
    [Fact]
    public void ThereIsNoDefaultBecauseADefaultWouldMeanNobodyEverStatesIt()
    {
        // A default that silently works is comfortable and wrong here: the value selects the handlers
        // dbo.uspReconcileCurrentRecord reconciles and the jurisdiction the five scoped code lists are
        // fetched for. Absent, the run must fail at startup naming the setting.
        LoadRunOptions options = new();

        Assert.Equal(string.Empty, options.ActivityLocation);

        string problem = Assert.Single(options.Validate());

        Assert.Contains("RCRAInfoLoad:ActivityLocation", problem, StringComparison.Ordinal);
        Assert.Contains("G2", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSectionIsTheSameOneTheJournalBinds()
    {
        // Two classes over one section, deliberately: an operator tuning a load reads one block, and the
        // flush interval and the state in scope are the same kind of decision made by the same person.
        Assert.Equal(LoadJournalOptions.SectionName, LoadRunOptions.SectionName);
        Assert.Equal("RCRAInfoLoad", LoadRunOptions.SectionName);
    }

    [Theory]
    [InlineData("MD")]
    [InlineData("md")]
    [InlineData(" MD ")]
    public void TwoAsciiLettersAreUsableWhateverTheCasingOrPadding(string location)
    {
        LoadRunOptions options = new() { ActivityLocation = location };

        Assert.Empty(options.Validate());
        Assert.Equal("MD", options.NormalizedActivityLocation());
    }

    [Theory]
    [InlineData("M")]
    [InlineData("MDX")]
    [InlineData("M1")]
    [InlineData("24")]
    public void AnythingElseIsRefusedWithItsOwnLengthInTheMessage(string location)
    {
        LoadRunOptions options = new() { ActivityLocation = location };

        string problem = Assert.Single(options.Validate());

        Assert.Contains("two ASCII letters", problem, StringComparison.Ordinal);
        Assert.Contains(location, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStringFormCarriesTheSettingAndNoSecretBecauseThereIsNoneHere()
    {
        Assert.Equal(
            "RCRAInfoLoad { ActivityLocation = MD, WindowDays = 7, FetchBatchSize = 25, "
            + "AbandonAfterMinutes = 720, ResumeMaxAgeHours = 48, InitialLoadFromDate = (not set) }",
            new LoadRunOptions { ActivityLocation = "MD" }.ToString());
    }

    /// <summary>
    /// An absent <c>InitialLoadFromDate</c> prints as words, for the reason the age limit does.
    /// </summary>
    /// <remarks>
    /// <b>This is the setting whose absence stops a full load</b>, so a start-up line reading
    /// "InitialLoadFromDate = " — indistinguishable from a truncated message — would be hiding the one value
    /// an operator has to supply before the initial load can run at all.
    /// </remarks>
    [Fact]
    public void AnAbsentInitialLoadFromDatePrintsAsWordsRatherThanAsAnEmptyGap()
    {
        Assert.Contains(
            "InitialLoadFromDate = (not set)",
            new LoadRunOptions { ActivityLocation = "MD", InitialLoadFromDate = null }.ToString(),
            StringComparison.Ordinal);

        Assert.Contains(
            "InitialLoadFromDate = 1980-01-01",
            new LoadRunOptions
            {
                ActivityLocation = "MD",
                InitialLoadFromDate = new DateOnly(1980, 1, 1),
            }.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent <c>InitialLoadFromDate</c> is legal at start-up, and is refused only by the run that needs it.
    /// </summary>
    /// <remarks>
    /// <b>Validating it here would stop a perfectly ordinary incremental run over a setting it never reads.</b>
    /// It is required for a full load and unused for every other, so <c>LoadRun.Refuse</c> names it on the one
    /// run that needs it — see <c>LoadRunTests</c>.
    /// </remarks>
    [Fact]
    public void AnAbsentInitialLoadFromDateIsNotAStartUpProblem()
    {
        Assert.Empty(
            new LoadRunOptions { ActivityLocation = "MD", InitialLoadFromDate = null }.Validate());
    }

    /// <summary>
    /// A date before 1900 is refused, because that is the shape a mistyped year has.
    /// </summary>
    /// <remarks>
    /// <b>The floor is not a claim about RCRA.</b> <c>0001-01-01</c> — the default <c>DateOnly</c>, and what a
    /// digit dropped from a year produces — is about 105,000 windows at the default width, which presents as a
    /// load that never finishes rather than as an error. The message names the recommended value so the
    /// operator has somewhere to go.
    /// </remarks>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(198, 1, 1)]
    [InlineData(1899, 12, 31)]
    public void ADateBeforeTheFloorIsRefusedAndTheMessageNamesTheRecommendation(
        int year,
        int month,
        int day)
    {
        LoadRunOptions options = new()
        {
            ActivityLocation = "MD",
            InitialLoadFromDate = new DateOnly(year, month, day),
        };

        string problem = Assert.Single(options.Validate());

        Assert.Contains(nameof(LoadRunOptions.InitialLoadFromDate), problem, StringComparison.Ordinal);
        Assert.Contains("1900-01-01", problem, StringComparison.Ordinal);
        Assert.Contains(
            LoadRunOptions.RecommendedInitialLoadFromDate, problem, StringComparison.Ordinal);
    }

    /// <summary>The recommended value is on or after the floor, which is the one way it could be wrong.</summary>
    /// <remarks>
    /// A refusal message that names a value the same validator would refuse sends an operator in a circle, and
    /// nothing else in the solution would notice.
    /// </remarks>
    [Fact]
    public void TheRecommendedInitialLoadFromDateIsItselfAcceptable()
    {
        DateOnly recommended = DateOnly.Parse(
            LoadRunOptions.RecommendedInitialLoadFromDate, CultureInfo.InvariantCulture);

        Assert.True(recommended >= LoadRunOptions.MinInitialLoadFromDate);

        Assert.Empty(
            new LoadRunOptions
            {
                ActivityLocation = "MD",
                InitialLoadFromDate = recommended,
            }.Validate());
    }

    [Fact]
    public void AnAbsentAgeLimitPrintsAsWordsRatherThanAsAnEmptyGap()
    {
        // A start-up line reading "ResumeMaxAgeHours = " is indistinguishable from a truncated message,
        // and the setting it would be hiding is the one that decides whether a weeks-old run's successes
        // are trusted.
        Assert.Contains(
            "ResumeMaxAgeHours = (no limit)",
            new LoadRunOptions { ActivityLocation = "MD", ResumeMaxAgeHours = null }.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoResumeSettingsDefaultToTheSameNumbersTheProceduresDo()
    {
        // AbandonAfterMinutes is passed to BOTH logs.uspStartLoadRun and
        // logs.uspGetHandlerLoadResumeSet, which have no way to check each other -- so the defaults
        // matching is what makes an unconfigured deployment agree with itself.
        LoadRunOptions options = new() { ActivityLocation = "MD" };

        Assert.Equal(720, options.AbandonAfterMinutes);
        Assert.Equal(48, options.ResumeMaxAgeHours);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(14)]
    [InlineData(0)]
    [InlineData(-720)]
    public void AnAbandonmentThresholdBelowTheProceduresFloorIsRefused(int minutes)
    {
        // Both procedures enforce 15 minutes. Repeated here so the refusal names the SETTING rather than
        // a parameter, and so it happens before the run opens a connection: below the floor, the resume
        // read would offer a healthy in-flight run's versions to a second run as already loaded.
        LoadRunOptions options = new() { ActivityLocation = "MD", AbandonAfterMinutes = minutes };

        string problem = Assert.Single(options.Validate());

        Assert.Contains("AbandonAfterMinutes", problem, StringComparison.Ordinal);
        Assert.Contains("15", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAgeLimitIsLegalBecauseItIsHowALongInitialLoadIsReDrivenByHand()
    {
        Assert.Empty(
            new LoadRunOptions { ActivityLocation = "MD", ResumeMaxAgeHours = null }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnAgeLimitOfZeroOrLessIsRefusedBecauseItSilentlyDisablesResume(int hours)
    {
        // The dangerous shape: it rejects every candidate, which returns an empty resume set, which reads
        // as "nothing to resume" -- so the run re-fetches the whole population and reports success. Null
        // is the way to say "no limit"; zero is a mistake.
        LoadRunOptions options = new() { ActivityLocation = "MD", ResumeMaxAgeHours = hours };

        string problem = Assert.Single(options.Validate());

        Assert.Contains("ResumeMaxAgeHours", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultWindowIsAWeekAndAWeekIsDeliberatelyNarrow()
    {
        // The only lever on how large a summaries response gets, because /hd/sources/summaries has no paging
        // and no envelope -- there is no way to tell a complete window from a truncated one, so the defence is
        // a window narrow enough that truncation is implausible.
        LoadRunOptions options = new() { ActivityLocation = "MD" };

        Assert.Equal(7, options.WindowDays);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(367)]
    public void AWindowOutsideOneToAYearIsRefused(int windowDays)
    {
        // A year is the point past which "walk the population in slices" has stopped being what the code
        // does, and zero or negative would produce either no windows or an infinite loop -- and no windows is
        // the dangerous one, because every window succeeds when there are none.
        LoadRunOptions options = new() { ActivityLocation = "MD", WindowDays = windowDays };

        Assert.Contains("WindowDays", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }
}
