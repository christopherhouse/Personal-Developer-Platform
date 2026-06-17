using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory;

/// <summary>
/// The inventory read surface (contracts §1) the spec-006/007 verb/CLI/MCP layers will wrap. Every
/// method is read-only, derives everything from Azure Resource Graph (FR-002 — never local records,
/// the IPAM ledger, or OpenTofu state), and always succeeds in the face of drift (FR-016). A freshly
/// vended fabric/spoke/workload appears with no code change (FR-009) — the surface is purely
/// tag/Graph-driven.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// The full live sweep: discover subscriptions → query ARG → classify → group → drift. The result
    /// is a typed object graph (FR-010); inaccessible subscriptions appear in
    /// <see cref="InventorySnapshot.Coverage"/>, never silently dropped (FR-011 / SC-008). US1.
    /// </summary>
    Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The deployed environments (workloads grouped by <c>pdp-env</c>) — FR-005/007. US2.</summary>
    Task<IReadOnlyList<EnvironmentView>> GetEnvironmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every spoke with its subscription and region — FR-007. US2.</summary>
    Task<IReadOnlyList<SpokeItem>> GetSpokesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The contents of a named environment, or <c>null</c> when no such environment exists — a clean
    /// empty result, not an error (FR-008). US2.
    /// </summary>
    Task<EnvironmentView?> GetEnvironmentAsync(string name, CancellationToken cancellationToken = default);
}
