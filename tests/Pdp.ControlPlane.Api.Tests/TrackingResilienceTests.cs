using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

namespace Pdp.ControlPlane.Api.Tests;

/// <summary>
/// Resilient completion tracking (T062, US5/SC-006): the webhook is the low-latency primary path, but the
/// polling reconciler guarantees completion even when a delivery is <b>missed</b>, and a <b>duplicate</b>
/// delivery is idempotent (first-terminal-wins; <c>TrackedBy</c> records the winning signal). GitHub is
/// faked (WireMock for the reconciler's Actions-API read; a no-op dispatcher), all on real Postgres.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class TrackingResilienceTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "77777777-7777-7777-7777-777777777777";

    private readonly FakeGitHubServer _gitHub = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _factory = WebhookTestHarness.CreateFactory(
            fixture.ConnectionString,
            Substitute.For<IWorkflowDispatcher>(),
            new StubGitHubAppCredential(_gitHub.BaseUrl));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Missed_webhook_is_recovered_by_the_reconciler()
    {
        _ = _factory.CreateClient(); // start the host
        var envId = await VendSpokeAsync("app1");

        // No webhook is delivered. GitHub (faked) reports the dispatched run, correlated by run-name, as
        // completed successfully; the reconciler's sweep must pick it up and drive the env terminal.
        StubRunsList(RunNameCorrelation.Format(RunPhase.Apply, envId), conclusion: "success", runId: 9100);

        using (var scope = _factory.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRunTracker>();
            var advanced = await tracker.ReconcileInFlightAsync();
            advanced.ShouldBe(1);
        }

        await WebhookTestHarness.WaitForStatusAsync(fixture, envId, EnvironmentStatus.Active);

        await using var registry = fixture.CreateRegistryContext();
        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
        run.Outcome.ShouldBe(RunOutcome.Succeeded);
        run.TrackedBy.ShouldBe(TrackingSource.Reconciler);
        run.GitHubRunId.ShouldBe(9100);
    }

    [Fact]
    public async Task Duplicate_webhook_is_idempotent_first_terminal_wins()
    {
        var client = _factory.CreateClient();
        var envId = await VendSpokeAsync("app2");

        var payload = WebhookTestHarness.WorkflowRunPayload(envId, RunPhase.Apply, runId: 9200, conclusion: "success");

        var first = await client.SendAsync(WebhookTestHarness.SignedRequest(payload));
        first.IsSuccessStatusCode.ShouldBeTrue();
        await WebhookTestHarness.WaitForStatusAsync(fixture, envId, EnvironmentStatus.Active);

        // A redelivery of the same run — accepted at the endpoint, but a no-op at the tracker.
        var second = await client.SendAsync(WebhookTestHarness.SignedRequest(payload));
        second.IsSuccessStatusCode.ShouldBeTrue();
        // Give the inbox a moment to (not) change anything.
        await Task.Delay(300);

        await using var registry = fixture.CreateRegistryContext();
        var runs = await registry.ProvisioningRuns.Where(r => r.EnvId == envId).ToListAsync();
        runs.Count.ShouldBe(1);
        runs[0].Outcome.ShouldBe(RunOutcome.Succeeded);
        runs[0].TrackedBy.ShouldBe(TrackingSource.Webhook); // first-terminal-wins, unchanged by the duplicate
        runs[0].GitHubRunId.ShouldBe(9200);
    }

    private async Task<Guid> VendSpokeAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
        var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

        await ledger.RegisterRegionAsync("westus3", 2);
        var result = await verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "westus3", name, Size: 24),
            Confirmation.ForApply());
        return result.EnvId;
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

        _gitHub.Server
            .Given(Request.Create().WithPath(new WildcardMatcher("*/actions/runs")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }
}
