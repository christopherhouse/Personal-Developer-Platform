using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// The US2 two-phase Article VIII gate on real Postgres (T041): <c>spoke create</c> first dispatches a
/// <c>mode=plan</c> run and surfaces its captured <c>PlanSummary</c> (downloaded from the plan run's
/// artifact — T043a); the apply (<c>mode=apply</c>) is dispatched <b>only</b> after an explicit
/// confirmation. GitHub is faked: the dispatcher is a substitute (so the dispatched mode is asserted),
/// while the run-status list + plan artifact are served by WireMock so the plan-output capture and the
/// reconcile-driven completion run end to end.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class PlanConfirmTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "77777777-7777-7777-7777-777777777777";
    private const string PlanText = "OpenTofu will perform the following actions: + 1 to add, 0 to change, 0 to destroy.";

    private readonly FakeGitHubServer _gitHub = new();
    private readonly IWorkflowDispatcher _dispatcher = Substitute.For<IWorkflowDispatcher>();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services =>
            {
                services.AddSingleton(_dispatcher);
                services.AddSingleton<IGitHubAppCredential>(new StubGitHubAppCredential(_gitHub.BaseUrl));
            });
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Plan_is_surfaced_before_apply_and_apply_dispatches_only_after_confirmation()
    {
        var request = new SpokeCreateRequest(Subscription, "westus3", "app1", Size: 24);

        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

            await ledger.RegisterRegionAsync("westus3", 2);
            var plan = await verbs.PlanCreateAsync(request);
            envId = plan.EnvId;

            plan.Phase.ShouldBe(RunPhase.Plan);
            // ProposedInputs are exactly what the confirmed apply will dispatch.
            plan.ProposedInputs["mode"].ShouldBe("apply");
            plan.ProposedInputs["spoke_cidr"].ShouldBe("10.2.0.0/24");
        }

        // Only a plan run was dispatched (mode=plan); the apply has NOT been dispatched (Article VIII).
        await WaitForDispatchAsync(envId, RunPhase.Plan);
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d => d.EnvId == envId && d.Mode == RunPhase.Apply),
            Arg.Any<CancellationToken>());

        await using (var registry = fixture.CreateRegistryContext())
        {
            var env = await registry.Environments.SingleAsync(e => e.EnvId == envId);
            env.Status.ShouldBe(EnvironmentStatus.Provisioning);
            var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
            run.Phase.ShouldBe(RunPhase.Plan);
        }

        // GitHub reports the plan run complete and serves its plan artifact; the reconciler records the
        // terminal plan and captures the PlanSummary (T043a).
        StubRunsList($"pdp plan {envId}", runId: 9100);
        StubArtifacts(runId: 9100, artifactId: 5001, artifactName: $"plan-{envId}");
        StubArtifactDownload(artifactId: 5001, ZipWith("plan.txt", PlanText));

        using (var scope = _host.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRunTracker>();
            (await tracker.ReconcileInFlightAsync()).ShouldBe(1);
        }

        await WaitForPlanSummaryAsync(envId);

        await using (var registry = fixture.CreateRegistryContext())
        {
            // The plan completed, but the environment is still Provisioning — no apply yet (two-phase).
            var env = await registry.Environments.SingleAsync(e => e.EnvId == envId);
            env.Status.ShouldBe(EnvironmentStatus.Provisioning);

            var planRun = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId && r.Phase == RunPhase.Plan);
            planRun.Outcome.ShouldBe(RunOutcome.Succeeded);
            planRun.PlanSummary.ShouldBe(PlanText);
        }

        // The owner confirms → the apply is finally dispatched (mode=apply).
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            var result = await verbs.CreateAsync(request, Confirmation.ForApply());
            result.Status.ShouldBe(EnvironmentStatus.Provisioning);
            result.Outcome.ShouldBe(RunOutcome.Dispatched);
        }

        await WaitForDispatchAsync(envId, RunPhase.Apply);
        await _dispatcher.Received().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d =>
                d.EnvId == envId &&
                d.Mode == RunPhase.Apply &&
                d.WorkflowFile == "spoke-vend.yml" &&
                d.Inputs["spoke_cidr"] == "10.2.0.0/24"),
            Arg.Any<CancellationToken>());

        // A distinct apply run row now exists alongside the plan run (the audit trail — FR-015).
        await using (var registry = fixture.CreateRegistryContext())
        {
            var runs = await registry.ProvisioningRuns.Where(r => r.EnvId == envId).ToListAsync();
            runs.Select(r => r.Phase).ShouldBe(new[] { RunPhase.Plan, RunPhase.Apply }, ignoreOrder: true);
        }
    }

    private void StubRunsList(string runName, long runId)
    {
        var json = $$"""
        {
          "total_count": 1,
          "workflow_runs": [
            {
              "id": {{runId}},
              "name": "{{runName}}",
              "status": "completed",
              "conclusion": "success",
              "html_url": "https://github.com/pdp-owner/platform/actions/runs/{{runId}}"
            }
          ]
        }
        """;

        _gitHub.Server
            .Given(Request.Create().WithPath(new WildcardMatcher("*/actions/runs")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private void StubArtifacts(long runId, long artifactId, string artifactName)
    {
        var json = $$"""
        {
          "total_count": 1,
          "artifacts": [
            {
              "id": {{artifactId}},
              "name": "{{artifactName}}",
              "size_in_bytes": 1024,
              "expired": false
            }
          ]
        }
        """;

        _gitHub.Server
            .Given(Request.Create().WithPath(new WildcardMatcher($"*/actions/runs/{runId}/artifacts")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private void StubArtifactDownload(long artifactId, byte[] zipBytes)
    {
        _gitHub.Server
            .Given(Request.Create().WithPath(new WildcardMatcher($"*/actions/artifacts/{artifactId}/zip")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/zip")
                .WithBody(zipBytes));
    }

    private static byte[] ZipWith(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private async Task WaitForDispatchAsync(Guid envId, RunPhase mode)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var seen = _dispatcher.ReceivedCalls()
                .Select(c => c.GetArguments()[0])
                .OfType<WorkflowDispatch>()
                .Any(d => d.EnvId == envId && d.Mode == mode);
            if (seen)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No {mode} dispatch for env {envId} in time.");
    }

    private async Task WaitForPlanSummaryAsync(Guid envId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var registry = fixture.CreateRegistryContext();
            var run = await registry.ProvisioningRuns
                .SingleOrDefaultAsync(r => r.EnvId == envId && r.Phase == RunPhase.Plan);
            if (run?.PlanSummary is not null)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Plan summary for env {envId} was not captured in time.");
    }
}
