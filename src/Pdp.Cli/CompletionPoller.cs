using Microsoft.Extensions.DependencyInjection;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli;

/// <summary>
/// Drives a foreground CLI invocation's own completion (contracts/cli-surface.md §4, research §1): the
/// command has dispatched; this loop reconciles the run correlation and polls the registry until the
/// environment reaches a terminal status (or a timeout). The long-lived durable reconciler lives in the
/// spec-007 Api host — a one-shot CLI process closes its <i>own</i> loop here rather than depend on it.
/// </summary>
public static class CompletionPoller
{
    private static bool IsTerminal(EnvironmentStatus status) => status is
        EnvironmentStatus.Active or EnvironmentStatus.Failed or EnvironmentStatus.Destroyed;

    private static bool IsRunTerminal(RunOutcome outcome) => outcome is
        RunOutcome.Succeeded or RunOutcome.Failed or RunOutcome.Cancelled or RunOutcome.TimedOut;

    /// <summary>
    /// The outcome of polling a dispatched plan run to terminal: the enriched <see cref="PlanResult"/>
    /// (with the captured plan summary + run URL) and whether the plan run <see cref="Succeeded"/>.
    /// </summary>
    public sealed record PlanCompletion(PlanResult Plan, bool Succeeded);

    /// <summary>
    /// Polls a dispatched plan run to terminal (Article VIII gate), reconciling the GitHub correlation
    /// each pass, and returns the <see cref="PlanResult"/> enriched with the captured plan summary and
    /// run URL. <see cref="PlanCompletion.Succeeded"/> is false if the plan run did not succeed (the
    /// caller must not proceed to apply/destroy). Returns the latest known state on timeout.
    /// </summary>
    public static async Task<PlanCompletion> AwaitPlanAsync(
        IServiceProvider services,
        PlanResult plan,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<IEnvironmentRegistry>();
        var tracker = services.GetRequiredService<IRunTracker>();
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            try
            {
                await tracker.ReconcileInFlightAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Transient GitHub failure — keep polling; the next sweep retries the correlation.
            }

            var latest = (await registry.GetRunsAsync(plan.EnvId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();
            if (latest is { Phase: RunPhase.Plan } && IsRunTerminal(latest.Outcome))
            {
                var enriched = plan with { PlanSummary = latest.PlanSummary, RunUrl = latest.GitHubRunUrl };
                return new PlanCompletion(enriched, latest.Outcome == RunOutcome.Succeeded);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new PlanCompletion(plan with { RunUrl = latest?.GitHubRunUrl }, Succeeded: false);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until the dispatched environment is terminal, refreshing the <see cref="VerbResult"/> with
    /// the final status, outcome, and run URL. Returns the latest known state on timeout.
    /// </summary>
    public static async Task<VerbResult> AwaitTerminalAsync(
        IServiceProvider services,
        VerbResult dispatched,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<IEnvironmentRegistry>();
        var tracker = services.GetRequiredService<IRunTracker>();
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            try
            {
                await tracker.ReconcileInFlightAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Transient GitHub failure — keep polling; the next sweep retries the correlation.
            }

            var environment = await registry.FindByIdAsync(dispatched.EnvId, cancellationToken).ConfigureAwait(false);
            if (environment is not null && IsTerminal(environment.Status))
            {
                var latest = (await registry.GetRunsAsync(dispatched.EnvId, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault();
                return dispatched with
                {
                    Status = environment.Status,
                    RunId = latest?.RunId ?? dispatched.RunId,
                    Outcome = latest?.Outcome ?? dispatched.Outcome,
                    GitHubRunUrl = latest?.GitHubRunUrl ?? dispatched.GitHubRunUrl,
                };
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return environment is null ? dispatched : dispatched with { Status = environment.Status };
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
