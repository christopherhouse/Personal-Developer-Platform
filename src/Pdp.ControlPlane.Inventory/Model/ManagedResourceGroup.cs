namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// The shared, classification-agnostic detail of a managed resource group (data-model §5): the
/// identity and location every taxonomy item derives from. Kept as a distinct record so the
/// classifier and drift detector can pass RG detail around without coupling to a specific taxonomy
/// shape.
/// </summary>
public sealed record ManagedResourceGroup(
    string Id,
    string Name,
    string SubscriptionId,
    string Location,
    IReadOnlyDictionary<string, string> Tags);
