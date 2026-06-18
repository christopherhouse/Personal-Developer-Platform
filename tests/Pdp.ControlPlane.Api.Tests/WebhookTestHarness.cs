using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;

namespace Pdp.ControlPlane.Api.Tests;

/// <summary>
/// Shared scaffolding for the API-host webhook tests (US5): boots <c>Pdp.ControlPlane.Api</c> via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> bound to the Testcontainers Postgres, fakes the
/// GitHub seams, and builds HMAC-signed <c>workflow_run</c> deliveries exactly as GitHub does
/// (<c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> over the raw body).
/// </summary>
internal static class WebhookTestHarness
{
    /// <summary>The webhook signing secret the host is configured with for the tests.</summary>
    public const string WebhookSecret = "pdp-test-webhook-secret-0123456789";

    /// <summary>The internal webhook path the Api maps (and the Ingress forwards to).</summary>
    public const string WebhookPath = "/webhooks/github";

    /// <summary>
    /// Builds an Api host bound to <paramref name="connectionString"/> with the GitHub seams faked: the
    /// dispatcher is a no-op (a real <c>workflow_dispatch</c> would need a live App key) and the
    /// reconciler's recurring sweep is pushed an hour out so only an <i>explicit</i> reconcile runs in a
    /// test. Pass a <paramref name="credential"/> (pointed at a fake GitHub) only for the reconcile path.
    /// </summary>
    /// <remarks>
    /// The Api's <c>Program</c> reads its Postgres/GitHub config <i>eagerly</i> (to register the
    /// DbContexts and configure Wolverine) before <c>WebApplicationFactory</c> can layer in-memory config,
    /// so the test config is supplied via environment variables — which <c>WebApplication.CreateBuilder</c>
    /// reads at construction. The values are constant for the run (one shared container), so the
    /// process-wide set is safe across the serialized collection.
    /// </remarks>
    public static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        IWorkflowDispatcher dispatcher,
        IGitHubAppCredential? credential = null)
    {
        System.Environment.SetEnvironmentVariable("ControlPlane__PostgresConnectionString", connectionString);
        System.Environment.SetEnvironmentVariable("GitHubApp__AppId", "1");
        System.Environment.SetEnvironmentVariable("GitHubApp__InstallationId", "1");
        System.Environment.SetEnvironmentVariable("GitHubApp__Owner", "pdp-owner");
        System.Environment.SetEnvironmentVariable("GitHubApp__Repository", "platform");
        System.Environment.SetEnvironmentVariable("GitHubApp__DefaultBranch", "main");
        System.Environment.SetEnvironmentVariable("GitHubApp__WebhookSecret", WebhookSecret);

        return new PdpWebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton(dispatcher);
                // Neutralise the background sweep so cross-test scheduled messages never fire mid-test;
                // the reconcile-path test triggers ReconcileInFlightAsync explicitly.
                services.AddSingleton(new ReconcilerOptions { Interval = TimeSpan.FromHours(1) });
                if (credential is not null)
                {
                    services.AddSingleton(credential);
                }
            }));
    }

    // A complete real workflow_run delivery (captured from octokit/webhooks.net's fixtures) — the typed
    // model has dozens of `required` members, so a hand-rolled body fails STJ deserialization. We mutate
    // only the fields the handler cares about (correlation run-name, outcome, id) and re-serialize.
    private static readonly string FixtureTemplate = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Resources", "workflow_run.completed.json"));

    /// <summary>
    /// Builds a valid <c>workflow_run.completed</c> body whose <c>workflow_run.name</c> carries the
    /// <c>pdp &lt;mode&gt; &lt;env_id&gt;</c> correlation, with the given run id and terminal conclusion.
    /// </summary>
    public static string WorkflowRunPayload(Guid envId, RunPhase mode, long runId, string conclusion)
    {
        var root = JsonNode.Parse(FixtureTemplate)!;
        root["action"] = "completed";

        var run = root["workflow_run"]!;
        run["name"] = RunNameCorrelation.Format(mode, envId);
        run["id"] = runId;
        run["status"] = "completed";
        run["conclusion"] = conclusion;

        return root.ToJsonString();
    }

    /// <summary>Builds a signed POST to the webhook endpoint, optionally tampering the signature.</summary>
    public static HttpRequestMessage SignedRequest(string payload, bool validSignature = true)
    {
        var signature = validSignature ? ComputeSignature(payload) : "sha256=" + new string('0', 64);
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", "workflow_run");
        request.Headers.Add("X-GitHub-Delivery", Guid.CreateVersion7().ToString());
        request.Headers.Add("X-Hub-Signature-256", signature);
        return request;
    }

    /// <summary>The exact <c>sha256=&lt;hex&gt;</c> HMAC GitHub sends — over the raw UTF-8 body bytes.</summary>
    private static string ComputeSignature(string payload)
    {
        var key = Encoding.UTF8.GetBytes(WebhookSecret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        var hex = Convert.ToHexStringLower(hash);
        return "sha256=" + hex;
    }

    /// <summary>Polls the registry until <paramref name="envId"/> reaches <paramref name="expected"/>.</summary>
    public static async Task WaitForStatusAsync(
        ControlPlanePostgresFixture fixture,
        Guid envId,
        EnvironmentStatus expected,
        int maxAttempts = 100)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await using var registry = fixture.CreateRegistryContext();
            var environment = await registry.Environments.SingleOrDefaultAsync(e => e.EnvId == envId);
            if (environment?.Status == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Environment {envId} did not reach {expected} within {maxAttempts * 100} ms.");
    }
}
