using System.Text.Json;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Pdp.ControlPlane.Dispatch.Tests;

/// <summary>
/// The dispatcher's outbound contract against the fake GitHub (T026): <c>GitHubWorkflowDispatcher</c>
/// posts a <c>workflow_dispatch</c> to the exact Actions endpoint with the precise <c>ref</c> and
/// inputs — <c>env_id</c>, <c>mode</c>, the ledger-allocated <c>spoke_cidr</c>, and the target — so the
/// dispatched run's <c>run-name</c> (set by the workflow) resolves back to the environment (research §4).
/// </summary>
public sealed class DispatchInputsTests : IDisposable
{
    private readonly FakeGitHubServer _gitHub = new();

    public void Dispose() => _gitHub.Dispose();

    [Fact]
    public async Task Dispatch_posts_exact_workflow_inputs_and_ref()
    {
        // The workflow-dispatch endpoint suffix. (Octokit prepends /api/v3 for a non-github.com base —
        // a test-only artifact of pointing it at the WireMock fake; production targets api.github.com.)
        const string dispatchPathSuffix = "/repos/pdp-owner/platform/actions/workflows/spoke-vend.yml/dispatches";
        _gitHub.Server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var options = new GitHubAppOptions
        {
            Owner = "pdp-owner",
            Repository = "platform",
            DefaultBranch = "main",
            ApiBaseUrl = _gitHub.BaseUrl,
        };
        var dispatcher = new GitHubWorkflowDispatcher(new StubGitHubAppCredential(_gitHub.BaseUrl), options);

        var envId = Guid.CreateVersion7();
        var inputs = new Dictionary<string, string>
        {
            ["env_id"] = envId.ToString(),
            ["mode"] = "apply",
            ["region"] = "westus3",
            ["region_index"] = "2",
            ["target_subscription_id"] = "44444444-4444-4444-4444-444444444444",
            ["spoke_name"] = "app1",
            ["spoke_cidr"] = "10.2.0.0/24",
        };

        await dispatcher.DispatchAsync(
            new WorkflowDispatch("spoke-vend.yml", "main", envId, RunPhase.Apply, inputs));

        var request = _gitHub.Server.LogEntries
            .Single(e => e.RequestMessage?.Method == "POST");
        request.RequestMessage!.Path.ShouldEndWith(dispatchPathSuffix);

        var rawBody = request.RequestMessage?.Body ?? throw new InvalidOperationException("dispatch had no body");
        using var body = JsonDocument.Parse(rawBody);
        body.RootElement.GetProperty("ref").GetString().ShouldBe("main");

        var sentInputs = body.RootElement.GetProperty("inputs");
        sentInputs.GetProperty("env_id").GetString().ShouldBe(envId.ToString());
        sentInputs.GetProperty("mode").GetString().ShouldBe("apply");
        sentInputs.GetProperty("spoke_cidr").GetString().ShouldBe("10.2.0.0/24");
        sentInputs.GetProperty("region").GetString().ShouldBe("westus3");
        sentInputs.GetProperty("target_subscription_id").GetString().ShouldBe("44444444-4444-4444-4444-444444444444");
        sentInputs.GetProperty("spoke_name").GetString().ShouldBe("app1");
    }
}
