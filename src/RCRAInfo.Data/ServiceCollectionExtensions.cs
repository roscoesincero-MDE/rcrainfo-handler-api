using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RCRAInfo.Data;

/// <summary>Registers <see cref="RCRAInfoContext"/> and its options.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data layer, reading its options from the
    /// <c>RCRAInfoData</c> configuration section.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">The application's configuration root.</param>
    /// <returns>The container, for chaining.</returns>
    /// <remarks>
    /// The connection string comes from configuration but is <b>not</b> in <c>appsettings.json</c>:
    /// <c>.gitignore</c> excludes that file and its per-environment variants for exactly this reason,
    /// and the password reaches the process through the DPAPI-protected credential file (AR4) or an
    /// environment variable. Nothing here reads a password, and nothing here writes one anywhere.
    /// </remarks>
    public static IServiceCollection AddRCRAInfoData (
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull (services);
        ArgumentNullException.ThrowIfNull (configuration);

        services.AddOptions<RCRAInfoDataOptions> ()
            .Bind (configuration.GetSection (RCRAInfoDataOptions.SectionName))
            .ValidateDataAnnotations ()
            .ValidateOnStart ();

        return AddContext (services);
    }

    /// <summary>
    /// Registers the data layer with options set in code rather than read from configuration.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Sets the options.</param>
    /// <returns>The container, for chaining.</returns>
    /// <remarks>
    /// For tests and for a host that has already resolved its connection string by other means. The
    /// same validation applies — an overload that skipped it would be the one path by which an
    /// out-of-range timeout reached the driver.
    /// </remarks>
    public static IServiceCollection AddRCRAInfoData (
        this IServiceCollection services,
        Action<RCRAInfoDataOptions> configure)
    {
        ArgumentNullException.ThrowIfNull (services);
        ArgumentNullException.ThrowIfNull (configure);

        services.AddOptions<RCRAInfoDataOptions> ()
            .Configure (configure)
            .ValidateDataAnnotations ()
            .ValidateOnStart ();

        return AddContext (services);
    }

    private static IServiceCollection AddContext (IServiceCollection services)
    {
        services.AddDbContext<RCRAInfoContext> ((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<RCRAInfoDataOptions>> ().Value;

            builder.UseSqlServer (options.ConnectionString, sqlServer =>
            {
                // The target is SQL Server 2022 while the developer workstation runs 2025. Compatibility
                // level 160 is not a feature gate on either side -- it does not stop 2025-only syntax
                // compiling -- but it does stop EF Core emitting SQL that only a later engine accepts,
                // which is a class of defect that would otherwise appear first in UAT.
                sqlServer.UseCompatibilityLevel (160);

                // Retry is safe here only because of a property of the database, not a property of the
                // driver: every procedure this context calls is idempotent by construction. Script 520
                // SETs AttemptCount from the payload rather than incrementing it, the merges are MERGEs,
                // and logs.uspStartLoadRun refuses a second concurrent run. Take any of those away and
                // this line becomes a way to double-count.
                //
                // It also depends on nothing in this assembly opening a transaction [R12]: EF Core
                // throws when a retrying execution strategy meets an explicit transaction, and it throws
                // on the first retry rather than at startup -- which is to say in UAT.
                sqlServer.EnableRetryOnFailure (
                    options.MaxRetryCount,
                    TimeSpan.FromSeconds (options.MaxRetryDelaySeconds),
                    errorNumbersToAdd: null);

                // A default, overridden per call. Every method in the context sets its own, because a
                // paged read and a 500-element merge want limits an order of magnitude apart.
                sqlServer.CommandTimeout (options.CommandTimeoutSeconds);
            });
        });

        return services;
    }
}
