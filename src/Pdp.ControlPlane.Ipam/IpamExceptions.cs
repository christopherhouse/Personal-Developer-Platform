namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// Base type for the IPAM ledger's named operation failures (contracts/ipam-operations.md).
/// Operations surface these so callers (spec 006 verbs, 003/004) can react to a specific
/// failure rather than parse messages. Distinct from a <c>DbUpdateException</c> raised by the
/// database backstop, which only ever fires when application-level checks are bypassed.
/// </summary>
public abstract class IpamException : Exception
{
    /// <summary>Creates the exception with a human-readable message.</summary>
    protected IpamException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping an underlying cause (e.g. the GiST backstop).</summary>
    protected IpamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// No pool exists for the requested region — address space is never assigned outside a
/// registered pool (Article VI, FR-012).
/// </summary>
public sealed class RegionNotRegisteredException(string region)
    : IpamException($"Region '{region}' is not registered; no address space can be allocated outside a registered pool (Article VI, FR-012).")
{
    /// <summary>The region that has no pool.</summary>
    public string Region { get; } = region;
}

/// <summary>
/// No free aligned block of the requested size remains in the region's pool. Nothing is
/// recorded (FR-007).
/// </summary>
public sealed class PoolExhaustedException(string region, int prefixLength)
    : IpamException($"No free /{prefixLength} block remains in region '{region}' (FR-007).")
{
    /// <summary>The region whose pool is exhausted.</summary>
    public string Region { get; } = region;

    /// <summary>The requested block size that could not be satisfied.</summary>
    public int PrefixLength { get; } = prefixLength;
}

/// <summary>
/// A live allocation already holds the requested name but with a different size/intent, so the
/// request cannot be satisfied idempotently (FR-010).
/// </summary>
public sealed class AllocationNameConflictException(string region, string name, int existingPrefixLength, int requestedPrefixLength)
    : IpamException($"Allocation '{name}' already exists in region '{region}' as a /{existingPrefixLength}; cannot satisfy a /{requestedPrefixLength} request for the same name (FR-010).")
{
    /// <summary>The region holding the conflicting allocation.</summary>
    public string Region { get; } = region;

    /// <summary>The contended allocation name.</summary>
    public string Name { get; } = name;

    /// <summary>The size of the existing allocation under that name.</summary>
    public int ExistingPrefixLength { get; } = existingPrefixLength;

    /// <summary>The size the conflicting request asked for.</summary>
    public int RequestedPrefixLength { get; } = requestedPrefixLength;
}

/// <summary>
/// The requested prefix length is outside the permitted spoke range (<c>/29</c>–<c>/22</c>).
/// </summary>
public sealed class InvalidPrefixLengthException(int prefixLength)
    : IpamException($"Prefix length /{prefixLength} is outside the permitted spoke range /22–/29.")
{
    /// <summary>The rejected prefix length.</summary>
    public int PrefixLength { get; } = prefixLength;
}

/// <summary>
/// An attempt to release a <see cref="Entities.AllocationKind.Reservation"/> (e.g. the
/// control-plane VNet) — reservations are non-releasable (contracts/ipam-operations.md).
/// </summary>
public sealed class CannotReleaseReservationException(string region, string name)
    : IpamException($"Allocation '{name}' in region '{region}' is a reservation and cannot be released.")
{
    /// <summary>The region holding the reservation.</summary>
    public string Region { get; } = region;

    /// <summary>The reservation's name.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// The requested region index is outside the geographic range (<c>1</c>–<c>255</c>); index
/// <c>0</c> is reserved for the platform-shared pool (data-model §1, contract).
/// </summary>
public sealed class InvalidRegionIndexException(int regionIndex)
    : IpamException($"Region index {regionIndex} is invalid; geographic regions use indices 1–255 (0 is reserved for the platform-shared pool).")
{
    /// <summary>The rejected region index.</summary>
    public int RegionIndex { get; } = regionIndex;
}

/// <summary>
/// The region is already registered with a different index — a region's <c>/16</c> is fixed at
/// registration, so re-registering it under a new index is refused (contract). Re-registering
/// with the <em>same</em> index is the idempotent no-op success path, not this error.
/// </summary>
public sealed class RegionAlreadyExistsException(string region, int existingIndex, int requestedIndex)
    : IpamException($"Region '{region}' is already registered with index {existingIndex}; it cannot be re-registered with index {requestedIndex}.")
{
    /// <summary>The region whose registration conflicts.</summary>
    public string Region { get; } = region;

    /// <summary>The index the region is already registered under.</summary>
    public int ExistingIndex { get; } = existingIndex;

    /// <summary>The index the conflicting request asked for.</summary>
    public int RequestedIndex { get; } = requestedIndex;
}

/// <summary>
/// The requested region's <c>/16</c> supernet overlaps an already-registered region's — refused
/// by the <c>region_pool_supernet_no_overlap</c> exclusion constraint, the database backstop
/// for FR-011. Since each region's supernet is keyed by its index, this means the index is
/// already taken by another region.
/// </summary>
public sealed class SupernetOverlapException : IpamException
{
    /// <summary>Creates the exception when the colliding region is known up front.</summary>
    public SupernetOverlapException(string region, int regionIndex, string? conflictingRegion)
        : base(Describe(region, regionIndex, conflictingRegion))
    {
        Region = region;
        RegionIndex = regionIndex;
        ConflictingRegion = conflictingRegion;
    }

    /// <summary>Creates the exception wrapping the database exclusion-constraint violation.</summary>
    public SupernetOverlapException(string region, int regionIndex, string? conflictingRegion, Exception innerException)
        : base(Describe(region, regionIndex, conflictingRegion), innerException)
    {
        Region = region;
        RegionIndex = regionIndex;
        ConflictingRegion = conflictingRegion;
    }

    /// <summary>The region whose registration was refused.</summary>
    public string Region { get; }

    /// <summary>The index whose <c>/16</c> collided.</summary>
    public int RegionIndex { get; }

    /// <summary>The already-registered region holding the overlapping supernet, when known.</summary>
    public string? ConflictingRegion { get; }

    private static string Describe(string region, int regionIndex, string? conflictingRegion)
    {
        var owner = conflictingRegion is null ? "an existing region" : $"region '{conflictingRegion}'";
        return $"Region '{region}' index {regionIndex} maps to supernet 10.{regionIndex}.0.0/16, which overlaps {owner} (FR-011).";
    }
}
