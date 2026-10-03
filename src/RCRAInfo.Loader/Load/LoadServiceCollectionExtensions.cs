using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RCRAInfo.Loader.Load;

/// <summary>Registers the load stages: their options, their database seams, and the journal's factory.</summary>
public static class LoadServiceCollectionExtensions
{
    /// <summary>
    /// Adds the run's scope, the lookup refresh stage, the summaries walk, the summaries probe and the
    /// resume read.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Configuration holding the <c>RCRAInfoLoad</c> section.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The caller must have registered the data layer (<c>AddRCRAInfoData</c>) and the API clients
    /// (<c>AddRcraInfoApi</c>) already.
    /// </para>
    /// <para>
    /// <b><see cref="LoadRunOptions"/> is validated at startup and <see cref="LoadRunOptions.ActivityLocation"/>
    /// has no default</b>, so an absent or malformed value stops the process with the name of the setting
    /// rather than at the first request. For an unattended 2am job that is the difference between a
    /// configuration error and a missed window.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRcraInfoLookupRefresh(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LoadRunOptions>()
            .Bind(configuration.GetSection(LoadRunOptions.SectionName))
            .Validate(
                options => options.Validate().Count == 0,
                "The RCRAInfoLoad configuration section does not name a usable ActivityLocation.")
            .ValidateOnStart();

        // Scoped for the reason the journal's writer is: RCRAInfoContext is scoped.
        services.TryAddScoped<ILookupRefreshWriter, RCRAInfoContextLookupWriter>();
        services.TryAddScoped<ILookupRefresh, LookupRefresh>();

        // The walk touches no DbContext -- it reads EPA and returns a report, and it is the caller that
        // turns the versions into Enumerate rows. Scoped anyway, to match the stage it runs beside: a
        // singleton stage in a scoped pipeline is the registration that eventually captures something.
        services.TryAddScoped<ISummaryWalk, SummaryWalk>();

        // The probe, registered beside the walk because it asks the same feed the same way. Concrete rather
        // than behind an interface, and that is deliberate: there is nothing to substitute it for. It writes
        // nothing, so no test needs to fake a database out from under it, and no other stage depends on it --
        // Program.cs resolves it and prints what it returns. An interface here would be a seam with one
        // implementation and no caller that benefits.
        services.TryAddScoped<SummaryProbe>();

        // The record-detail probe, registered on SummaryProbe's reasoning in every respect: concrete, scoped,
        // writes nothing, resolved only by Program.cs. It closes the half of G25 the summaries probe could not
        // reach -- HandlerSource.createdDate and updatedDate as EPA writes them -- and it is the only
        // outstanding item on the project whose evidence decays, because no column retains a raw body.
        services.TryAddScoped<SourceProbe>();

        // The resume read. It needs a DbContext, so the seam and the stage are both scoped for the reason
        // the journal's writer is. TimeProvider is registered here as well as by AddRcraInfoLoadJournal
        // because LoadResume needs one for the candidate's age and neither method may assume the other
        // was called -- TryAddSingleton makes the duplicate a no-op.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ILoadResumeReader, RCRAInfoContextResumeReader>();
        services.TryAddScoped<ILoadResume, LoadResume>();

        // The orchestrator and its second database seam. A separate seam from ILoadJournalWriter on purpose
        // -- see ILoadRunWriter: AdvanceWatermarkAsync should not be in reach of every journal holder.
        services.TryAddScoped<ILoadRunWriter, RCRAInfoContextLoadRunWriter>();

        // The CurrentRecord reconciliation (§D4). Registered after the writer because it depends on it -- it
        // is the one stage that both asks EPA and writes, and both for the same reason: the fact it asserts
        // belongs to a handler's whole version list, so it needs the list EPA will only give per handler and a
        // procedure that can act on a lineage rather than on a version.
        services.TryAddScoped<ICurrentRecordReconcile, CurrentRecordReconcile>();
        services.TryAddScoped<ILoadRun, LoadRun>();

        return services;
    }

    /// <summary>Adds the buffered status and attempt journal.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Configuration holding the <c>RCRAInfoLoad</c> section.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The caller must have registered the data layer already — <c>AddRCRAInfoData</c> — because the
    /// journal's writer is an adapter over <c>RCRAInfoContext</c> and reads its element limit from
    /// <c>RCRAInfoDataOptions</c>.
    /// </para>
    /// <para>
    /// Validated at startup for the reason <c>AddRcraInfoApi</c> gives: an unattended run that fails on a
    /// configuration value at 2am has already burned the window it was given.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRcraInfoLoadJournal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LoadJournalOptions>()
            .Bind(configuration.GetSection(LoadJournalOptions.SectionName))
            .Validate(
                options => options.Validate().Count == 0,
                "The RCRAInfoLoad configuration section is not usable.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Scoped, because RCRAInfoContext is: AddDbContext registers scoped, and a singleton adapter over
        // a scoped context is the classic way to end up with one DbContext shared across a run's lifetime
        // and captured past its scope.
        services.TryAddScoped<ILoadJournalWriter, RCRAInfoContextJournalWriter>();
        services.TryAddScoped<ILoadJournalFactory, LoadJournalFactory>();

        return services;
    }
}
