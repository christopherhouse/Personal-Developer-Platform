namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// A platform-shared resource group (classified from the <c>pdp-platform</c> scope tag): foundations,
/// shared DNS, the control plane, and similar regional shared services that are managed but belong to
/// no fabric/spoke/workload scope. Recognized as first-class platform infrastructure rather than
/// orphan drift. <see cref="Region"/> comes from the RG location.
/// </summary>
public sealed record PlatformItem(
    string SubscriptionId,
    string Region,
    string ResourceGroupName);
