using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pdp.ControlPlane.Verbs;
using Wolverine;

namespace Pdp.ControlPlane.TestSupport;

/// <summary>
/// Builds and starts the full control-plane verb layer (DI + Wolverine durable inbox/outbox + saga)
/// against the Testcontainers Postgres for the integration tests — the verb→allocate→record→dispatch
/// spine, the lifecycle saga, and the run tracker all exercised on real Postgres (the only way the
/// outbox/saga semantics are testable — plan §Testing). GitHub is faked at its seams: tests override
/// <c>IWorkflowDispatcher</c> and/or <c>IGitHubAppCredential</c> through <paramref name="configureOverrides"/>.
/// </summary>
public static class ControlPlaneTestHost
{
    /// <summary>
    /// Starts a host bound to <paramref name="connectionString"/>. <paramref name="configureOverrides"/>
    /// runs after <c>AddControlPlaneVerbs</c>, so a test's fake registrations (last-wins) replace the
    /// real GitHub seams.
    /// </summary>
    public static async Task<IHost> StartAsync(
        string connectionString,
        Action<IServiceCollection>? configureOverrides = null,
        bool runScheduledAgents = true,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControlPlane:PostgresConnectionString"] = connectionString,
            ["GitHubApp:AppId"] = "1",
            ["GitHubApp:InstallationId"] = "1",
            ["GitHubApp:Owner"] = "pdp-owner",
            ["GitHubApp:Repository"] = "platform",
            ["GitHubApp:DefaultBranch"] = "main",
        });

        builder.Services.AddControlPlaneVerbs(builder.Configuration);
        builder.UseWolverine(opts => opts.ConfigureControlPlaneMessaging(connectionString, runScheduledAgents));

        configureOverrides?.Invoke(builder.Services);

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }
}
