using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs;

/// <summary>
/// Classifies why a confirm-branch apply/destroy cannot proceed (FR-019), so the conversational surface can
/// tell "your plan isn't finished yet" apart from "a real operation is already in flight." Pure
/// presentation/sequencing — it does <b>not</b> change the single-flight invariant (a genuine concurrent
/// mutation still yields <see cref="OperationInProgressException"/>).
/// </summary>
internal static class PlanGate
{
    /// <summary>
    /// Maps the most-recent run of a non-terminal environment to the rejection an apply/destroy should throw:
    /// a Plan run still in flight → <see cref="PlanNotReadyException"/>; a failed Plan run →
    /// <see cref="PlanFailedException"/>; anything else (a real apply/destroy already dispatched) →
    /// <see cref="OperationInProgressException"/>. Callers handle the succeeded-plan case before calling this.
    /// </summary>
    public static Exception RejectionFor(Guid envId, EnvironmentStatus status, ProvisioningRun? latest) =>
        latest is { Phase: RunPhase.Plan }
            ? latest.Outcome switch
            {
                RunOutcome.Dispatched or RunOutcome.InProgress => new PlanNotReadyException(envId, status, latest.Phase),
                RunOutcome.Failed or RunOutcome.Cancelled or RunOutcome.TimedOut => new PlanFailedException(envId, status, latest.Outcome),
                _ => new OperationInProgressException(envId, status),
            }
            : new OperationInProgressException(envId, status);
}

/// <summary>
/// A destroy (or other gated mutation) was requested without the explicit, matching confirmation the
/// constitution mandates (Article VIII / FR-007). The verb layer throws this <b>before any dispatch</b>,
/// so a missing or mismatched confirmation can never reach the execution plane. Unbypassable: there is
/// no <c>--yes</c> shortcut for destroy.
/// </summary>
public sealed class ConfirmationRequiredException : Exception
{
    /// <summary>Creates the exception describing the target whose confirmation was missing/mismatched.</summary>
    public ConfirmationRequiredException(string target)
        : base($"This operation requires an explicit confirmation restating the target '{target}' (Article VIII / FR-007). No mutation was dispatched.")
    {
        Target = target;
    }

    /// <summary>The target whose confirmation was required.</summary>
    public string Target { get; }
}

/// <summary>
/// A recovery reset was requested for an environment that is <b>already in a terminal state</b> — there
/// is nothing to unwedge (issue #48). Reset exists only to clear a single-flight guard (FR-022a) left
/// stuck by a run that never recorded terminal; a terminal environment is already free for the normal
/// plan/create/destroy path, so the verb rejects fail-fast before mutating anything.
/// </summary>
public sealed class EnvironmentNotWedgedException : Exception
{
    /// <summary>Creates the exception for an environment that is already terminal.</summary>
    public EnvironmentNotWedgedException(Guid envId, EnvironmentStatus status)
        : base($"Environment {envId} is in a terminal state ('{status}'); there is nothing to reset. " +
               "Reset only unwedges an environment stuck non-terminal (Requested/Provisioning/Destroying) " +
               "whose run never completed (issue #48).")
    {
        EnvId = envId;
        Status = status;
    }

    /// <summary>The already-terminal environment.</summary>
    public Guid EnvId { get; }

    /// <summary>The terminal status that means there is nothing to reset.</summary>
    public EnvironmentStatus Status { get; }
}

/// <summary>
/// A verb referenced an environment that the registry does not know — the operation cannot proceed
/// (fail-fast, no dispatch — FR-023).
/// </summary>
public sealed class EnvironmentNotFoundException : Exception
{
    /// <summary>Creates the exception for an unresolved <see cref="EnvRef"/>.</summary>
    public EnvironmentNotFoundException(EnvRef reference)
        : base(Describe(reference))
    {
        Reference = reference;
    }

    /// <summary>The reference that did not resolve.</summary>
    public EnvRef Reference { get; }

    private static string Describe(EnvRef reference) =>
        reference.EnvId is { } id
            ? $"No environment with env_id '{id}' is registered."
            : $"No environment '{reference.Name}' ({reference.Kind}) is registered in subscription '{reference.Subscription}'.";
}

/// <summary>
/// One caller parameter's failure against the archetype's JSON schema (spec 008, FR-003) — the
/// schema-derived shape JsonSchema.Net's <c>OutputFormat.List</c> yields, surfaced verbatim by the
/// CLI (one <c>&lt;path&gt;: &lt;message&gt;</c> line per violation, exit 2) and the MCP tool (as data,
/// no confirmation token). Immutable; System.Text.Json serializable.
/// </summary>
/// <param name="Path">JSON Pointer to the offending parameter (<c>(root)</c> for document-level failures).</param>
/// <param name="Keyword">The schema keyword that failed (e.g. <c>enum</c>, <c>required</c>, <c>maximum</c>).</param>
/// <param name="Message">The evaluator's human-readable constraint message.</param>
public sealed record ParameterViolation(string Path, string Keyword, string Message);

