namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// One informational tag-side drift observation about a resource group (data-model §4). Drift is
/// reported, never thrown: the snapshot always succeeds regardless of how many findings it carries
/// (FR-016). The owner-managed seed backend is never a finding (FR-018).
/// </summary>
/// <param name="Category">Which kind of drift this is.</param>
/// <param name="ResourceGroupId">Full ARM id of the offending RG (empty for invisible candidates without an id).</param>
/// <param name="ResourceGroupName">The RG's name.</param>
/// <param name="SubscriptionId">The subscription the RG lives in.</param>
/// <param name="OffendingTag">The specific tag at fault, when the category points to one (null otherwise).</param>
/// <param name="Detail">A human-readable explanation of the finding.</param>
public sealed record DriftFinding(
    DriftCategory Category,
    string ResourceGroupId,
    string ResourceGroupName,
    string SubscriptionId,
    string? OffendingTag,
    string Detail);
