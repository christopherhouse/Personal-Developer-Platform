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
/// US4 — the intent registry + provisioning-run audit trail on real Postgres (T057): after a vend, the
/// environment records its status transition (Requested→Provisioning→Active) and the
/// <c>provisioning_runs</c> row captures the full audit (jsonb <c>DispatchInputs</c>, GitHub run id/url,
/// <c>Outcome</c>, <c>TrackedBy</c>, <c>CompletedAt</c>). The run verbs (<see cref="IRunVerbs"/>) project
/// that intent/history — "what did I ask for and what happened?" — while "what's deployed?" is the
/// inventory/ARG path, never the registry (division of truth, FR-016). GitHub is faked: the dispatcher
/// is a substitute and completion is driven by the reconciler over WireMock.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class RegistryAuditTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "99999999-9999-9999-9999-999999999999";
    private const long RunGitHubId = 9201;

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
        _host.Dispose();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Registry_records_status_and_full_run_audit_readable_through_the_run_verbs()
    {
        // Vend a spoke and drive its apply run to a successful terminal via the reconciler.
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            await ledger.RegisterRegionAsync("westus3", 2);
            var result = await verbs.CreateAsync(
                new SpokeCreateRequest(Subscription, "westus3", "app5", Size: 24),
                Confirmation.ForApply());
            envId = result.EnvId;
        }

        StubRunsList($"pdp apply {envId}", "success", RunGitHubId);
        using (var scope = _host.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRunTracker>();
            await tracker.ReconcileInFlightAsync();
        }

        await WaitForStatusAsync(envId, EnvironmentStatus.Active);

        // The provisioning_runs row captured the full audit (T057/T058): jsonb inputs + run id/url +
        // terminal outcome + which signal recorded it + completion time.
        await using (var registry = fixture.CreateRegistryContext())
        {
            var env = await registry.Environments.SingleAsync(e => e.EnvId == envId);
            env.Status.ShouldBe(EnvironmentStatus.Active);
            env.SpokeCidr!.ToString().ShouldBe("10.2.0.0/24");

            var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
            run.Phase.ShouldBe(RunPhase.Apply);
            run.DispatchInputs.ShouldContain("spoke_cidr");
            run.DispatchInputs.ShouldContain("10.2.0.0/24");
            run.GitHubRunId.ShouldBe(RunGitHubId);
            run.GitHubRunUrl.ShouldNotBeNullOrWhiteSpace();
            run.Outcome.ShouldBe(RunOutcome.Succeeded);
            run.TrackedBy.ShouldBe(TrackingSource.Reconciler);
            run.CompletedAt.ShouldNotBeNull();
        }

        // The run verbs project that intent + history (FR-015) — by env_id and by run id.
        using (var scope = _host.Services.CreateScope())
        {
            var runVerbs = scope.ServiceProvider.GetRequiredService<IRunVerbs>();

            var record = await runVerbs.GetEnvironmentAsync(EnvRef.ById(envId));
            record.ShouldNotBeNull();
            record!.Status.ShouldBe(EnvironmentStatus.Active);
            record.SpokeCidr.ShouldBe("10.2.0.0/24");
            record.Kind.ShouldBe(EnvironmentKind.Spoke);

            var runs = await runVerbs.GetRunsAsync(EnvRef.ById(envId));
            var only = runs.ShouldHaveSingleItem();
            only.Outcome.ShouldBe(RunOutcome.Succeeded);
            only.TrackedBy.ShouldBe(TrackingSource.Reconciler);
            // DispatchInputs is parsed back to a map (not a doubly-escaped string) — consumable --json.
            only.DispatchInputs["mode"].ShouldBe("apply");
            only.DispatchInputs["spoke_cidr"].ShouldBe("10.2.0.0/24");

            // Resolvable by run id too (the `run show` path).
            var byId = await runVerbs.GetRunAsync(only.RunId);
            byId.ShouldNotBeNull();
            byId!.RunId.ShouldBe(only.RunId);

            // Also resolvable by the compact natural-key ref the CLI accepts (spoke:<sub>:app5).
            EnvRef.TryParse($"spoke:{Subscription}:app5", out var byKey).ShouldBeTrue();
            var viaKey = await runVerbs.GetEnvironmentAsync(byKey!);
            viaKey!.EnvId.ShouldBe(envId);
        }
    }

    [Fact]
    public async Task Unknown_environment_reads_as_a_clean_empty_result()
    {
        using var scope = _host.Services.CreateScope();
        var runVerbs = scope.ServiceProvider.GetRequiredService<IRunVerbs>();

        (await runVerbs.GetEnvironmentAsync(EnvRef.ById(Guid.CreateVersion7()))).ShouldBeNull();
        (await runVerbs.GetRunsAsync(EnvRef.ById(Guid.CreateVersion7()))).ShouldBeEmpty();
        (await runVerbs.GetRunAsync(Guid.CreateVersion7())).ShouldBeNull();
    }

    private void StubRunsList(string runName, string conclusion, long runId)
    {
        _gitHub.Server.Reset();
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
            var env = await registry.Environments.SingleOrDefaultAsync(e => e.EnvId == envId);
            if (env?.Status == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Environment {envId} did not reach {expected} in time.");
    }
}
