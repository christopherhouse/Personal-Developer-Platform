using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The inventory / environment verbs (spec 005 reused — FR-013), the answer to "what's deployed?". They
/// delegate to <c>Pdp.ControlPlane.Inventory</c> over Azure Resource Graph with the control plane's
/// injected <c>TokenCredential</c> — <b>no inventory logic is duplicated</b> (contracts/verb-surface.md
/// §4). Deployed-state queries route here (ARG), never to the intent registry (division of truth —
/// FR-016).
/// </summary>
public interface IInventoryVerbs
{
    /// <summary>The full live sweep: every managed resource group classified, grouped, and drift-checked.</summary>
    Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The deployed environments (workloads grouped by <c>pdp-env</c>).</summary>
    Task<IReadOnlyList<EnvironmentView>> GetEnvironmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every spoke with its subscription and region.</summary>
    Task<IReadOnlyList<SpokeItem>> GetSpokesAsync(CancellationToken cancellationToken = default);

    /// <summary>The contents of a named environment, or null when no such environment exists.</summary>
    Task<EnvironmentView?> GetEnvironmentAsync(string name, CancellationToken cancellationToken = default);
}
