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

namespace Pdp.ControlPlane.Api.Tests;

/// <summary>
/// The internal webhook endpoint (T061, US5/FR-005): <c>MapGitHubWebhooks</c> validates the
/// <c>X-Hub-Signature-256</c> HMAC, deserialises the typed <c>workflow_run</c>, and the handler enqueues
/// it to the durable inbox → <c>RecordRunStatusAsync</c>, which records the terminal outcome and drives
/// the lifecycle saga. A forged signature is rejected before any state changes. GitHub is faked at its
/// seams; the flow runs on real Postgres (the only way the inbox/saga semantics are testable).
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class WebhookHandlerTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "66666666-6666-6666-6666-666666666666";

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _factory = WebhookTestHarness.CreateFactory(
            fixture.ConnectionString,
            Substitute.For<IWorkflowDispatcher>());
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Signed_completed_workflow_run_records_terminal_outcome_via_the_inbox()
    {
        var client = _factory.CreateClient();
        var envId = await VendSpokeAsync("app1");

        var payload = WebhookTestHarness.WorkflowRunPayload(envId, RunPhase.Apply, runId: 7700, conclusion: "success");
        var response = await client.SendAsync(WebhookTestHarness.SignedRequest(payload));

        response.IsSuccessStatusCode.ShouldBeTrue($"webhook POST returned {(int)response.StatusCode}");

        // The handler inboxed the delivery; RecordRunStatusAsync records terminal + emits RunCompleted,
        // which the saga processes asynchronously — wait for the environment to reach Active.
        await WebhookTestHarness.WaitForStatusAsync(fixture, envId, EnvironmentStatus.Active);

        await using var registry = fixture.CreateRegistryContext();
        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
        run.Outcome.ShouldBe(RunOutcome.Succeeded);
        run.TrackedBy.ShouldBe(TrackingSource.Webhook);
        run.GitHubRunId.ShouldBe(7700);
        run.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Forged_signature_is_rejected_and_records_nothing()
    {
        var client = _factory.CreateClient();
        var envId = await VendSpokeAsync("app2");

        var payload = WebhookTestHarness.WorkflowRunPayload(envId, RunPhase.Apply, runId: 7701, conclusion: "success");
        var response = await client.SendAsync(WebhookTestHarness.SignedRequest(payload, validSignature: false));

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400);

        // The delivery never reached the handler: the run stays as dispatched, the env stays Provisioning.
        await using var registry = fixture.CreateRegistryContext();
        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
        run.Outcome.ShouldBe(RunOutcome.Dispatched);
        run.TrackedBy.ShouldBeNull();

        var environment = await registry.Environments.SingleAsync(e => e.EnvId == envId);
        environment.Status.ShouldBe(EnvironmentStatus.Provisioning);
    }

    /// <summary>Vends a spoke through the host's verb layer (no-op dispatcher) and returns its env_id.</summary>
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
}
