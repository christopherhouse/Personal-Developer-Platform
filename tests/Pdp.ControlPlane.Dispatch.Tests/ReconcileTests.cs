using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Pdp.ControlPlane.Dispatch.Tests;

/// <summary>
/// The polling reconciler closing the loop (T027, SC-006): it correlates an in-flight run by its
/// <c>run-name</c> → <c>env_id</c> against the GitHub Actions API and records the terminal outcome,
/// which drives the environment's lifecycle saga to <c>Active</c>. GitHub is faked (WireMock) and the
/// dispatcher is a no-op, so the test exercises the tracking path on real Postgres without GitHub.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class ReconcileTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "55555555-5555-5555-5555-555555555555";

    private readonly FakeGitHubServer _gitHub = new();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services =>
            {
                services.AddSingleton(Substitute.For<IWorkflowDispatcher>());
                services.AddSingleton<IGitHubAppCredential>(new StubGitHubAppCredential(_gitHub.BaseUrl));
            });
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Reconciler_correlates_a_completed_run_and_drives_the_environment_active()
    {
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

            await ledger.RegisterRegionAsync("westus3", 2);
            var result = await verbs.CreateAsync(
                new SpokeCreateRequest(Subscription, "westus3", "app1", Size: 24),
                Confirmation.ForApply());
            envId = result.EnvId;
        }

        // GitHub reports the dispatched run (correlated by run-name) as completed successfully.
        StubRunsList($"pdp apply {envId}", conclusion: "success", runId: 9001);

        using (var scope = _host.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRunTracker>();
            var advanced = await tracker.ReconcileInFlightAsync();
            advanced.ShouldBe(1);
        }

        // The saga processes RunCompleted asynchronously — wait for the environment to reach Active.
        await WaitForStatusAsync(envId, EnvironmentStatus.Active);

        await using var registry = fixture.CreateRegistryContext();
        var environment = await registry.Environments.SingleAsync(e => e.EnvId == envId);
        environment.Status.ShouldBe(EnvironmentStatus.Active);

        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
        run.Outcome.ShouldBe(RunOutcome.Succeeded);
        run.TrackedBy.ShouldBe(TrackingSource.Reconciler);
        run.GitHubRunId.ShouldBe(9001);
        run.CompletedAt.ShouldNotBeNull();
    }

    private void StubRunsList(string runName, string conclusion, long runId)
    {
        var json = $$"""
        {
          "total_count": 1,
          "workflow_runs": [
            {
              "id": {{runId}},
              "name": "{{runName}}",
              "status": "completed",
              "conclusion": "{{conclusion}}",
              "html_url": "https://github.com/pdp-owner/platform/actions/runs/{{runId}}"
            }
          ]
        }
        """;

        // Wildcard path: Octokit prepends /api/v3 for the non-github.com (WireMock) base address.
        _gitHub.Server
            .Given(Request.Create().WithPath(new WildcardMatcher("*/actions/runs")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private async Task WaitForStatusAsync(Guid envId, EnvironmentStatus expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var registry = fixture.CreateRegistryContext();
            var environment = await registry.Environments.SingleOrDefaultAsync(e => e.EnvId == envId);
            if (environment?.Status == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Environment {envId} did not reach {expected} in time.");
    }
}
