using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// A gated apply/destroy was requested while its <b>plan run has not yet reached a successful terminal
/// outcome</b> — the plan is still in flight (FR-019). Distinct from <see cref="OperationInProgressException"/>
/// (a genuine concurrent mutating operation): this means "your plan isn't finished yet," not "something else
/// is already mutating." The owner should observe the plan via a status read (which reconciles it) and retry
/// the apply once it has succeeded. A subclass of <see cref="OperationInProgressException"/> so callers that
/// only branch on "cannot mutate now" still handle it, while the MCP adapter catches it specifically.
/// </summary>
public sealed class PlanNotReadyException : OperationInProgressException
{
    /// <summary>Creates the exception for an environment whose plan run is still in flight.</summary>
    public PlanNotReadyException(Guid envId, EnvironmentStatus status, RunPhase phase)
        : base(envId, status,
               $"The plan for environment {envId} has not finished yet (its {phase} run is still in flight). " +
               "Check it with a status read and retry the apply once the plan has succeeded — no mutation was dispatched (FR-019).")
    {
        Phase = phase;
    }

    /// <summary>The phase of the in-flight plan run.</summary>
    public RunPhase Phase { get; }
}

/// <summary>
/// A gated apply/destroy was requested but its <b>plan run did not succeed</b> (failed/cancelled/timed out)
/// — there is nothing to apply (FR-019). The owner must re-plan. A subclass of
/// <see cref="OperationInProgressException"/> for the same reason as <see cref="PlanNotReadyException"/>.
/// </summary>
public sealed class PlanFailedException : OperationInProgressException
{
    /// <summary>Creates the exception for an environment whose plan run reached a non-success terminal.</summary>
    public PlanFailedException(Guid envId, EnvironmentStatus status, RunOutcome outcome)
        : base(envId, status,
               $"The plan for environment {envId} did not succeed (outcome '{outcome}'); there is nothing to apply. " +
               "Re-plan and try again (FR-019).")
    {
        PlanOutcome = outcome;
    }

    /// <summary>The non-success terminal outcome of the plan run.</summary>
    public RunOutcome PlanOutcome { get; }
}
