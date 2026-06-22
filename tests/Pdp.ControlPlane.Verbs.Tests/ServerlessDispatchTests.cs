using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// Regression for the live MCP-node failure: the scale-to-zero MCP host (<c>runScheduledAgents: false</c>)
/// was configured for <c>DurabilityMode.Serverless</c>, which DISABLES the transactional inbox/outbox. But
/// the verb's dispatch cascade (<c>BeginFabricProvisioning → DispatchWorkflowCommand → GitHub dispatch</c>)
/// is enrolled in the durable outbox and routed to <c>local://durable/</c>, so Serverless couldn't build
/// that sender and threw <c>Wolverine.Runtime.UnknownTransportException</c> on the first dispatch. The fix
/// keeps the MCP node on <c>DurabilityMode.Solo</c> (durable outbox present); it's kept off the Api node's
/// toes by registration (only the Api runs the reconciler) rather than by a crippled durability mode.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class ServerlessDispatchTests(ControlPlanePostgresFixture fixture)
{
    [Fact]
    public async Task Mcp_node_dispatches_a_plan_without_UnknownTransportException()
    {
        await fixture.ResetAsync();
        var dispatcher = Substitute.For<IWorkflowDispatcher>();
        using var gitHub = new FakeGitHubServer();

        // Boot a host with the MCP node's messaging configuration (runScheduledAgents: false). The wolverine
        // message store is already provisioned by the fixture (the MCP node has AutoBuildMessageStorageOnStartup
        // = None and never builds it). Pre-fix, the dispatch cascade below threw UnknownTransportException.
        var host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services =>
            {
                services.AddSingleton(dispatcher);
                services.AddSingleton<IGitHubAppCredential>(new StubGitHubAppCredential(gitHub.BaseUrl));
            },
            runScheduledAgents: false);
        try
        {
            using var scope = host.Services.CreateScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();

            // The exact call that failed live (PlanFabricCreate → PlanCreateAsync → dispatch a plan run).
            var plan = await verbs.PlanCreateAsync(new FabricCreateRequest("eastus2", 5));

            plan.ShouldNotBeNull();
            // The dispatch cascade actually ran (not lost / not thrown).
            await dispatcher.ReceivedWithAnyArgs(1).DispatchAsync(default!, default);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
