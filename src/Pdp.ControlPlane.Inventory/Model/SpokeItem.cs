namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// A spoke resource group (classified from the <c>pdp-spoke</c> scope tag, data-model §3). Spokes
/// carry no region tag (spec 004), so <see cref="Region"/> is taken from the RG's
/// <see cref="ResourceGroupName">location</see>. Each spoke reports its own subscription and region —
/// the data behind "which spokes exist, in which subscription and region?" (FR-007).
/// </summary>
public sealed record SpokeItem(
    string Name,
    string SubscriptionId,
    string Region,
    string ResourceGroupName);
