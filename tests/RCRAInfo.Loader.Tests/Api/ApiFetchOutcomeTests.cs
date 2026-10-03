using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The four projections off <see cref="ApiFetchOutcome"/>, asserted to be total and to land inside the two
/// CHECK domains they feed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Totality is a compile-time guarantee and is still asserted here, because the two guarantees are
/// different.</b> <c>CS8509</c> makes an unhandled named value an error under this solution's
/// <c>TreatWarningsAsErrors</c> — but <c>CS8524</c> is suppressed in
/// <see cref="ApiFetchOutcomeExtensions"/>, and a future edit that adds a <c>_</c> arm to silence a build
/// complaint would silence <c>CS8509</c> permanently and leave a new outcome quietly classified as
/// non-retryable, non-fatal, and <c>Failed</c>. Iterating <see cref="Enum.GetValues{TEnum}"/> keeps a
/// second, independent check on that: the value has to be reached, and the string it produces has to be
/// one the database accepts.
/// </para>
/// <para>
/// The two string sets below are the <b>third</b> copy of two CHECK constraints — the DDL has one, the
/// projections have another. That is deliberate duplication in a test: its job is to disagree with the
/// code when the code changes without the constraint changing.
/// <c>AttemptLogTests</c> closes the loop against the real database.
/// </para>
/// </remarks>
public class ApiFetchOutcomeTests
{
    /// <summary>From <c>CK_logs_HandlerLoadAttempt_Outcome</c>.</summary>
    private static readonly string[] AttemptOutcomes =
        ["Succeeded", "Failed", "Throttled", "TimedOut", "Cancelled"];

    /// <summary>From <c>CK_logs_HandlerLoadStatus_Status</c>.</summary>
    private static readonly string[] StatusValues =
        ["Pending", "InProgress", "Succeeded", "Failed", "Skipped"];

    public static TheoryData<ApiFetchOutcome> AllOutcomes()
    {
        TheoryData<ApiFetchOutcome> data = [];

        foreach (ApiFetchOutcome outcome in Enum.GetValues<ApiFetchOutcome>())
        {
            data.Add(outcome);
        }

        return data;
    }

    // ---- Totality ----------------------------------------------------------------------------------