/// <summary>
/// The caller's parameters failed the archetype version's JSON schema (spec 008, FR-003/SC-003).
/// Thrown <b>before</b> any intent row or dispatch, so an invalid deploy never reaches the execution
/// plane. Carries every violation so one rejection names everything wrong at once.
/// </summary>
public sealed class WorkloadParameterValidationException : Exception
{
    /// <summary>Creates the exception for parameters that failed <paramref name="archetype"/>'s schema.</summary>
    public WorkloadParameterValidationException(
        string archetype,
        string version,
        IReadOnlyList<ParameterViolation> violations)
        : base($"Parameters do not satisfy archetype '{archetype}' {version}: " +
               string.Join("; ", violations.Select(v => $"{v.Path}: {v.Message}")) +
               ". Nothing was recorded or dispatched.")
    {
        Archetype = archetype;
        Version = version;
        Violations = violations;
    }

    /// <summary>The archetype whose schema rejected the parameters.</summary>
    public string Archetype { get; }

    /// <summary>The resolved archetype version whose schema was evaluated.</summary>
    public string Version { get; }

    /// <summary>Every schema violation, one per offending parameter/constraint.</summary>
    public IReadOnlyList<ParameterViolation> Violations { get; }
}

/// <summary>
/// The requested archetype cannot be deployed: it is either <b>unknown</b> to the catalog or
/// <b>retired</b> (no new deploys; existing workloads unaffected — FR-002/FR-004, US4-AS2). The two
/// cases carry distinct messages so the owner knows whether to fix a typo or pick a successor.
/// Thrown before any intent or dispatch.
/// </summary>
public sealed class ArchetypeNotDeployableException : Exception
{
    private ArchetypeNotDeployableException(string archetype, bool isRetired, string message)
        : base(message)
    {
        Archetype = archetype;
        IsRetired = isRetired;
    }

    /// <summary>The archetype that cannot be deployed.</summary>
    public string Archetype { get; }

    /// <summary>True when the archetype exists but is retired; false when it is unknown.</summary>
    public bool IsRetired { get; }

    /// <summary>The archetype is not in the catalog at all.</summary>
    public static ArchetypeNotDeployableException Unknown(string archetype) =>
        new(archetype, isRetired: false,
            $"Archetype '{archetype}' is not in the catalog. Nothing was recorded or dispatched.");

    /// <summary>The archetype is retired — no new deploys (existing workloads are unaffected).</summary>
    public static ArchetypeNotDeployableException Retired(string archetype) =>
        new(archetype, isRetired: true,
            $"Archetype '{archetype}' is retired: new deploys are refused; existing workloads are " +
            "unaffected. Nothing was recorded or dispatched.");
}

/// <summary>
/// A repeat deploy of an <b>existing</b> workload presented different parameters (or a different
/// archetype). In-place reconfiguration is out of scope for spec 008 — the supported path is
/// destroy → deploy; identical parameters follow the idempotent-convergence behavior instead
/// (contracts/workload-verbs.md §repeat-deploy).
/// </summary>
public sealed class WorkloadParametersChangedException : Exception
{
    /// <summary>Creates the exception for a changed repeat deploy of <paramref name="workloadName"/>.</summary>
    public WorkloadParametersChangedException(string workloadName)
        : base($"Workload '{workloadName}' already exists with different parameters (or a different " +
               "archetype). In-place reconfiguration is not supported — destroy the workload, then " +
               "deploy the new configuration. Nothing was dispatched.")
    {
        WorkloadName = workloadName;
    }

    /// <summary>The existing workload whose configuration differs from the request.</summary>
    public string WorkloadName { get; }
}

/// <summary>
/// The target spoke exists but is not <c>Active</c>, so a workload cannot be deployed into it
/// (spec 008, US1-AS5). A missing spoke is <see cref="EnvironmentNotFoundException"/> instead; both
/// are thrown before any intent or dispatch.
/// </summary>
public sealed class SpokeNotActiveException : Exception
{
    /// <summary>Creates the exception for a spoke that is not ready to receive workloads.</summary>
    public SpokeNotActiveException(string spokeName, EnvironmentStatus status)
        : base($"Spoke '{spokeName}' is not Active (current status: {status}); workloads deploy only " +
               "into Active spokes. Nothing was recorded or dispatched.")
    {
        SpokeName = spokeName;
        Status = status;
    }

    /// <summary>The spoke that is not active.</summary>
    public string SpokeName { get; }

    /// <summary>The spoke's current lifecycle status.</summary>
    public EnvironmentStatus Status { get; }
}
