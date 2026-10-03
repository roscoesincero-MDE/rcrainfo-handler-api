using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>The journal's configuration, and the registration that validates it before a run starts.</summary>
public class LoadJournalOptionsTests
{
    [Fact]
    public void TheDefaultsAreThePlansStartingPoint()
    {
        // 100 rows or 30 seconds, from plan D2, which also says to tune them against F2's measured numbers
        // rather than guessing -- and F2 needs a credential, so they are still a starting point.
        LoadJournalOptions options = new();

        Assert.Equal(100, options.FlushRowCount);
        Assert.Equal(TimeSpan.FromSeconds(30), options.FlushInterval);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARowCountBelowOneIsRefused(int rowCount)
    {
        LoadJournalOptions options = new() { FlushRowCount = rowCount };

        Assert.Contains("FlushRowCount", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroIntervalIsRefusedRatherThanTakenLiterally()
    {
        // Zero would mean a flush per row: one round-trip per handler to the same table the set-based
        // procedures exist to avoid, arrived at by a configuration value rather than by a decision.
        LoadJournalOptions options = new() { FlushInterval = TimeSpan.Zero };

        Assert.Contains("FlushInterval", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void BothProblemsAreReportedTogether()
    {
        LoadJournalOptions options = new() { FlushRowCount = 0, FlushInterval = TimeSpan.FromSeconds(-1) };

        Assert.Equal(2, options.Validate().Count);
    }

    [Fact]
    public void TheSectionBindsFromConfiguration()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<ILoadJournalWriter>(new RecordingJournalWriter());
        services.AddRcraInfoLoadJournal(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RCRAInfoLoad:FlushRowCount"] = "250",
                    ["RCRAInfoLoad:FlushInterval"] = "00:00:05",
                })
                .Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        LoadJournalOptions options = provider.GetRequiredService<IOptions<LoadJournalOptions>>().Value;

        Assert.Equal(250, options.FlushRowCount);
        Assert.Equal(TimeSpan.FromSeconds(5), options.FlushInterval);
    }

    [Fact]
    public void BadConfigurationFailsAtStartupAndNotOnTheFirstFlush()
    {
        // Same reason AddRcraInfoApi validates on start: a scheduled task that fails after two hours of
        // fetching, when it tries to write what it fetched, has done the expensive half and kept none of it.
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<ILoadJournalWriter>(new RecordingJournalWriter());
        services.AddRcraInfoLoadJournal(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RCRAInfoLoad:FlushRowCount"] = "0",
                })
                .Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<LoadJournalOptions>>().Value);
    }

    [Fact]
    public void TheFactoryResolvesFromTheContainerAndProducesAJournal()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<ILoadJournalWriter>(new RecordingJournalWriter());
        services.AddRcraInfoLoadJournal(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        ILoadJournalFactory factory = scope.ServiceProvider.GetRequiredService<ILoadJournalFactory>();

        Assert.NotNull(factory.Create(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TheFactoryRefusesARunIdentifierNoRunCouldHave(int loadRunId)
    {
        // logs.uspStartLoadRun mints the identifier and it is an IDENTITY, so it is never zero. A journal
        // built with zero would buffer rows for a run that does not exist, and script 524 would report every
        // one of them as orphaned -- which reads as a loader defect somewhere else entirely.
        LoadJournalFactory factory = new(
            new RecordingJournalWriter(),
            Options.Create(new LoadJournalOptions()),
            TimeProvider.System,
            NullLogger<LoadJournal>.Instance);

        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(loadRunId));
    }

    [Fact]
    public void TheWriterTakesItsElementLimitFromTheDataOptionsRatherThanASecondSetting()
    {
        // One number, in the place that enforces it: above RCRAInfoDataOptions.MaxPayloadElements,
        // PayloadJson.Serialize throws. A copy in LoadJournalOptions would be two numbers that have to
        // agree, and the failure when they stop agreeing is a thrown flush.
        RCRAInfoContextJournalWriter writer = new(
            null!,
            Options.Create(new RCRAInfoDataOptions { MaxPayloadElements = 250 }));

        Assert.Equal(250, writer.MaxElementsPerCall);
    }

    [Fact]
    public void AHandlerVersionConvertsToTheSoftDeletePayloadElement()
    {
        HandlerVersion version = new("MDD000000001", "N", 3);

        HandlerKeyElement element = version.ToKeyElement();

        Assert.Equal("MDD000000001", element.HandlerId);
        Assert.Equal("N", element.SourceType);
        Assert.Equal(3, element.Sequence);
        Assert.Equal("MDD000000001/N/3", version.ToString());
    }

    [Fact]
    public void TheFlushSummaryHoldsCountsAndNothingElse()
    {
        // It goes into a log message, so it may hold no identifier, no path and no payload (AR8).
        LoadJournalFlush flush = new(1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal(10, flush.StatusRows);
        Assert.Equal(15, flush.TotalRows);
        Assert.True(flush.HasDefects);

        string summary = flush.ToString();

        Assert.Equal(
            "enumerated=1 attempted=2 failed=3 skipped=4 attempts=5 calls=8 orphaned=6 withheld=7",
            summary);
    }

    [Fact]
    public void FlushesAddUp()
    {
        LoadJournalFlush total = LoadJournalFlush.Empty
            .Add(new LoadJournalFlush(1, 1, 1, 1, 1, 0, 0, 5))
            .Add(new LoadJournalFlush(2, 0, 0, 0, 3, 1, 0, 2));

        Assert.Equal(3, total.Enumerated);
        Assert.Equal(4, total.AttemptRows);
        Assert.Equal(1, total.RowsOrphaned);
        Assert.Equal(7, total.Calls);
        Assert.True(total.HasDefects);
        Assert.False(LoadJournalFlush.Empty.HasDefects);
    }
}
