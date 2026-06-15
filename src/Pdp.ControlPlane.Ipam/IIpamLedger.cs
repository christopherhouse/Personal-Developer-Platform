using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// The IPAM ledger's operation surface (contracts/ipam-operations.md). Every mutating operation
/// is transactional and serializes per pool with a <c>pg_advisory_xact_lock</c>; the database
/// GiST exclusion constraint is the absolute non-overlap backstop (FR-006, FR-008). Consumed by
/// the spec-006 verbs and specs 003/004 without modifying the schema or addressing scheme
/// (FR-017). <c>register_region</c> and <c>query</c> land in later phases.
/// </summary>
public interface IIpamLedger
{
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
}
