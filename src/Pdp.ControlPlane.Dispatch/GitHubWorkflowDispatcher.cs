using Octokit;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// Dispatches GitHub Actions workflows as the <c>pdp-orchestrator</c> App (Article II / FR-002/FR-003):
/// an installation-scoped <see cref="IGitHubClient"/> from <see cref="IGitHubAppCredential"/> calls
/// <c>Actions.Workflows.CreateDispatch</c> with the exact <c>workflow_dispatch</c> inputs (including the
/// ledger-allocated <c>spoke_cidr</c> — never a user value). Outbound calls run through a Polly v8
/// resilience pipeline (retry on transient GitHub failures + an overall timeout) per the pinned
/// <c>Microsoft.Extensions.Http.Resilience</c> stack. Durability (send iff the intent commits) is the
/// caller's transactional outbox; this adapter performs only the HTTP call.
/// </summary>
public sealed class GitHubWorkflowDispatcher : IWorkflowDispatcher
{
    private readonly IGitHubAppCredential _credential;
    private readonly GitHubAppOptions _options;
    private readonly ResiliencePipeline _pipeline;

    /// <summary>Creates the dispatcher from the App credential and bound options.</summary>
    public GitHubWorkflowDispatcher(IGitHubAppCredential credential, GitHubAppOptions options)
    {
        _credential = credential;
        _options = options;
        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                // Transient GitHub failures: server errors and connection faults. 4xx (bad inputs,
                // missing workflow, auth) are non-retryable and surface immediately.
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<ApiException>(static e => (int)e.StatusCode >= 500),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
            })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }

    /// <inheritdoc />
    public async Task DispatchAsync(WorkflowDispatch dispatch, CancellationToken cancellationToken = default)
    {
        var client = await _credential.CreateInstallationClientAsync(cancellationToken).ConfigureAwait(false);

        var createDispatch = new CreateWorkflowDispatch(dispatch.GitRef)
        {
            Inputs = dispatch.Inputs.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
        };

        await _pipeline.ExecuteAsync(
            async token => await client.Actions.Workflows
                .CreateDispatch(_options.Owner, _options.Repository, dispatch.WorkflowFile, createDispatch)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }
}
