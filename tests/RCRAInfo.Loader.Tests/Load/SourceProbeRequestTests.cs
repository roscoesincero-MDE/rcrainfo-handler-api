using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The handler an operator asked the record-detail probe about, and the two ways of asking that would produce
/// something other than an answer about that handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>The blank case is the dangerous one, and it is dangerous rather than merely wrong.</b> A blank
/// <c>handlerId</c> on <c>/hd/sources/summaries</c> does not fail — it asks EPA for <i>every handler it has</i>.
/// So a probe that let an empty value through would send an unpaged whole-state request in place of a
/// two-request diagnostic, and most likely time out while looking like a network problem.
/// </para>
/// <para>
/// <b>The over-width case is refused rather than sent</b> for [R36]'s reason turned around: EPA answers an
/// identifier it does not hold with a <c>404</c>, and a <c>404</c> is the one shape this codebase reads as "EPA
/// withdrew this record". The probe writes nothing, so it could not act on that — but the output is what
/// somebody will reason about the loader from, and it must not be able to show a withdrawal that is really a
/// typo.
/// </para>
/// <para>
/// <b>What this type deliberately does not refuse is a version.</b> It takes a handler, because an operator
/// cannot supply a valid <c>(sourceType, sequence)</c> triple: sequences are scoped per source type and EPA's
/// own numbering has gaps in 3.4% of lineages. The probe asks the summaries feed which versions exist and then
/// fetches one — see <see cref="SourceProbeTests"/>.
/// </para>
/// </remarks>
public sealed class SourceProbeRequestTests
{
    /// <summary>The ordinary case: one identifier, accepted as it stands.</summary>
    [Fact]
    public void AHandlerIdentifierIsAccepted()
    {
        SourceProbeRequest request = new("MD0570024000");

        Assert.Empty(request.Validate());
        Assert.Equal("MD0570024000", request.Normalized());
    }

    /// <summary>The identifier is trimmed and upper-cased before it becomes a path segment.</summary>
    /// <remarks>
    /// Upper-cased for <c>TargetedLoadRequest.Normalized</c>'s reason: EPA's identifiers are upper case, the path
    /// segment is case-sensitive, and an operator pasting from an email may not be.
    /// </remarks>
    /// <param name="typed">The identifier as typed.</param>
    [Theory]
    [InlineData("md0570024000")]
    [InlineData("  MD0570024000  ")]
    [InlineData("\tMd0570024000\n")]
    public void TheIdentifierIsTrimmedAndUpperCased(string typed)
    {
        SourceProbeRequest request = new(typed);

        Assert.Empty(request.Validate());
        Assert.Equal("MD0570024000", request.Normalized());
    }

    /// <summary>
    /// A blank identifier is refused, because on the summaries feed it asks for every handler in the state.
    /// </summary>
    /// <param name="typed">The identifier as typed, or the absence of one.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void ABlankIdentifierIsRefusedAndTheRefusalSaysThereIsNoDefault(string? typed)
    {
        SourceProbeRequest request = new(typed!);

        string problem = Assert.Single(request.Validate());

        Assert.Contains("--probe-source", problem, StringComparison.Ordinal);
        Assert.Contains("no default", problem, StringComparison.Ordinal);
        Assert.Equal(string.Empty, request.Normalized());
    }

    /// <summary>
    /// An identifier the database could not hold is refused, at its stated width rather than near it.
    /// </summary>
    /// <remarks>
    /// Asserted as a pair at the boundary, because a cap tested only well inside or well outside it is a cap
    /// whose actual value nothing checks. The width is
    /// <see cref="SummaryPayload.MaxHandlerIdLength"/> rather than a literal, so the one place that number is
    /// stated governs the argument list too.
    /// </remarks>
    [Fact]
    public void TheWidthIsRefusedOnlyOnceItIsExceeded()
    {
        SourceProbeRequest widest = new(new string('M', SummaryPayload.MaxHandlerIdLength));
        SourceProbeRequest tooWide = new(new string('M', SummaryPayload.MaxHandlerIdLength + 1));

        Assert.Empty(widest.Validate());

        string problem = Assert.Single(tooWide.Validate());

        Assert.Contains("404", problem, StringComparison.Ordinal);
        Assert.Contains("withdrawn record", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The over-width refusal names the value and its length, because a clipped identifier names a different
    /// handler rather than none.
    /// </summary>
    [Fact]
    public void TheWidthRefusalNamesTheValueAndItsLength()
    {
        string problem = Assert.Single(new SourceProbeRequest("MD05700240001234").Validate());

        Assert.Contains("MD05700240001234", problem, StringComparison.Ordinal);
        Assert.Contains("16 characters", problem, StringComparison.Ordinal);
        Assert.Contains("at most 12", problem, StringComparison.Ordinal);
    }

    /// <summary>An identifier over width in the wrong casing is still reported in the normalised form.</summary>
    /// <remarks>
    /// The operator should see the value the probe would actually have sent, not the value they typed — the two
    /// differ, and a refusal that quoted the typed form would leave them comparing the wrong string against
    /// EPA's screen.
    /// </remarks>
    [Fact]
    public void TheRefusalQuotesTheValueTheProbeWouldHaveSent()
    {
        Assert.Contains(
            "MD05700240001234",
            Assert.Single(new SourceProbeRequest("  md05700240001234 ").Validate()),
            StringComparison.Ordinal);
    }

    /// <summary>The printed form is the normalised identifier and nothing else.</summary>
    /// <remarks>
    /// A handler identifier is not a secret — script 506 and the plan both say so — but nothing else about the
    /// request is safe to assume, so <c>ToString</c> carries exactly the one value it has.
    /// </remarks>
    [Fact]
    public void ThePrintedFormIsTheNormalisedIdentifierAndNothingElse()
    {
        Assert.Equal("MD0570024000", new SourceProbeRequest(" md0570024000 ").ToString());
    }
}
