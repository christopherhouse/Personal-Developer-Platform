using JasperFx;
using Npgsql;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace Pdp.ControlPlane.Verbs;

/// <summary>
/// Configures Wolverine for the control plane: a durable transactional inbox/outbox on the platform
/// Postgres, EF Core transactional middleware, EF Core saga storage (the <c>EnvironmentSaga</c> via
/// <c>RegistryDbContext</c>), and durable local queues (research §2/§6). The host
/// (<c>Pdp.Cli</c> / <c>Pdp.ControlPlane.Api</c>) calls this from inside <c>UseWolverine(...)</c>; the
/// matching service registrations (DbContexts with Wolverine integration) are wired by
/// <see cref="ServiceCollectionExtensions.AddControlPlaneVerbs"/>.
/// </summary>
public static class WolverineConfiguration
{
    /// <summary>The dedicated schema for Wolverine's message-store tables (separate from ipam/registry).</summary>
    public const string MessageSchema = "wolverine";

    /// <summary>
    /// Applies the control plane's durability and saga configuration to <paramref name="options"/>,
    /// persisting messages over a plain connection string (the CLI / test path — trusted local Postgres).
    /// </summary>
    /// <param name="options">The Wolverine options being configured (inside <c>UseWolverine</c>).</param>
    /// <param name="postgresConnectionString">The platform Postgres connection (shared server).</param>
    /// <param name="runScheduledAgents">
    /// Whether this node runs the background durability/scheduled-message agents (the polling reconciler's
    /// recurring sweeps). The always-on Api node keeps them on; a scale-to-zero node (the spec-007 MCP
    /// server) passes <see langword="false"/> so two nodes never double-process the sweep — research §13.
    /// </param>
    public static WolverineOptions ConfigureControlPlaneMessaging(
        this WolverineOptions options,
        string postgresConnectionString,
        bool runScheduledAgents = true)
    {
        // Durable transactional inbox/outbox, isolated in its own schema on the shared server so a
        // committed allocate+record transaction and its dispatch are all-or-nothing (research §6).
        options.PersistMessagesWithPostgresql(postgresConnectionString, MessageSchema);
        return ConfigureCommon(options, runScheduledAgents);
    }

    /// <summary>
    /// Same configuration over a pre-built <see cref="NpgsqlDataSource"/> — used by the in-VNet hosts that
    /// authenticate to the private, Entra-only Postgres with a managed-identity token rather than a
    /// password (<see cref="Hosting.EntraPostgres.CreateDataSource"/>). Wolverine and the EF Core
    /// DbContexts then share the one token-backed source.
    /// </summary>
    /// <param name="options">The Wolverine options being configured (inside <c>UseWolverine</c>).</param>
    /// <param name="dataSource">The Entra-token data source (no password).</param>
    /// <param name="runScheduledAgents">See the string overload — <see langword="false"/> on the MCP node.</param>
    public static WolverineOptions ConfigureControlPlaneMessaging(
        this WolverineOptions options,
        NpgsqlDataSource dataSource,
        bool runScheduledAgents = true)
    {
        options.PersistMessagesWithPostgresql(dataSource);
        // The data-source overload takes no schema argument; pin the envelope-storage schema explicitly so
        // the message store still lives in `wolverine` (not `public`), matching the connection-string path.
        options.Durability.MessageStorageSchemaName = MessageSchema;
        return ConfigureCommon(options, runScheduledAgents);
    }

    /// <summary>The durability/saga/discovery configuration shared by both persistence overloads.</summary>
    private static WolverineOptions ConfigureCommon(WolverineOptions options, bool runScheduledAgents)
    {
        // EF Core transactional middleware + EF Core saga support. Wolverine auto-detects the
        // RegistryDbContext that maps EnvironmentSaga (research §2).
        options.UseEntityFrameworkCoreTransactions();

        // The reconciler's scheduled messages and the dispatch/track flow survive restarts.
        options.Policies.UseDurableLocalQueues();

        // The Api is single-node by design (one owner, one host — Article-level "no SaaS"; spec 007
        // deploys ONE always-on Api container). Solo mode skips the leadership election / node-assignment
        // dance, so the inbox/outbox start immediately and recover faster after an ungraceful shutdown.
        // It also removes the multi-host node-coordination contention that flares when the test suite
        // starts and stops many hosts back-to-back against one shared Postgres (the CI flake). The MCP
        // node (runScheduledAgents=false) instead runs Serverless: it keeps the durable transactional
        // outbox (a dispatch enqueued there is still durable and sent immediately) but turns OFF the
        // background durability/scheduled agents, so the reconciler's recurring sweep runs ONLY on the
        // Api node — never double-processed across the two nodes that share this Postgres (research §13).
        options.Durability.Mode = runScheduledAgents ? DurabilityMode.Solo : DurabilityMode.Serverless;

        // Wolverine 6 unbundled the Roslyn code generator; compile handler glue at startup (GH-2876).
        options.UseRuntimeCompilation();

        // Handlers/sagas live across three assemblies (registry saga, verb-layer dispatch/release
        // handlers, dispatch-layer reconciler), none of which is the host's entry assembly — include
        // them explicitly so Wolverine discovers them.
        options.Discovery.IncludeAssembly(typeof(EnvironmentSaga).Assembly);
        options.Discovery.IncludeAssembly(typeof(WolverineConfiguration).Assembly);
        options.Discovery.IncludeAssembly(typeof(RunReconciler).Assembly);

        return options;
    }
}
