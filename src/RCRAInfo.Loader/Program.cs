using System.Runtime.Versioning;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Core.Credentials;
using RCRAInfo.Data;
using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Credentials;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader;

/// <summary>
/// Entry point for the console application (AR1): retrieves EPA RCRAInfo Handler data and writes it
/// to the RCRAInfo database. Run unattended by Windows Task Scheduler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two things happen here, in this order, and the first is allowed to end the process.</b> The AR4
/// credential bootstrap runs before there is a host, because the credential file may need sealing and
/// the SQL password may be wrong — and both of those are exit codes rather than start-up exceptions
/// (plan §4.2). Only once the credentials are proved is the container built and
/// <see cref="ILoadRun"/> asked for one load.
/// </para>
/// <para>
/// <b><c>--seed-only</c> exists because the seeding procedure predates the pipeline.</b> Step 3 of
/// plan §4.2 is "run the application once under its service identity", which is what turns the
/// plaintext file the operator typed into a sealed one; steps 4 and 5 confirm the file now reads
/// <c>Encrypted: true</c> and that a second run works from the ciphertext alone. Now that a run also
/// loads several hundred thousand handler versions, performing those three steps without the switch
/// would start an initial load as a side effect of checking a file.
/// </para>
/// <para>
/// <b><c>--handler-id</c> is the diagnostic, and it needs a scope.</b> <c>--handler-id &lt;id&gt;
/// --current-record</c> fetches the version EPA marks as its current record; <c>--every-version</c> fetches
/// the handler's entire history. It exists because an operator has to be able to ask what EPA holds for one
/// site without waiting for the nightly load or disturbing it, so the run it opens reads no watermark, moves
/// none, refreshes no code list and resumes from nothing — see <see cref="ILoadRun.RunTargetedAsync"/>.
/// <see cref="LoaderArguments"/> refuses an unrecognised switch for a related reason: every switch here asks
/// for <i>less</i> than the default, so a mistyped one would otherwise be ignored and start the full load.
/// </para>
/// <para>
/// <b><c>--probe-summaries &lt;from&gt; &lt;to&gt;</c> is the only mode that writes nothing at all.</b> It asks
/// the summaries feed for one window and reports the row count, the response size, the handlers that carry
/// more than one version, and EPA's date fields as literally sent. Three questions the plan could not answer
/// otherwise: the feed takes no <c>offset</c> and no <c>limit</c>, so the window is the only lever on response
/// size and F2 cannot size a slice without measuring one; <c>--every-version</c> proves nothing on a handler
/// that has one version, so a second pass needs a handler that has several; and <b>no column retains a raw
/// body</b>, so a date format not written down at the moment of the call cannot be recovered afterwards
/// (G25). Because it opens no run row it also works on a database that has never loaded anything, which is
/// exactly when the sizing question is asked.
/// </para>
/// <para>
/// <b>The exit codes are the interface</b>, because Task Scheduler reads them and nobody reads the
/// console at 2am. <b>Zero means a completed load and nothing else</b> — not a sealed credential file,
/// not a disabled feed, not a partial run — because a scheduled task that reports success while loading
/// nothing is the failure mode this project would least like to ship, and it is invisible for as long as
/// nobody checks the row counts. Every other code distinguishes one operator action, which is the same
/// rule <see cref="CredentialBootstrapOutcome"/> follows.
/// </para>
/// </remarks>
// Windows-only, and stated rather than assumed. DPAPI is Windows, the NTFS deny-read ACE that is the
// actual control on the credential file is Windows, and Task Scheduler is Windows -- the deployment
// target has never been in question. Saying so here is what lets CA1416 keep checking the rest of the
// tree instead of being suppressed at each call.
[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>The load completed with every stage doing what it was asked.</summary>
    /// <remarks>
    /// The one path that returns zero, and it requires <see cref="LoadRunOutcome.Succeeded"/> —
    /// which in turn requires that every window was walked, every version accounted for and every code
    /// list refreshed. See <see cref="LoadRun"/>.
    /// </remarks>
    private const int ExitSucceeded = 0;

    /// <summary>
    /// <c>--seed-only</c> was passed: the credentials were sealed or opened and no load was attempted.
    /// </summary>
    /// <remarks>
    /// Deliberately non-zero, and deliberately not the same code as a credential failure. During seeding
    /// this is the SUCCESS case — the operator wants to see it, confirm the file now reads
    /// <c>Encrypted: true</c>, and run again. Non-zero because a Task Scheduler action that acquired
    /// <c>--seed-only</c> by accident would otherwise report a successful nightly load forever.
    /// </remarks>
    private const int ExitSeededOnly = 2;

    /// <summary>The AR4 bootstrap failed. One of the G5 rows; the message names which and what to do.</summary>
    private const int ExitCredentialFailure = 3;

    /// <summary>No connection-string template, so the SQL password could not have been validated.</summary>
    /// <remarks>
    /// Separate from <see cref="ExitCredentialFailure"/> because nothing was tried and nothing was
    /// written. Reporting a missing configuration value as a credential failure sends an operator to the
    /// password manager, and the password is fine.
    /// </remarks>
    private const int ExitConfigurationMissing = 4;

    /// <summary>Real work landed and something did not. <c>logs.LoadRun</c> says which.</summary>
    /// <remarks>
    /// Distinct from <see cref="ExitLoadFailed"/> because the operator action differs: a partial run needs
    /// somebody to read <c>logs.HandlerLoadStatus</c> for the versions that did not land, and the next
    /// scheduled run will resume them on its own. A failed run needs somebody tonight.
    /// </remarks>
    private const int ExitLoadPartial = 5;

    /// <summary>The load stopped early. The run row records why.</summary>
    private const int ExitLoadFailed = 6;

    /// <summary>Shutdown or Ctrl-C. Everything buffered was still flushed and the run was still closed.</summary>
    private const int ExitLoadCancelled = 7;

    /// <summary>
    /// The feed is switched off in <c>config.LoadWatermark</c>, or a run is already live, or the feed has no
    /// usable watermark row. No run was opened and nothing was fetched.
    /// </summary>
    /// <remarks>
    /// One code for the three, because the operator action is the same in all three cases — look at
    /// <c>config.LoadWatermark</c> and <c>logs.LoadRun</c> — and the console line names which. None of them
    /// is a failure of a load, and all three would read as one if they shared
    /// <see cref="ExitLoadFailed"/>.
    /// </remarks>
    private const int ExitNoRunOpened = 8;

    /// <summary>The command line could not be used. Nothing was attempted and nothing was written.</summary>
    /// <remarks>
    /// Its own code rather than <see cref="ExitConfigurationMissing"/>, because the operator action is
    /// different in a way that matters at 2am: a configuration problem sends somebody to
    /// <c>appsettings.json</c> or to the Task Scheduler action's environment, and this sends them to the
    /// action's argument list. Reached before the credential bootstrap, so a bad argument cannot have
    /// rewritten the credential file.
    /// </remarks>
    private const int ExitBadArguments = 9;

    /// <summary>
    /// <c>--probe-summaries</c> answered. The window was reported on and nothing was loaded.
    /// </summary>
    /// <remarks>
    /// Non-zero for <see cref="ExitSeededOnly"/>'s reason, and it is the stronger case of the two: a Task
    /// Scheduler action that acquired this switch by accident would report a healthy nightly load forever
    /// while the mirror stood still, and unlike a seeding run this one does not even write a
    /// <c>logs.LoadRun</c> row for anybody to notice the absence of.
    /// </remarks>
    private const int ExitProbed = 10;

    /// <summary>
    /// <c>--probe-summaries</c> could not be answered. EPA refused the window or sent a body that could not
    /// be read.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ExitProbed"/> because the whole point of a probe is the number it returns, and
    /// a probe that came back empty has to be distinguishable from one that came back with an answer by
    /// something a script can read. Distinct from <see cref="ExitLoadFailed"/> because no run was opened, so
    /// there is no run row to send anybody to.
    /// </remarks>
    private const int ExitProbeFailed = 11;

    private static async Task<int> Main(string[] args)
    {
        // Parsed before anything else runs, including the credential bootstrap -- which rewrites the
        // credential file in place, and must not be a side effect of a command line that will be refused.
        LoaderArguments arguments = LoaderArguments.Parse(args);

        if (arguments.Problems.Count > 0)
        {
            foreach (string problem in arguments.Problems)
            {
                Console.Error.WriteLine(problem);
            }

            Console.Error.WriteLine(LoaderArguments.Usage);

            return ExitBadArguments;
        }

        bool seedOnly = arguments.SeedOnly;

        // Environment variables last so they win, which is how a Task Scheduler action supplies the
        // connection string on a machine with no configuration file. appsettings.json is optional and
        // gitignored; it holds the server and database and NO password -- the password is the one thing
        // this file exists to keep out of configuration.
        //
        // The credential file is deliberately absent from this chain. It is rewritten in place on the
        // first run, while an exclusive lock is held on it, and a JSON configuration source added with
        // reloadOnChange watches the file it was built from -- so the application would recycle itself
        // in the middle of its own startup. build/check_credential_template.py enforces that.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        string? template = configuration[$"{RCRAInfoDataOptions.SectionName}:ConnectionString"];

        if (string.IsNullOrWhiteSpace(template))
        {
            Console.Error.WriteLine(
                $"No {RCRAInfoDataOptions.SectionName}:ConnectionString is configured, so the SQL "
                + "password could not be validated and nothing has been sealed. Supply a connection "
                + "string naming the server, the database and User ID=RCRAInfoLoader, and NO password "
                + "-- either in appsettings.json beside the executable or in the environment variable "
                + $"{RCRAInfoDataOptions.SectionName}__ConnectionString.");

            return ExitConfigurationMissing;
        }

        SqlCredentialValidator sql;

        try
        {
            sql = new SqlCredentialValidator(template);
        }
        catch (ArgumentException error)
        {
            // The three refusals -- a password already in the template, integrated security, no User ID
            // -- are all configuration defects, and all three would otherwise produce a validator that
            // reports every password as valid. The message names the defect and carries no value.
            Console.Error.WriteLine(
                $"The configured {RCRAInfoDataOptions.SectionName}:ConnectionString cannot be used to "
                + $"validate a password: {error.Message}");

            return ExitConfigurationMissing;
        }

        RcraInfoApiOptions api =
            configuration.GetSection(RcraInfoApiOptions.SectionName).Get<RcraInfoApiOptions>()
            ?? new RcraInfoApiOptions();

        IReadOnlyList<string> apiProblems = api.Validate();

        if (apiProblems.Count > 0)
        {
            // Same code as a missing connection string, and for the same reason: nothing was tried and
            // nothing was written. A missing base address is not a bad credential, and reporting it as one
            // sends an operator to the password manager to fix a configuration file.
            foreach (string problem in apiProblems)
            {
                Console.Error.WriteLine(problem);
            }

            return ExitConfigurationMissing;
        }

        using ApiCredentialValidator epa = ApiCredentialValidator.Create(api);

        // Order matters and is asserted by CredentialValidators.All's own remarks: the SQL login first, so
        // that an unreachable SQL Server is never reported as a problem with an API Key nobody tried. Then
        // the offline shape check, which costs nothing and names the character position of a bad paste. Then
        // EPA, which is the only thing that can actually prove the pair -- and the only one that needs the
        // network.
        CredentialValidator validate = CredentialValidators.All(
            sql.Delegate,
            ApiCredentialShapeValidator.Delegate,
            epa.Delegate);

        // Named on every run rather than only on failure. Which environment a credential was proved against
        // is the fact an operator needs when the same key stops working after a promotion, and the base
        // address is the only place that distinction is recorded. It is not a secret; the credential is.
        Console.Out.WriteLine(
            $"The RCRAInfo API credential will be proved by calling {api.BaseAddress} -- plan §4.3. The "
            + "credentials themselves are never shown or logged: EPA carries both halves in the request "
            + "path of that call.");

        CredentialBootstrapper bootstrapper = new(new DpapiSecretProtector(ApplicationIdentity.Loader));

        CredentialBootstrapResult result = await bootstrapper.BootstrapAsync(
            Path.Combine(AppContext.BaseDirectory, CredentialFile.DefaultFileName),
            validate);

        if (!result.Succeeded)
        {
            // The message names the file, the outcome and the next action, and is asserted by test to
            // carry no credential on any path -- including the paths where the value that failed IS the
            // password.
            Console.Error.WriteLine($"{result.Outcome}: {result.Message}");

            return ExitCredentialFailure;
        }

        Console.Out.WriteLine(
            $"{result.Outcome}: {result.Message}"
            + (result.LockWaits > 0
                ? $" The credential file was held by another process {result.LockWaits} time(s) before "
                  + "it opened, which is expected when both applications first run on one machine."
                : string.Empty));

        if (seedOnly)
        {
            // Nothing is retrieved and nothing is written, and this says so and exits non-zero rather than
            // logging a cheerful line.
            Console.Error.WriteLine(
                "--seed-only: the credential bootstrap is complete and no load was attempted. Nothing has "
                + "been retrieved from EPA and nothing has been written to the database. If this run was "
                + $"step 3 of the seeding procedure, confirm {CredentialFile.DefaultFileName} now reads "
                + "\"Encrypted\": true, then run once more with --seed-only to confirm the ciphertext "
                + "alone works.");

            return ExitSeededOnly;
        }

        return await LoadAsync(
                configuration,
                template,
                result.Require(),
                arguments.Targeted,
                arguments.Probe,
                arguments.SourceProbe)
            .ConfigureAwait(false);
    }

    /// <summary>Builds the container and runs one load.</summary>
    /// <param name="configuration">The configuration built above, reused rather than rebuilt.</param>
    /// <param name="template">The connection string with no password in it.</param>
    /// <param name="credentials">The proved credentials.</param>
    /// <param name="targeted">
    /// The single-handler request, or <see langword="null"/> for the scheduled load. The only thing it changes
    /// below is which of <see cref="ILoadRun"/>'s two methods is called — the container, the logging and the
    /// cancellation handling are identical, because they are not what distinguishes the two runs.
    /// </param>
    /// <param name="probe">
    /// The summaries window to report on, or <see langword="null"/>. When set, <see cref="ILoadRun"/> is never
    /// resolved at all: the probe is not a run and must not be able to look like one. It shares this method
    /// only for the container and the Ctrl-C handling, both of which it wants for the same reasons a load does.
    /// </param>
    /// <param name="sourceProbe">
    /// The handler whose record detail to report on, or <see langword="null"/>. Treated exactly as
    /// <paramref name="probe"/> is, and for the same reason — it answers the open half of G25 and writes
    /// nothing.
    /// </param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// <para>
    /// <b>A plain <c>ServiceCollection</c> rather than a host, because there is nothing to host.</b> This is
    /// one method call that ends, not a service that waits — and a host would add a lifetime, a shutdown
    /// timeout and a second configuration chain reading <c>appsettings.json</c> from the <i>working</i>
    /// directory, which for a Task Scheduler action is not the directory the executable is in. The cost is
    /// that <c>ValidateOnStart</c> does not fire without a host, which is why <see cref="LoadRun"/> validates
    /// its own options and why <see cref="OptionsValidationException"/> is caught below.
    /// </para>
    /// <para>
    /// <b>Ctrl-C is handled rather than allowed to kill the process</b>, because the orchestrator's whole
    /// cancellation path — flush the journal, close the run row — needs the process to still be alive to
    /// perform it. The default behaviour terminates it, which leaves <c>logs.LoadRun</c> at <c>Running</c>
    /// and loses the buffered status rows.
    /// </para>
    /// </remarks>
    private static async Task<int> LoadAsync(
        IConfiguration configuration,
        string template,
        ApplicationCredentials credentials,
        TargetedLoadRequest? targeted,
        SummaryProbeRequest? probe,
        SourceProbeRequest? sourceProbe)
    {
        // The one place the SQL password is put back into the connection string. SqlConnectionStringBuilder
        // rather than concatenation, so a password containing a semicolon or a quote is escaped instead of
        // truncating the string into something that happens to parse.
        SqlConnectionStringBuilder connection = new(template)
        {
            Password = credentials.SqlPassword,
        };

        ServiceCollection services = new();

        services.AddSingleton(configuration);
        services.AddLogging(logging => logging
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));

        // Registered before AddRcraInfoApi, which documents this ordering as its own precondition.
        services.AddSingleton(credentials);

        services.AddRCRAInfoData(configuration);

        // PostConfigure rather than the Action<RCRAInfoDataOptions> overload, so every other setting in the
        // RCRAInfo section -- the command timeout, the payload element limit -- is still bound from
        // configuration. This replaces one property and leaves the rest alone.
        services.PostConfigure<RCRAInfoDataOptions>(
            options => options.ConnectionString = connection.ConnectionString);

        services.AddRcraInfoApi(configuration);
        services.AddRcraInfoLookupRefresh(configuration);
        services.AddRcraInfoLoadJournal(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        using CancellationTokenSource cancellation = new();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Cancel = true keeps the process alive so the orchestrator can flush and close the run. A
            // second Ctrl-C is not intercepted: an operator who insists gets the abrupt exit, and the next
            // run sweeps the row to 'Abandoned'.
            eventArgs.Cancel = true;
            Console.Error.WriteLine("Cancellation requested. Flushing and closing the run before exiting.");

            cancellation.Cancel();
        };

        if (probe is not null)
        {
            return await ProbeAsync(provider, probe, cancellation.Token).ConfigureAwait(false);
        }

        if (sourceProbe is not null)
        {
            return await ProbeSourceAsync(provider, sourceProbe, cancellation.Token).ConfigureAwait(false);
        }

        if (targeted is not null)
        {
            // Said before the run rather than only in its summary, and it names the handler and the scope --
            // neither of which is a secret. This is the one invocation whose exit code 0 does NOT mean the
            // mirror is up to date, so an unattended log that shows a healthy nightly load has to also show
            // that the load was one site.
            Console.Out.WriteLine(
                $"--handler-id {targeted.Normalized()} --{(targeted.Scope == TargetedVersionScope.EveryVersion
                    ? "every-version" : "current-record")}: a Targeted run for ONE handler. No watermark is "
                + "read and none will be moved, no code list is refreshed, and nothing is skipped on the "
                + "strength of an earlier run. This is not a scheduled load and does not stand in for one.");
        }

        LoadRunResult run;

        try
        {
            // A scope because RCRAInfoContext and every seam over it is scoped, and validateScopes: true
            // above turns resolving one from the root provider into an exception rather than into a context
            // that outlives the run.
            using IServiceScope scope = provider.CreateScope();

            ILoadRun loader = scope.ServiceProvider.GetRequiredService<ILoadRun>();

            run = await (targeted is null
                    ? loader.RunAsync(cancellation.Token)
                    : loader.RunTargetedAsync(targeted, cancellation.Token))
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is OptionsValidationException or InvalidOperationException)
        {
            // Unusable configuration, which ILoadRun documents as the one thing it throws for. Reported as
            // a configuration problem rather than a load failure, because nothing was attempted -- the same
            // distinction the missing connection string above makes.
            Console.Error.WriteLine(
                $"The load did not start because the configuration is not usable: {error.Message}");

            return ExitConfigurationMissing;
        }

        // The run's own summary, which contains counts, a run number and a date, and no credential and no
        // URI -- see LoadRunResult.ToString.
        Console.Out.WriteLine(run.ToString());

        if (run.FailureMessage is not null)
        {
            Console.Error.WriteLine(run.FailureMessage);
        }

        return run.Outcome switch
        {
            LoadRunOutcome.Succeeded => ExitSucceeded,
            LoadRunOutcome.PartiallySucceeded => ExitLoadPartial,
            LoadRunOutcome.Cancelled => ExitLoadCancelled,
            LoadRunOutcome.FeedDisabled
                or LoadRunOutcome.AlreadyRunning
                or LoadRunOutcome.NotConfigured => ExitNoRunOpened,
            _ => ExitLoadFailed,
        };
    }

    /// <summary>Asks EPA about one summaries window and prints what came back.</summary>
    /// <param name="provider">The container built above, reused rather than built a second time.</param>
    /// <param name="probe">The window, already validated by <see cref="LoaderArguments"/>.</param>
    /// <param name="cancellationToken">Ctrl-C, honoured for the single call.</param>
    /// <returns><see cref="ExitProbed"/>, or one of the failure codes.</returns>
    /// <remarks>
    /// <para>
    /// <b>Says out loud that it is not a load, before it runs and again in the report.</b> The banner is not
    /// courtesy: the console output of this mode looks a great deal like the console output of a load, and the
    /// difference — that the database was never touched — is invisible in it. An operator reading an
    /// unattended log has to be able to tell which of the two they are looking at without counting rows.
    /// </para>
    /// <para>
    /// <b>Reads no watermark and needs no <c>logs.LoadRun</c> row, so it works on a database with none.</b>
    /// That matters for the question it exists to answer: [R28] wants to know what a window costs <i>before</i>
    /// the initial load is attempted, and the probe therefore has to be usable before the load path is.
    /// </para>
    /// </remarks>
    private static async Task<int> ProbeAsync(
        IServiceProvider provider,
        SummaryProbeRequest probe,
        CancellationToken cancellationToken)
    {
        Console.Out.WriteLine(
            $"--probe-summaries {probe}: a read-only measurement of ONE summaries window. Nothing is written "
            + "to the database -- no run row, no handler record, no watermark -- and nothing is loaded. Exit "
            + $"code {ExitProbed} means the window was reported on, which is not the same thing as a load and "
            + "does not stand in for one.");

        SummaryProbeReport report;

        try
        {
            // A scope for the reason the load takes one: the data client's token provider and everything under
            // it are resolved through the same container, and validateScopes: true refuses a scoped service
            // taken from the root.
            using IServiceScope scope = provider.CreateScope();

            SummaryProbe summaries = scope.ServiceProvider.GetRequiredService<SummaryProbe>();

            report = await summaries.ProbeAsync(probe.FromDate, probe.ToDate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is OptionsValidationException or InvalidOperationException)
        {
            // Same treatment the load gets, and for the same reason: an unusable RCRAInfoLoad section is a
            // configuration defect and nothing was attempted.
            Console.Error.WriteLine(
                $"The probe did not run because the configuration is not usable: {error.Message}");

            return ExitConfigurationMissing;
        }
        catch (OperationCanceledException)
        {
            // Nothing to flush and nothing to close -- that is the whole shape of this mode -- so cancellation
            // here is simply a probe that did not happen.
            Console.Error.WriteLine("Cancelled before the window was answered. Nothing was written.");

            return ExitLoadCancelled;
        }

        // Counts, sizes, durations, dates, handler identifiers and EPA's raw date text. No credential, no URI,
        // no query string and no contact field -- see SummaryProbeReport's remarks.
        Console.Out.WriteLine(report.ToString());

        return report.IsAnswered ? ExitProbed : ExitProbeFailed;
    }

    /// <summary>Reports EPA's raw date text for one handler's record detail, writing nothing.</summary>
    /// <param name="provider">The container built above.</param>
    /// <param name="request">The handler, already validated by the argument parser.</param>
    /// <param name="cancellationToken">Cancels either of the two calls.</param>
    /// <returns>
    /// <see cref="ExitProbed"/> when the record body was read and scanned, <see cref="ExitProbeFailed"/> when
    /// it was not.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>It reuses the summaries probe's two exit codes rather than inventing a pair.</b> Both modes answer
    /// the same question of a scheduler — "was the thing reported on, or not" — and the codes are already
    /// documented as belonging to a mode that measures instead of loading. A third and fourth code would make a
    /// Task Scheduler action distinguish two diagnostics from each other, which nothing needs to do.
    /// </para>
    /// <para>
    /// <b>Reads no watermark and needs no <c>logs.LoadRun</c> row</b>, so it works on a database with none —
    /// <see cref="ProbeAsync"/>'s reasoning, and here it matters for a second reason: the question is about
    /// EPA's wire format, so the answer must not depend on this side being deployed at all.
    /// </para>
    /// </remarks>
    private static async Task<int> ProbeSourceAsync(
        IServiceProvider provider,
        SourceProbeRequest request,
        CancellationToken cancellationToken)
    {
        Console.Out.WriteLine(
            $"--probe-source {request}: a read-only reading of ONE handler's version list and ONE of its "
            + "records, to see EPA's date fields exactly as EPA writes them (G25). Nothing is written to the "
            + "database -- no run row, no handler record, no watermark -- and nothing is loaded. Exit code "
            + $"{ExitProbed} means the record was reported on, which is not a load and does not stand in for "
            + "one. Two requests to EPA.");

        SourceProbeReport report;

        try
        {
            // A scope for the reason the load and the summaries probe take one: the data client's token
            // provider and everything under it are resolved through the same container, and
            // validateScopes: true refuses a scoped service taken from the root.
            using IServiceScope scope = provider.CreateScope();

            SourceProbe sources = scope.ServiceProvider.GetRequiredService<SourceProbe>();

            report = await sources.ProbeAsync(request.Normalized(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OptionsValidationException or InvalidOperationException)
        {
            // Same treatment the load and the summaries probe get: an unusable configuration is a defect on
            // this side and nothing was attempted.
            Console.Error.WriteLine(
                $"The record-detail probe did not run because the configuration is not usable: {error.Message}");

            return ExitConfigurationMissing;
        }
        catch (OperationCanceledException)
        {
            // Nothing to flush and nothing to close -- that is the whole shape of this mode -- so cancellation
            // here is simply a probe that did not happen.
            Console.Error.WriteLine("Cancelled before the record was read. Nothing was written.");

            return ExitLoadCancelled;
        }

        // Counts, sizes, durations, a handler identifier, version keys, JSON paths and EPA's raw date text. No
        // credential, no URI, no query string, no FailureMessage and no contact field -- see
        // SourceProbeReport's remarks and RawDateScan's selection rule.
        Console.Out.WriteLine(report.ToString());

        return report.IsAnswered ? ExitProbed : ExitProbeFailed;
    }
}
