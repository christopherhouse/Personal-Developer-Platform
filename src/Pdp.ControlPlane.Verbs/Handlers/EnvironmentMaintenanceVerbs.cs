using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.ControlPlane.Verbs.PlanConfirm;
using Pdp.ControlPlane.Verbs.Telemetry;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The environment-maintenance recovery verbs (issue #48). Pure registry — it never runs OpenTofu, never
/// dispatches a workflow, and never touches the IPAM ledger; it only clears a stuck single-flight guard
/// (FR-022a) so the owner can re-plan a destroy/create through the sanctioned path. The Article VIII gate
/// is enforced exactly as for a destroy: <see cref="ResetAsync"/> refuses without a matching restatement
/// of the target name (<see cref="ConfirmationGuard"/>) before mutating anything.
/// </summary>
public sealed class EnvironmentMaintenanceVerbs(IEnvironmentRegistry registry) : IEnvironmentMaintenanceVerbs
{
    private static readonly EnvironmentStatus[] NonTerminal =
        [EnvironmentStatus.Requested, EnvironmentStatus.Provisioning, EnvironmentStatus.Destroying];

    private static bool IsNonTerminal(EnvironmentStatus status) => NonTerminal.Contains(status);

    /// <inheritdoc />
    public async Task<ResetPreview> PlanResetAsync(EnvRef environment, CancellationToken cancellationToken = default)
    {
        var env = await ResolveAsync(environment, cancellationToken).ConfigureAwait(false);
        if (!IsNonTerminal(env.Status))
        {
            throw new EnvironmentNotWedgedException(env.EnvId, env.Status);
        }

        var latest = (await registry.GetRunsAsync(env.EnvId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();

        var notes = new List<string>
        {
            $"Environment {env.EnvId} ('{env.Name}', {env.Kind}) is wedged in status '{env.Status}'.",
            "Confirming the reset forces it to 'Failed', releasing the single-flight guard (FR-022a).",
            "This changes REGISTRY STATUS ONLY — no Azure resource is touched and no IPAM allocation is " +
            "released. Tear the resources down afterward with the normal destroy verb.",
        };
        if (latest is not null)
        {
            notes.Add(
                $"Latest run: {latest.Phase} → {latest.Outcome}" +
                (latest.DispatchedAt is { } at ? $", dispatched {at:u}" : string.Empty) +
                ". Confirm it is truly stranded (not still executing) before resetting.");
        }

        return new ResetPreview(
            env.EnvId,
            env.Kind,
            env.Name,
            env.Status,
            latest is null ? null : RunRecord.From(latest),
            notes);
    }

    /// <inheritdoc />
    public async Task<VerbResult> ResetAsync(
        EnvRef environment,
        Confirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        var env = await ResolveAsync(environment, cancellationToken).ConfigureAwait(false);

        // Article VIII / FR-007: reset confirmation is mandatory and unbypassable — the owner must restate
        // the exact target name. Rejected BEFORE any mutation.
        ConfirmationGuard.RequireMatch(confirmation, env.Name);

        using var activity = ControlPlaneTelemetry.StartVerb("env.reset", env.EnvId);

        var prior = await registry.ForceTerminalAsync(env.EnvId, cancellationToken).ConfigureAwait(false);
        if (prior is null)
        {
            // Already terminal (idempotent): nothing was wedged — the reconciler may have advanced it, or a
            // prior reset already cleared it. Report the current status without pretending we changed it.
            return new VerbResult(
                env.EnvId,
                env.Status,
                RunId: null,
                GitHubRunUrl: null,
                Outcome: null,
                new[] { $"Environment '{env.Name}' is already terminal ('{env.Status}'); nothing to reset." });
        }

        return new VerbResult(
            env.EnvId,
            EnvironmentStatus.Failed,
            RunId: null,
            GitHubRunUrl: null,
            Outcome: null,
            new[]
            {
                $"Reset '{env.Name}' from '{prior}' to 'Failed'; the single-flight guard is released (FR-022a).",
                "No Azure resource was touched and no IPAM allocation was released — destroy the surviving " +
                "resources with the normal destroy verb.",
            });
    }

    private async Task<Environment> ResolveAsync(EnvRef reference, CancellationToken cancellationToken)
    {
        var env = reference.EnvId is { } id
            ? await registry.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : reference.HasNaturalKey
                ? await registry
                    .FindByNaturalKeyAsync(reference.Kind!.Value, reference.Subscription!, reference.Name!, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        return env ?? throw new EnvironmentNotFoundException(reference);
    }
}
