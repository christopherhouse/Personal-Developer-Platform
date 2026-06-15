namespace Pdp.ControlPlane.Ipam.Entities;

/// <summary>
/// Why a block is held in the ledger. Persisted as text (readable in the DB — no integer
/// codes) so a row's intent is self-evident.
/// </summary>
public enum AllocationKind
{
    /// <summary>A spoke VNet range handed out by <c>allocate</c>; releasable.</summary>
    Spoke,

    /// <summary>
    /// A pre-committed, non-allocatable block (e.g. the control-plane VNet). Recorded so the
    /// range is registered (Article VI) but refused by <c>release</c> (data-model §3).
    /// </summary>
    Reservation,
}
