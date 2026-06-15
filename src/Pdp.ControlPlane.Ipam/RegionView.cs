using System.Net;
using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// A read-only snapshot of one region pool's utilization (contracts/ipam-operations.md
/// <c>query</c>): the supernet, the reserved hub carve-out, every live allocation, and the free
/// space remaining. Produced without any lock — <c>query</c> reports current truth and never
/// touches a deployed network (SC-005). Consumed by spec 006 verbs and specs 003/004.
/// </summary>
/// <param name="Region">Region identifier (e.g. <c>eastus2</c>; <c>platform</c> for index 0).</param>
/// <param name="RegionIndex">Second octet of the supernet: 0 = platform-shared, 1–255 = geographic.</param>
/// <param name="Supernet">The region's <c>/16</c> supernet.</param>
/// <param name="HubCarveout">The reserved hub <c>/22</c> carve-out; <c>null</c> for the platform pool.</param>
/// <param name="Allocations">Every live allocation in the pool, in ascending address order.</param>
/// <param name="Free">The space remaining after the carve-out and live allocations.</param>
public sealed record RegionView(
    string Region,
    short RegionIndex,
    IPNetwork Supernet,
    IPNetwork? HubCarveout,
    IReadOnlyList<AllocationView> Allocations,
    FreeSpace Free);

/// <summary>
/// A single live allocation as reported by <c>query</c> — the caller-facing projection of an
/// <see cref="Allocation"/> row (name, network, size, kind, and when it was allocated).
/// </summary>
/// <param name="Name">Caller-supplied allocation name (e.g. the spoke name).</param>
/// <param name="Network">The allocated block.</param>
/// <param name="PrefixLength">The block size (prefix length).</param>
/// <param name="Kind">Whether the block is a releasable spoke or a non-releasable reservation.</param>
/// <param name="AllocatedAt">When the block was allocated.</param>
public sealed record AllocationView(
    string Name,
    IPNetwork Network,
    short PrefixLength,
    AllocationKind Kind,
    DateTimeOffset AllocatedAt);

/// <summary>
/// The free space remaining in a pool: the pool's supernet minus its hub carve-out minus every
/// live allocation, expressed both as a total free-address count and as the maximal aligned CIDR
/// blocks that remain allocatable (the sizes of <see cref="Blocks"/> sum to <see cref="AddressCount"/>).
/// </summary>
/// <param name="AddressCount">Total number of free addresses remaining.</param>
/// <param name="Blocks">The maximal aligned free CIDR blocks, in ascending address order.</param>
public sealed record FreeSpace(
    ulong AddressCount,
    IReadOnlyList<IPNetwork> Blocks);
