namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// A regional fabric resource group (classified from the <c>pdp-fabric</c> scope tag, data-model §3).
/// <see cref="Region"/> is the <c>pdp-fabric</c> tag value; <see cref="Location"/> is the RG's actual
/// Azure location — a mismatch between the two is a conformance finding (data-model §4), not a
/// reclassification.
/// </summary>
public sealed record FabricItem(
    string Region,
    string SubscriptionId,
    string ResourceGroupName,
    string Location);
