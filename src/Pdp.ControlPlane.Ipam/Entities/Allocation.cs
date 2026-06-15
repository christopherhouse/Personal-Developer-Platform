using System.Net;

namespace Pdp.ControlPlane.Ipam.Entities;

/// <summary>
/// One live block handed out from a pool. The non-overlap invariant across allocations in a
/// pool lives in the database (the <c>allocations_no_overlap</c> GiST exclusion constraint),
/// never in application code. See data-model.md §3.
/// </summary>
public class Allocation
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning pool.</summary>
    public Guid PoolId { get; set; }

    /// <summary>
    /// Caller-supplied name (e.g. spoke name), unique within the pool — the idempotency key
    /// for <c>allocate</c>/<c>release</c> (data-model §3).
    /// </summary>
    public required string Name { get; set; }

    /// <summary>The allocated block (e.g. <c>10.1.0.0/24</c>); native <c>cidr</c>.</summary>
    public required IPNetwork Network { get; set; }

    /// <summary>Requested size; permitted <c>/29</c>–<c>/22</c> (validated before insert).</summary>
    public short PrefixLength { get; set; }

    /// <summary>Whether this block is a releasable spoke or a non-releasable reservation.</summary>
    public AllocationKind Kind { get; set; }

    /// <summary>Audit: when the block was allocated.</summary>
    public DateTimeOffset AllocatedAt { get; set; }

    /// <summary>Navigation to the owning pool.</summary>
    public RegionPool? Pool { get; set; }
}
