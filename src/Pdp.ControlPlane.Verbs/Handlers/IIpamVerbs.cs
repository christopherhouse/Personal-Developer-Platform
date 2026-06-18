using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The IPAM verbs (spec 002 wrapped) — a thin pass-through to the <see cref="IIpamLedger"/>
/// (contracts/verb-surface.md §3). Every CIDR the control plane hands to a workflow originates here
/// (Article VI / FR-010); the read verbs (<see cref="QueryAsync"/>/<see cref="QueryAllAsync"/>) stay
/// available even while a mutating run is in flight. No address logic is reimplemented — the ledger and
/// its GiST non-overlap backstop remain the single authority for address space.
/// </summary>
public interface IIpamVerbs
{
    /// <summary>Reserves the lowest free aligned block of <paramref name="size"/> from the region pool.</summary>
    Task<Allocation> AllocateAsync(string region, string name, int size = 24, CancellationToken cancellationToken = default);

    /// <summary>Returns a previously allocated block to its pool (idempotent).</summary>
    Task ReleaseAsync(string region, string name, CancellationToken cancellationToken = default);

    /// <summary>Reports one region's utilization (read-only; no lock).</summary>
    Task<RegionView> QueryAsync(string region, CancellationToken cancellationToken = default);

    /// <summary>Reports utilization for every registered region, ordered by region index.</summary>
    Task<IReadOnlyList<RegionView>> QueryAllAsync(CancellationToken cancellationToken = default);
}
