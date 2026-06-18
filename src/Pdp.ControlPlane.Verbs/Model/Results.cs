using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Verbs.Model;

/// <summary>
/// The result of a <c>Plan*</c> verb — surfaced to the owner <b>before</b> any apply/destroy
/// (Article VIII / FR-006). Carries the captured <see cref="PlanSummary"/> (downloaded from the
/// plan run's artifact — contracts/dispatch-and-tracking.md §6) and the exact inputs that the
/// confirmed apply/destroy would dispatch. Immutable; System.Text.Json serializable.
/// </summary>
/// <param name="EnvId">The environment the plan pertains to.</param>
/// <param name="Phase">Always <see cref="RunPhase.Plan"/>.</param>
/// <param name="PlanSummary">The captured <c>tofu plan</c> output, or null if it must be reviewed in GitHub.</param>
/// <param name="ProposedInputs">The dispatch inputs the confirmed mutation would send.</param>
/// <param name="RunUrl">Link to the plan run (for review when the summary is unavailable).</param>
public sealed record PlanResult(
    Guid EnvId,
    RunPhase Phase,
    string? PlanSummary,
    IReadOnlyDictionary<string, string> ProposedInputs,
    string? RunUrl);

/// <summary>
/// The typed outcome every mutating verb returns — rendered human-readable or as <c>--json</c>
/// (SC-008). Reports the environment's lifecycle <see cref="Status"/> and, when a run was dispatched,
/// its tracking handles. Immutable; System.Text.Json serializable.
/// </summary>
/// <param name="EnvId">The environment surrogate (correlation key).</param>
/// <param name="Status">The environment's lifecycle status after the verb ran.</param>
/// <param name="RunId">The dispatched provisioning-run id, if any.</param>
/// <param name="GitHubRunUrl">Link to the dispatched GitHub Actions run, once correlated.</param>
/// <param name="Outcome">The tracked run outcome, if a run was dispatched and tracked.</param>
/// <param name="Messages">Human-facing notes (e.g. allocated CIDR, single-flight rejection reason).</param>
public sealed record VerbResult(
    Guid EnvId,
    EnvironmentStatus Status,
    Guid? RunId,
    string? GitHubRunUrl,
    RunOutcome? Outcome,
    IReadOnlyList<string> Messages);
