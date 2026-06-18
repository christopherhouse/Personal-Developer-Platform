using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Registry.Lifecycle;
using Wolverine;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// Correlates <c>workflow_run</c> signals — from the webhook (US5) or the polling reconciler (US1) —
/// back to an <c>env_id</c> and records the terminal outcome (FR-004/FR-005, SC-006). Recording is
/// idempotent (first-terminal-wins, deduped by <c>(env_id, github_run_id)</c>); on terminal it emits
/// <see cref="RunCompleted"/>/<see cref="RunFailed"/> to the lifecycle saga. The reconciler sweep is
/// self-healing: an environment left non-terminal while its run is already terminal in our store has
/// its lifecycle event re-emitted, closing the gap where a prior emit was lost.
/// </summary>
public sealed class RunTracker(
    RegistryDbContext context,
    IMessageBus bus,
    IGitHubAppCredential credential,
    GitHubAppOptions options,
    TimeProvider? timeProvider = null) : IRunTracker
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private static bool IsTerminal(RunOutcome outcome) => outcome is
        RunOutcome.Succeeded or RunOutcome.Failed or RunOutcome.Cancelled or RunOutcome.TimedOut;

    /// <inheritdoc />
    public async Task RecordRunStatusAsync(
        WorkflowRunStatus status,
        TrackingSource source,
        CancellationToken cancellationToken = default)
    {
        if (!RunNameCorrelation.TryParse(status.RunName, out var mode, out var envId))
        {
            return; // Not one of ours — ignore foreign runs (research §4).
        }

        // Correlate to the in-flight run: by the GitHub run id once known, else the latest dispatched
        // run for this environment + phase still awaiting an id.
        var run = await context.ProvisioningRuns
            .Where(r => r.EnvId == envId &&
                        (r.GitHubRunId == status.GitHubRunId || (r.GitHubRunId == null && r.Phase == mode)))
            .OrderByDescending(r => r.DispatchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return;
        }

        if (IsTerminal(run.Outcome))
        {
            return; // First-terminal-wins: a prior signal already recorded the outcome (FR-005).
        }

        run.GitHubRunId = status.GitHubRunId;
        run.GitHubRunUrl = status.Url;
        run.Outcome = status.Outcome;
        if (IsTerminal(status.Outcome))
        {
            run.CompletedAt = _time.GetUtcNow();
            run.TrackedBy = source;
        }

        // Article VIII plan-output capture (contracts/dispatch-and-tracking.md §6): when a plan run
        // succeeds, download its plan artifact into PlanSummary so the verb can surface the actual
        // `tofu plan` for confirmation. Best-effort — a missing/oversized artifact leaves PlanSummary
        // null and the owner reviews via the run URL (never silently treats a missing plan as approved).
        if (run.Phase == RunPhase.Plan && status.Outcome == RunOutcome.Succeeded)
        {
            run.PlanSummary = await TryDownloadPlanSummaryAsync(envId, status.GitHubRunId, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            // A concurrent signal (webhook vs reconciler) recorded this run first — idempotent no-op.
            return;
        }

        if (IsTerminal(status.Outcome))
        {
            await EmitLifecycleAsync(envId, mode, status.Outcome, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<int> ReconcileInFlightAsync(CancellationToken cancellationToken = default)
    {
        var inFlight = await context.Environments
            .AsNoTracking()
            .Where(e => e.Status == EnvironmentStatus.Provisioning || e.Status == EnvironmentStatus.Destroying)
            .ToListAsync(cancellationToken);

        var advanced = 0;
        foreach (var environment in inFlight)
        {
            var run = await context.ProvisioningRuns
                .AsNoTracking()
                .Where(r => r.EnvId == environment.EnvId)
                .OrderByDescending(r => r.DispatchedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (run is null)
            {
                continue;
            }

            if (IsTerminal(run.Outcome))
            {
                // A terminal Plan run legitimately leaves the environment non-terminal (Provisioning/
                // Destroying) while it awaits the owner's confirmation (Article VIII) — do NOT re-emit
                // or it would loop every sweep. Apply/Destroy runs that left the environment stuck DO
                // get re-emitted to unstick them (SC-006).
                if (run.Phase != RunPhase.Plan)
                {
                    await EmitLifecycleAsync(environment.EnvId, run.Phase, run.Outcome, cancellationToken)
                        .ConfigureAwait(false);
                    advanced++;
                }

                continue;
            }

            var observed = await QueryGitHubRunAsync(environment.EnvId, run.Phase, cancellationToken)
                .ConfigureAwait(false);
            if (observed is not null && IsTerminal(observed.Outcome))
            {
                await RecordRunStatusAsync(observed, TrackingSource.Reconciler, cancellationToken)
                    .ConfigureAwait(false);
                advanced++;
            }
        }

        return advanced;
    }

    private async Task EmitLifecycleAsync(
        Guid envId,
        RunPhase phase,
        RunOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome == RunOutcome.Succeeded)
        {
            await bus.PublishAsync(new RunCompleted(envId, phase)).ConfigureAwait(false);
        }
        else
        {
            await bus.PublishAsync(new RunFailed(envId, phase, outcome)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The plan artifact a plan run uploads: <c>plan-&lt;env_id&gt;</c> containing <c>plan.txt</c>
    /// (the human <c>tofu plan</c> output the workflow wrote — contracts/dispatch-and-tracking.md §6).
    /// </summary>
    private const string PlanTextEntry = "plan.txt";

    /// <summary>
    /// Best-effort download of a succeeded plan run's <c>plan.txt</c> artifact into a string for
    /// <see cref="ProvisioningRun.PlanSummary"/>. Returns null on any failure (no run id, missing
    /// artifact, no <c>plan.txt</c> entry, transient GitHub error) — the caller surfaces the run URL
    /// instead and never auto-confirms a missing plan.
    /// </summary>
    private async Task<string?> TryDownloadPlanSummaryAsync(
        Guid envId,
        long gitHubRunId,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await credential.CreateInstallationClientAsync(cancellationToken).ConfigureAwait(false);
            var artifacts = await client.Actions.Artifacts
                .ListWorkflowArtifacts(options.Owner, options.Repository, gitHubRunId)
                .ConfigureAwait(false);

            var artifactName = $"plan-{envId}";
            var artifact = artifacts.Artifacts.FirstOrDefault(a =>
                string.Equals(a.Name, artifactName, StringComparison.Ordinal));
            if (artifact is null)
            {
                return null;
            }

            await using var download = await client.Actions.Artifacts
                .DownloadArtifact(options.Owner, options.Repository, artifact.Id, "zip")
                .ConfigureAwait(false);

            // ZipArchive needs a seekable stream; the artifact download is a forward-only HTTP stream.
            using var buffer = new MemoryStream();
            await download.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            var entry = archive.GetEntry(PlanTextEntry);
            if (entry is null)
            {
                return null;
            }

            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a plan we cannot fetch is surfaced via the run URL, not treated as approved.
            return null;
        }
    }

    /// <summary>
    /// Queries the GitHub Actions API for the run whose <c>run-name</c> matches this environment+phase,
    /// returning a normalized <see cref="WorkflowRunStatus"/> (or null if no run has surfaced yet).
    /// </summary>
    private async Task<WorkflowRunStatus?> QueryGitHubRunAsync(
        Guid envId,
        RunPhase phase,
        CancellationToken cancellationToken)
    {
        var client = await credential.CreateInstallationClientAsync(cancellationToken).ConfigureAwait(false);
        var response = await client.Actions.Workflows.Runs
            .List(options.Owner, options.Repository)
            .ConfigureAwait(false);

        var targetName = RunNameCorrelation.Format(phase, envId);
        var match = response.WorkflowRuns.FirstOrDefault(r =>
            string.Equals(r.Name, targetName, StringComparison.Ordinal));
        if (match is null)
        {
            return null;
        }

        var statusText = match.Status.StringValue;
        if (!string.Equals(statusText, "completed", StringComparison.Ordinal))
        {
            return new WorkflowRunStatus(match.Id, match.Name, match.HtmlUrl, RunOutcome.InProgress);
        }

        return new WorkflowRunStatus(match.Id, match.Name, match.HtmlUrl, MapConclusion(match.Conclusion?.StringValue));
    }

    private static RunOutcome MapConclusion(string? conclusion) => conclusion switch
    {
        "success" => RunOutcome.Succeeded,
        "cancelled" => RunOutcome.Cancelled,
        "timed_out" => RunOutcome.TimedOut,
        // failure / startup_failure / action_required / null → a failed terminal for our purposes.
        _ => RunOutcome.Failed,
    };
}
