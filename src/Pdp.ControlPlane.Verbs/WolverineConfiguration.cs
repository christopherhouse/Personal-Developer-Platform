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
    /// Applies the control plane's durability and saga configuration to <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The Wolverine options being configured (inside <c>UseWolverine</c>).</param>
    /// <param name="postgresConnectionString">The platform Postgres connection (shared server).</param>
    public static WolverineOptions ConfigureControlPlaneMessaging(
        this WolverineOptions options,
        string postgresConnectionString)
    {
        // Durable transactional inbox/outbox, isolated in its own schema on the shared server so a
        // committed allocate+record transaction and its dispatch are all-or-nothing (research §6).
        options.PersistMessagesWithPostgresql(postgresConnectionString, MessageSchema);

        // EF Core transactional middleware + EF Core saga support. Wolverine auto-detects the
        // RegistryDbContext that maps EnvironmentSaga (research §2).
        options.UseEntityFrameworkCoreTransactions();

        // The reconciler's scheduled messages and the dispatch/track flow survive restarts.
        options.Policies.UseDurableLocalQueues();

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
