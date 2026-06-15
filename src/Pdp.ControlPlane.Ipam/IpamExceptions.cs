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
