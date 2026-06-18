using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Verbs.Model;

/// <summary>
/// The recorded <b>intent</b> for one environment, projected from the registry for the read verbs
/// (FR-014/FR-015). A front-end-agnostic, immutable, <c>System.Text.Json</c>-serializable view of the
/// <c>environments</c> row — decoupled from the EF entity, with the allocated block as a CIDR string.
/// This is intent + lifecycle history; "what is actually deployed?" comes from inventory/ARG (FR-016).
/// </summary>
/// <param name="EnvId">The surrogate correlation key.</param>
/// <param name="Kind">Fabric or spoke.</param>
/// <param name="Subscription">Target subscription (platform subscription for a fabric).</param>
/// <param name="Region">Registered region.</param>
/// <param name="Name">Spoke name; the region for a fabric.</param>
/// <param name="Owner">The requesting principal.</param>
/// <param name="Status">Lifecycle status (intent/history — not a deployment-truth claim).</param>
/// <param name="SpokeCidr">The ledger-allocated block (spoke only), as CIDR text; null for a fabric.</param>
/// <param name="CreatedAt">When the intent was first recorded.</param>
/// <param name="UpdatedAt">When the row was last updated.</param>
public sealed record EnvironmentRecord(
    Guid EnvId,
    EnvironmentKind Kind,
    string Subscription,
    string Region,
    string Name,
    string Owner,
    EnvironmentStatus Status,
    string? SpokeCidr,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a registry <see cref="Environment"/> entity to the front-end-agnostic record.</summary>
    public static EnvironmentRecord From(Registry.Entities.Environment environment) => new(
        environment.EnvId,
        environment.Kind,
        environment.Subscription,
        environment.Region,
        environment.Name,
        environment.Owner,
        environment.Status,
        environment.SpokeCidr?.ToString(),
        environment.CreatedAt,
        environment.UpdatedAt);
}

/// <summary>
/// One dispatched provisioning run, projected from the registry for the audit trail (FR-015). Like
/// <see cref="EnvironmentRecord"/> it is an immutable, serializable view of the
/// <c>provisioning_runs</c> row — with <see cref="DispatchInputs"/> parsed back to a map (so <c>--json</c>
/// is consumable, not a doubly-escaped string).
/// </summary>
/// <param name="RunId">Our surrogate run id.</param>
/// <param name="EnvId">The correlated environment.</param>
/// <param name="Phase">The dispatched phase (plan/apply/destroy).</param>
/// <param name="WorkflowFile">The dispatched workflow file.</param>
/// <param name="DispatchInputs">The exact inputs sent to the workflow.</param>
/// <param name="GitHubRunId">The Actions run id once correlated; null until then.</param>
/// <param name="GitHubRunUrl">Human link to the run; null until correlated.</param>
/// <param name="Outcome">The tracked outcome.</param>
/// <param name="PlanSummary">Captured plan output (plan phase); null otherwise / when unavailable.</param>
/// <param name="DispatchedAt">When the dispatch was sent.</param>
/// <param name="CompletedAt">When the run reached a terminal outcome.</param>
/// <param name="TrackedBy">Which signal recorded the terminal outcome (webhook/reconciler).</param>
public sealed record RunRecord(
    Guid RunId,
    Guid EnvId,
    RunPhase Phase,
    string WorkflowFile,
    IReadOnlyDictionary<string, string> DispatchInputs,
    long? GitHubRunId,
    string? GitHubRunUrl,
    RunOutcome Outcome,
    string? PlanSummary,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt,
    TrackingSource? TrackedBy)
{
    /// <summary>Projects a registry <see cref="ProvisioningRun"/> entity, parsing the jsonb inputs.</summary>
    public static RunRecord From(ProvisioningRun run) => new(
        run.RunId,
        run.EnvId,
        run.Phase,
        run.WorkflowFile,
        ParseInputs(run.DispatchInputs),
        run.GitHubRunId,
        run.GitHubRunUrl,
        run.Outcome,
        run.PlanSummary,
        run.DispatchedAt,
        run.CompletedAt,
        run.TrackedBy);

    private static IReadOnlyDictionary<string, string> ParseInputs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
