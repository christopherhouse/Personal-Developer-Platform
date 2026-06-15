using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// The IPAM ledger's operation surface (contracts/ipam-operations.md). Every mutating operation
/// is transactional and serializes per pool with a <c>pg_advisory_xact_lock</c>; the database
/// GiST exclusion constraint is the absolute non-overlap backstop (FR-006, FR-008). Consumed by
/// the spec-006 verbs and specs 003/004 without modifying the schema or addressing scheme
/// (FR-017). <c>query</c>/<c>query_all</c> are read-only and report current utilization.
/// </summary>
public interface IIpamLedger
{
    /// <summary>
    /// Registers a region's <c>/16</c> supernet (<c>10.&lt;index&gt;.0.0/16</c>) and reserves its
    /// hub <c>/22</c> carve-out (<c>10.&lt;index&gt;.252.0/22</c>) in one step. Idempotent: a
    /// repeat with the same <paramref name="regionIndex"/> returns the existing pool unchanged.
    /// </summary>
    /// <exception cref="InvalidRegionIndexException">Index outside 1–255 (0 is the platform pool).</exception>
    /// <exception cref="RegionAlreadyExistsException">Region already registered under a different index.</exception>
    /// <exception cref="SupernetOverlapException">The derived supernet overlaps an existing region (FR-011).</exception>
    Task<RegionPool> RegisterRegionAsync(
        string region,
        int regionIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves the lowest free aligned block of <paramref name="prefixLength"/> from the
    /// region's pool, records it durably, and returns it. Idempotent on
    /// <paramref name="name"/>: a repeat with the same name returns the existing block (FR-010).
    /// </summary>
    /// <exception cref="RegionNotRegisteredException">No pool exists for the region (FR-012).</exception>
    /// <exception cref="InvalidPrefixLengthException">Prefix length outside <c>/29</c>–<c>/22</c>.</exception>
    /// <exception cref="AllocationNameConflictException">Name reused for a different size (FR-010).</exception>
    /// <exception cref="PoolExhaustedException">No free aligned block of that size remains (FR-007).</exception>
    Task<Allocation> AllocateAsync(
        string region,
        string name,
        int prefixLength = Ledger.DefaultPrefixLength,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a previously allocated block to its pool, making the space immediately reusable.
    /// Idempotent: releasing an unknown/already-released name is a clean no-op (FR-009).
    /// </summary>
    /// <exception cref="RegionNotRegisteredException">No pool exists for the region.</exception>
    /// <exception cref="CannotReleaseReservationException">The block is a non-releasable reservation.</exception>
    Task ReleaseAsync(string region, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a region's utilization without touching any deployed network: its supernet, hub
    /// carve-out, every live allocation, and the free space remaining (read-only; no lock — SC-005).
    /// </summary>
    /// <exception cref="RegionNotRegisteredException">No pool exists for the region (FR-012).</exception>
    Task<RegionView> QueryAsync(string region, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports utilization for every registered region — including the platform-shared pool and its
    /// <c>control-plane-vnet</c> reservation — ordered by region index (read-only; no lock).
    /// </summary>
    Task<IReadOnlyList<RegionView>> QueryAllAsync(CancellationToken cancellationToken = default);
}
