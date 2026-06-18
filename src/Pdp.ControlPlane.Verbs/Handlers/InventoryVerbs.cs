using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The inventory verb delegation to the spec-005 <see cref="IInventoryService"/> (FR-013). A pure
/// pass-through: the inventory component is constructed with the control plane's <c>TokenCredential</c>
/// (wired in <see cref="ServiceCollectionExtensions"/>), so no Resource-Graph logic is reimplemented and
/// "what's deployed?" always derives from ARG, never the registry (FR-016).
/// </summary>
public sealed class InventoryVerbs(IInventoryService inventory) : IInventoryVerbs
{
    /// <inheritdoc />
    public Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        inventory.GetSnapshotAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvironmentView>> GetEnvironmentsAsync(CancellationToken cancellationToken = default) =>
        inventory.GetEnvironmentsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SpokeItem>> GetSpokesAsync(CancellationToken cancellationToken = default) =>
        inventory.GetSpokesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<EnvironmentView?> GetEnvironmentAsync(string name, CancellationToken cancellationToken = default) =>
        inventory.GetEnvironmentAsync(name, cancellationToken);
}