    [Fact]
    public void ThereAreElevenOutcomesAndAddingOneShouldBringADecisionWithIt()
    {
        // A literal count, for the reason the other literal counts in this suite exist: it is the cheapest
        // thing that fails when someone adds a value, and the failure points at the four decisions below
        // rather than at whichever call site first meets the new value.
        Assert.Equal(11, Enum.GetValues<ApiFetchOutcome>().Length);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryOutcomeIsClassifiedByAllFourProjections(ApiFetchOutcome outcome)
    {
        // No projection may throw. With CS8524 suppressed, an unmatched value would be a runtime
        // MatchFailureException on a nightly load rather than a build error.
        _ = outcome.IsRetryable();
        _ = outcome.IsFatalToTheRun();

        Assert.NotNull(outcome.ToAttemptOutcome());
        Assert.NotNull(outcome.ToStatus());
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryProjectedAttemptOutcomeIsInsideTheCheckConstraint(ApiFetchOutcome outcome)
    {
        // logs.uspRecordHandlerLoadAttemptSet refuses anything outside this set rather than letting the
        // constraint reject the row with an engine error naming no element -- so a value out of range here
        // costs a whole batch of attempt rows, silently, at the end of a load.
        Assert.Contains(outcome.ToAttemptOutcome(), AttemptOutcomes);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryProjectedStatusIsInsideTheCheckConstraint(ApiFetchOutcome outcome)
    {
        Assert.Contains(outcome.ToStatus(), StatusValues);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void NoFinishedAttemptProjectsToPendingOrInProgress(ApiFetchOutcome outcome)
    {
        // Those two describe work not yet finished: Pending is written when a run enumerates its work and
        // InProgress while a fetch is in flight. A finished attempt reporting either would make a resume
        // re-fetch it forever, or never.
        Assert.NotEqual("Pending", outcome.ToStatus());
        Assert.NotEqual("InProgress", outcome.ToStatus());
    }

    // ---- Retryability ------------------------------------------------------------------------------

    [Theory]
    [InlineData(ApiFetchOutcome.Throttled)]
    [InlineData(ApiFetchOutcome.ServiceFailure)]
    [InlineData(ApiFetchOutcome.Unreachable)]
    [InlineData(ApiFetchOutcome.TimedOut)]
    public void TheFourTransientOutcomesAreRetryable(ApiFetchOutcome outcome) =>
        Assert.True(outcome.IsRetryable());

    [Theory]
    [InlineData(ApiFetchOutcome.Succeeded)]
    [InlineData(ApiFetchOutcome.NotFound)]
    [InlineData(ApiFetchOutcome.BadRequest)]
    [InlineData(ApiFetchOutcome.Unauthorized)]
    [InlineData(ApiFetchOutcome.AccessDenied)]
    [InlineData(ApiFetchOutcome.Cancelled)]
    [InlineData(ApiFetchOutcome.Unexpected)]
    public void TheOtherSevenAreNot(ApiFetchOutcome outcome) =>
        Assert.False(outcome.IsRetryable());

    [Fact]
    public void UnauthorizedIsNotRetryableEvenThoughATokenProblemSoundsLikeTheMostRetryableThingHere()
    {
        // ApiTokenHandler has already retried once with a fresh token by the time this value exists -- that
        // is its single-retry contract. A second attempt from the caller repeats the refresh that just
        // failed, and the real remedy is re-seeding the credential.
        Assert.False(ApiFetchOutcome.Unauthorized.IsRetryable());
    }

    [Fact]
    public void UnexpectedIsNotRetryableBecauseAnUnparseableSuccessParsesNoBetterTwice()
    {
        Assert.False(ApiFetchOutcome.Unexpected.IsRetryable());
    }

    [Fact]
    public void NotFoundIsNotRetryableBecauseItIsAnAnswer()
    {
        Assert.False(ApiFetchOutcome.NotFound.IsRetryable());
    }

    // ---- Fatality ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(ApiFetchOutcome.BadRequest)]
    [InlineData(ApiFetchOutcome.Unauthorized)]
    [InlineData(ApiFetchOutcome.AccessDenied)]
    public void TheThreeSystemicRefusalsEndTheRun(ApiFetchOutcome outcome) =>
        Assert.True(outcome.IsFatalToTheRun());

    [Theory]
    [InlineData(ApiFetchOutcome.Succeeded)]
    [InlineData(ApiFetchOutcome.NotFound)]
    [InlineData(ApiFetchOutcome.Throttled)]
    [InlineData(ApiFetchOutcome.ServiceFailure)]
    [InlineData(ApiFetchOutcome.Unreachable)]
    [InlineData(ApiFetchOutcome.TimedOut)]
    [InlineData(ApiFetchOutcome.Cancelled)]
    [InlineData(ApiFetchOutcome.Unexpected)]
    public void EverythingElseIsAPerHandlerConditionTheRunCanSurvive(ApiFetchOutcome outcome) =>
        Assert.False(outcome.IsFatalToTheRun());

    [Fact]
    public void FatalAndRetryableAreDifferentQuestionsAndTheProofIsTwoOutcomes()
    {
        // NotFound: not retryable, not fatal -- ordinary. AccessDenied: not retryable, fatal. Collapsing
        // the two questions into one would either retry a permissions failure across four hundred thousand
        // handlers or abandon a run because one record was absent.
        Assert.False(ApiFetchOutcome.NotFound.IsRetryable());
        Assert.False(ApiFetchOutcome.NotFound.IsFatalToTheRun());

        Assert.False(ApiFetchOutcome.AccessDenied.IsRetryable());
        Assert.True(ApiFetchOutcome.AccessDenied.IsFatalToTheRun());
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void NothingIsBothRetryableAndFatal(ApiFetchOutcome outcome)
    {
        // The two are not opposites, but they are exclusive: an outcome worth trying again cannot also be
        // the reason to stop, and an orchestrator asking both questions of the same value must not get yes
        // twice.
        Assert.False(outcome.IsRetryable() && outcome.IsFatalToTheRun());
    }

    [Fact]
    public void SuccessIsNeitherRetryableNorFatal()
    {
        Assert.False(ApiFetchOutcome.Succeeded.IsRetryable());
        Assert.False(ApiFetchOutcome.Succeeded.IsFatalToTheRun());
    }

    // ---- The attempt-log projection ----------------------------------------------------------------

    [Theory]
    [InlineData(ApiFetchOutcome.Succeeded, "Succeeded")]
    [InlineData(ApiFetchOutcome.Throttled, "Throttled")]
    [InlineData(ApiFetchOutcome.TimedOut, "TimedOut")]
    [InlineData(ApiFetchOutcome.Cancelled, "Cancelled")]
    public void FourOutcomesHaveTheirOwnValueInTheAttemptLog(
        ApiFetchOutcome outcome,
        string expected) =>
        Assert.Equal(expected, outcome.ToAttemptOutcome());

    [Theory]
    [InlineData(ApiFetchOutcome.NotFound)]
    [InlineData(ApiFetchOutcome.BadRequest)]
    [InlineData(ApiFetchOutcome.Unauthorized)]
    [InlineData(ApiFetchOutcome.AccessDenied)]
    [InlineData(ApiFetchOutcome.ServiceFailure)]
    [InlineData(ApiFetchOutcome.Unreachable)]
    [InlineData(ApiFetchOutcome.Unexpected)]
    public void TheOtherSevenCollapseToFailed(ApiFetchOutcome outcome) =>
        Assert.Equal("Failed", outcome.ToAttemptOutcome());

    [Fact]
    public void ThrottledKeepsItsOwnValueSoAThrottledThenSucceededHandlerDoesNotReadAsClean()
    {
        Assert.Equal("Throttled", ApiFetchOutcome.Throttled.ToAttemptOutcome());
        Assert.NotEqual("Failed", ApiFetchOutcome.Throttled.ToAttemptOutcome());
    }

    [Fact]
    public void TheAttemptLogProjectionUsesAllFiveOfTheConstraintsValues()
    {
        // Not one value unreachable: if any of the five were never produced, either the enum is missing a
        // branch or the constraint is carrying a value nothing writes -- and both are worth knowing.
        string[] produced = [.. Enum.GetValues<ApiFetchOutcome>()
            .Select(o => o.ToAttemptOutcome())
            .Distinct()];

        Assert.Equal(5, produced.Length);

        foreach (string value in AttemptOutcomes)
        {
            Assert.Contains(value, produced);
        }
    }

    // ---- The status projection ---------------------------------------------------------------------

    [Fact]
    public void CancelledIsSkippedAndNotFailed()
    {
        // The one judgement in ToStatus. A version the run never reached because the process stopped was
        // not attempted and did not fail; Failed would make an orderly shutdown indistinguishable from EPA
        // refusing us, and would put a row in front of an operator who has nothing to fix.
        Assert.Equal("Skipped", ApiFetchOutcome.Cancelled.ToStatus());
    }

    [Fact]
    public void NotFoundIsSucceededBecauseTheColumnRecordsWhetherTheRunDealtWithTheVersion()
    {
        // Reads wrong until the grain is right. A 404 is a complete, usable answer that feeds the AR7 soft
        // delete; Failed would make a resume re-fetch it forever, since the record will still be absent
        // tomorrow. The distinction stays visible in logs.HandlerLoadStatus.Outcome = 'SoftDeleted'.
        Assert.Equal("Succeeded", ApiFetchOutcome.NotFound.ToStatus());
        Assert.Equal("Succeeded", ApiFetchOutcome.Succeeded.ToStatus());
    }

    [Fact]
    public void NotFoundIsSucceededAsAStatusAndFailedAsAnAttempt()
    {
        // The one outcome where the two projections disagree, and the disagreement is the design: the
        // attempt row records what the call did (a 404 is not a fetch), the status row records whether the
        // version still needs work (it does not).
        Assert.Equal("Failed", ApiFetchOutcome.NotFound.ToAttemptOutcome());
        Assert.Equal("Succeeded", ApiFetchOutcome.NotFound.ToStatus());
    }

    [Theory]
    [InlineData(ApiFetchOutcome.BadRequest)]
    [InlineData(ApiFetchOutcome.Unauthorized)]
    [InlineData(ApiFetchOutcome.AccessDenied)]
    [InlineData(ApiFetchOutcome.Throttled)]
    [InlineData(ApiFetchOutcome.ServiceFailure)]
    [InlineData(ApiFetchOutcome.Unreachable)]
    [InlineData(ApiFetchOutcome.TimedOut)]
    [InlineData(ApiFetchOutcome.Unexpected)]
    public void EverythingElseLeavesTheVersionFailedAndThereforeEligibleForTheNextRun(
        ApiFetchOutcome outcome) =>
        Assert.Equal("Failed", outcome.ToStatus());

    [Fact]
    public void ThrottledIsSucceededNowhereEvenThoughItIsRetryable()
    {
        // Retryable describes what the caller may do next; ToStatus describes the version once every retry
        // is spent. A Throttled that survived to this projection is a version that never arrived.
        Assert.True(ApiFetchOutcome.Throttled.IsRetryable());
        Assert.Equal("Failed", ApiFetchOutcome.Throttled.ToStatus());
    }

    [Fact]
    public void TheStatusProjectionProducesExactlyThreeOfTheFiveValues()
    {
        string[] produced = [.. Enum.GetValues<ApiFetchOutcome>()
            .Select(o => o.ToStatus())
            .Distinct()
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["Failed", "Skipped", "Succeeded"], produced);
    }
}
