namespace Pdp.ControlPlane.Verbs.Model;

/// <summary>
/// Request to vend a spoke (spec 004 wrapped; Gate-G1 closed). Immutable; System.Text.Json
/// serializable (the <c>--json</c> contract). <b>Note: no <c>Cidr</c> field</b> — the block is
/// allocated live by <see cref="Size"/> from the IPAM ledger at vend (FR-008), never hand-fitted.
/// </summary>
/// <param name="Subscription">Target subscription id (Azure GUID).</param>
/// <param name="Region">Registered region (e.g. <c>westus3</c>).</param>
/// <param name="Name">Spoke name — unique within <c>(Kind, Subscription)</c> (the natural key).</param>
/// <param name="Size">Requested prefix length (default <c>/24</c>); allocated from the region pool.</param>
/// <param name="Owner">Requesting principal; defaults to the running identity when null.</param>
public sealed record SpokeCreateRequest(
    string Subscription,
    string Region,
    string Name,
    int Size = 24,
    string? Owner = null);

/// <summary>
/// Request to vend a regional fabric (spec 003 wrapped). Dispatches the new <c>fabric-vend.yml</c>
/// (FR-012a) and registers the region in the IPAM ledger if needed.
/// </summary>
/// <param name="Region">The region to stand the fabric up in (the fabric's identity).</param>
/// <param name="RegionIndex">The region's <c>/16</c> index (<c>10.&lt;index&gt;.0.0/16</c>).</param>
/// <param name="Owner">Requesting principal; defaults to the running identity when null.</param>
public sealed record FabricCreateRequest(
    string Region,
    int RegionIndex,
    string? Owner = null);

/// <summary>
/// Request to destroy an environment. Confirmation is <b>mandatory and unbypassable</b> for destroy
/// (Article VIII / FR-007); on a successful spoke destroy the allocation is released (FR-009).
/// </summary>
/// <param name="Target">Which environment to destroy (by <c>env_id</c> or natural key).</param>
/// <param name="Confirmation">The owner's explicit confirmation restating the target.</param>
public sealed record DestroyRequest(
    EnvRef Target,
    Confirmation Confirmation);
