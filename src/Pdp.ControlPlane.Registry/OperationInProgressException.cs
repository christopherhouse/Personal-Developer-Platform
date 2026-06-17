using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// Thrown when a mutating verb targets an environment whose lifecycle is non-terminal (status ∈
/// <c>{Requested, Provisioning, Destroying}</c>) — the <b>single-flight guard</b> (FR-022a). The
/// environment's saga existence + non-terminal status is the authority; a second create/destroy for
/// the same environment is rejected before any allocation or dispatch (data-model §1/§3). Surfaced by
/// the CLI as a clear, non-zero-exit error (contracts/cli-surface.md §3).
/// </summary>
public sealed class OperationInProgressException : Exception
{
    /// <summary>Creates the exception for an environment already running an operation.</summary>
    public OperationInProgressException(Guid envId, EnvironmentStatus status)
        : base($"Environment {envId} has an operation in progress (status '{status}'); " +
               "a new mutating operation is rejected until it reaches a terminal state (FR-022a).")
    {
        EnvId = envId;
        Status = status;
    }

    /// <summary>The environment whose in-flight operation blocked the request.</summary>
    public Guid EnvId { get; }

    /// <summary>The non-terminal status that triggered the single-flight rejection.</summary>
    public EnvironmentStatus Status { get; }
}
