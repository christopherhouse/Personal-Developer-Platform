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
