using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry.Lifecycle;

namespace Pdp.ControlPlane.Verbs.Lifecycle;

/// <summary>
/// The cascaded side-effect handlers the lifecycle saga emits through the durable outbox. They live in
/// the verb layer because they hold the execution-plane (<see cref="IWorkflowDispatcher"/>) and IPAM
/// (<see cref="IIpamLedger"/>) references the registry assembly must not depend on. Both run as durable
/// Wolverine messages, so a transient GitHub or ledger failure is retried rather than lost (FR-011).
/// </summary>
public static class DispatchWorkflowHandler
{
    /// <summary>Performs the actual <c>workflow_dispatch</c> for a cascaded dispatch command.</summary>
    public static async Task Handle(
        DispatchWorkflowCommand command,
        IWorkflowDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var dispatch = new WorkflowDispatch(
            command.WorkflowFile,
            command.GitRef,
            command.EnvId,
            command.Mode,
            command.Inputs);

        await dispatcher.DispatchAsync(dispatch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Releases an environment's IPAM allocation when the saga compensates a failed create (FR-011).</summary>
public static class ReleaseAllocationHandler
{
    /// <summary>Returns the allocation to its pool (idempotent — releasing an unknown name is a no-op).</summary>
    public static async Task Handle(
        ReleaseAllocationCommand command,
        IIpamLedger ledger,
        CancellationToken cancellationToken)
    {
        await ledger.ReleaseAsync(command.Region, command.Name, cancellationToken).ConfigureAwait(false);
    }
}
