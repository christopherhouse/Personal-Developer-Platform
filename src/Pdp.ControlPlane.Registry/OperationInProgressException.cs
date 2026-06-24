using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// Thrown when a mutating verb targets an environment whose lifecycle is non-terminal (status ∈
/// <c>{Requested, Provisioning, Destroying}</c>) — the <b>single-flight guard</b> (FR-022a). The
/// environment's saga existence + non-terminal status is the authority; a second create/destroy for
/// the same environment is rejected before any allocation or dispatch (data-model §1/§3). Surfaced by
/// the CLI as a clear, non-zero-exit error (contracts/cli-surface.md §3).
/// </summary>
public class OperationInProgressException : Exception
{
    /// <summary>Creates the exception for an environment already running an operation.</summary>
    public OperationInProgressException(Guid envId, EnvironmentStatus status)
        : this(envId, status, $"Environment {envId} has an operation in progress (status '{status}'); " +
               "a new mutating operation is rejected until it reaches a terminal state (FR-022a).")
    {
    }

    /// <summary>
    /// Subclass constructor supplying a <b>more specific</b> message (see <see cref="PlanNotReadyException"/>
    /// / <see cref="PlanFailedException"/>). Keeping these as subclasses means a caller that only cares about
    /// "can't mutate right now" (e.g. the CLI's exit-code mapping) still handles them via the base type,
    /// while a caller that needs the distinction (the MCP adapter) catches the derived type first.
    /// </summary>
    protected OperationInProgressException(Guid envId, EnvironmentStatus status, string message)
        : base(message)
    {
        EnvId = envId;
        Status = status;
    }

    /// <summary>The environment whose in-flight operation blocked the request.</summary>
    public Guid EnvId { get; }

    /// <summary>The non-terminal status that triggered the single-flight rejection.</summary>
    public EnvironmentStatus Status { get; }
}
