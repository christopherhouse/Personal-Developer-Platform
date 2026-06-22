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

        // The reconciler's scheduled messages and the dispatch/track flow survive restarts. BOTH nodes need
        // these durable local queues: the verb's dispatch cascade (Begin*Provisioning ->
        // DispatchWorkflowCommand -> GitHub dispatch) is enrolled in the durable transactional outbox and
        // routed to `local://durable/`, so the node MUST be able to build that durable sender.
        options.Policies.UseDurableLocalQueues();

        // Solo durability on BOTH nodes. Solo skips the leadership-election / node-assignment dance, so the
        // inbox/outbox start immediately and recover faster after an ungraceful shutdown, and it removes the
        // multi-host coordination contention that flares when the test suite cycles many hosts against one
        // shared Postgres (the CI flake). The scale-to-zero MCP node CANNOT be Serverless: Serverless mode
        // DISABLES the transactional inbox/outbox, but the verb's dispatch cascade is enrolled in the durable
        // outbox and routed to `local://durable/` — Serverless can't build that sender, throwing
        // UnknownTransportException on the first dispatch (the live failure). The two nodes are kept off each
        // other's toes by REGISTRATION, not durability mode: only the Api registers ReconcilerScheduler (it
        // owns the recurring sweep), and the MCP node is scale-to-zero, so the windows where both run
        // recovery overlap are brief and the sweep is idempotent (research §13).
        options.Durability.Mode = DurabilityMode.Solo;

        // Message-store provisioning (the `wolverine` schema + envelope tables/functions). Wolverine
        // auto-builds missing storage on startup BY DEFAULT — fine for the always-on tracking node (the
        // Api / CLI / tests run as a principal that owns the `wolverine` schema, so the build is allowed).
        // The scale-to-zero MCP node (runScheduledAgents=false) runs as uami-mcp, which does NOT own the
        // schema and must never attempt DDL — and two nodes racing to build one shared store is undefined.
        // So the serverless node disables the auto-build: the Api owns + provisions the store; this node
        // only reads/writes the already-provisioned tables (its grants come via role membership — see the
        // host stack bootstrap SQL). NOT UseResourceSetupOnStartup() anywhere: that PURGES envelope state.
        if (!runScheduledAgents)
        {
            options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        }

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
