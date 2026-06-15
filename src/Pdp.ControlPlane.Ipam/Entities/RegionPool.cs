using System.Net;

namespace Pdp.ControlPlane.Ipam.Entities;

/// <summary>
/// One registered region's address pool: the <c>/16</c> supernet from which allocations are
/// carved, plus its reserved hub <c>/22</c> carve-out. The platform-shared supernet
/// (<c>region_index = 0</c>) is one of these too. See data-model.md §2.
/// </summary>
public class RegionPool
{
    /// <summary>Surrogate key; also the basis for the per-pool advisory-lock key (spec 002 §6).</summary>
    public Guid Id { get; set; }

    /// <summary>Region identifier, e.g. <c>eastus2</c>; <c>platform</c> for index 0. Unique.</summary>
    public required string Region { get; set; }

    /// <summary>
    /// Second octet of the supernet: <c>0</c> = platform-shared, <c>1</c>–<c>255</c> = geographic.
    /// Unique.
    /// </summary>
    public short RegionIndex { get; set; }

    /// <summary>The region's <c>/16</c> (e.g. <c>10.1.0.0/16</c>); native <c>cidr</c>.</summary>
    public required IPNetwork Supernet { get; set; }

    /// <summary>
    /// The reserved hub <c>/22</c> (<c>10.R.252.0/22</c>); <c>null</c> for the platform pool.
    /// Recorded here (not as an allocation) so it can never be released or double-issued.
    /// </summary>
    public IPNetwork? HubCarveout { get; set; }

    /// <summary>Audit: when the region was registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Live allocations carved from this pool.</summary>
    public ICollection<Allocation> Allocations { get; } = [];
}
