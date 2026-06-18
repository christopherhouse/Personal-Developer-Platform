using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.ControlPlane.Verbs.Telemetry;
using Pdp.ControlPlane.Verbs.Validation;
using Wolverine.EntityFrameworkCore;

namespace Pdp.ControlPlane.Verbs;

/// <summary>
/// The single DI entry point for the control-plane verb layer (shared by the <c>pdp</c> CLI host and
/// the <c>Pdp.ControlPlane.Api</c> host). Wires persistence (IPAM + registry DbContexts with Wolverine
/// integration), the IPAM ledger, the GitHub App credential (Dispatch), the inventory read stack with
/// the control plane's injected <c>TokenCredential</c> (FR-013), and telemetry. The host pairs this
/// with <c>UseWolverine(opts =&gt; opts.ConfigureControlPlaneMessaging(connString))</c>
/// (<see cref="WolverineConfiguration"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the control-plane verb layer's services and binds its options from configuration.
    /// </summary>
    public static IServiceCollection AddControlPlaneVerbs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // --- Options binding --------------------------------------------------------------------
        services.Configure<ControlPlaneOptions>(configuration.GetSection(ControlPlaneOptions.SectionName));
        services.Configure<GitHubAppOptions>(configuration.GetSection(GitHubAppOptions.SectionName));

        var options = configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>()
                      ?? new ControlPlaneOptions();

        // Expose the bound ControlPlaneOptions as a resolvable value (the fabric verbs take it directly
        // for the platform-subscription natural key).
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ControlPlaneOptions>>().Value);

        // --- Persistence: IPAM + registry on the shared platform Postgres -----------------------
        // Wolverine integration registers each DbContext (Singleton options lifetime — a real perf
        // gain for Wolverine) and activates EF Core transactional middleware + saga support, so an
        // allocate (ipam) + record (registry) + dispatch enqueue commit atomically (research §6).
        services.AddDbContextWithWolverineIntegration<IpamDbContext>(
            db => db.UseNpgsql(options.PostgresConnectionString));
        services.AddDbContextWithWolverineIntegration<RegistryDbContext>(
            db => db.UseNpgsql(options.PostgresConnectionString));

        // --- IPAM ledger (Gate-G1; Article VI) --------------------------------------------------
        services.AddScoped<IIpamLedger, Ledger>();

        // --- Dispatch: the GitHub App credential (the only non-Azure secret — FR-020) -----------
        // Expose the bound options as a resolvable value too (the dispatcher/tracker take it directly).
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GitHubAppOptions>>().Value);
        services.AddSingleton<IGitHubAppCredential>(sp =>
            new GitHubAppCredential(
                sp.GetRequiredService<GitHubAppOptions>(),
                sp.GetService<TimeProvider>()));

        // The execution-plane boundary: dispatch (Octokit + Polly) and run tracking (correlation +
        // idempotent terminal record + self-healing reconcile sweep) — US1 (T030–T032).
        services.AddSingleton<IWorkflowDispatcher, GitHubWorkflowDispatcher>();
        services.AddScoped<IRunTracker, RunTracker>();
        services.AddSingleton<ReconcilerOptions>();

        // --- Registry + verbs ------------------------------------------------------------------
        services.AddScoped<IEnvironmentRegistry, EnvironmentRegistry>();
        services.AddScoped<ISpokeVerbs, SpokeVerbs>();
        services.AddScoped<IFabricVerbs, FabricVerbs>();
        services.AddScoped<IIpamVerbs, IpamVerbs>();
        services.AddScoped<IInventoryVerbs, InventoryVerbs>();
        services.AddScoped<IRunVerbs, RunVerbs>();
        services.AddSingleton<IValidator<SpokeCreateRequest>, SpokeCreateRequestValidator>();
        services.AddSingleton<IValidator<FabricCreateRequest>, FabricCreateRequestValidator>();

        // --- Inventory read stack: reuse spec-005 with the injected credential (FR-013) ---------
        // "What's deployed?" routes here (ARG), never to the registry (FR-016). The control plane
        // owns the TokenCredential; the inventory component never constructs one.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton(sp => new ArmClient(sp.GetRequiredService<TokenCredential>()));
        services.AddScoped<IResourceGraphReader, ResourceGraphReader>();
        services.AddScoped<IInventoryService, InventoryService>();

        // --- Telemetry: OTel sources/meters (+ Azure Monitor when configured) — FR-O1/SC-013 ----
        services.AddControlPlaneTelemetry(options);

        return services;
    }
}
