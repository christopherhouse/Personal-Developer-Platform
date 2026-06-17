namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// A workload resource group (classified from the <c>pdp-workload</c> scope tag, data-model §3).
/// <see cref="Region"/> comes from the RG location. <see cref="Environment"/> is the <c>pdp-env</c>
/// grouping key; it is <c>null</c> when the tag is missing — the workload is still listed but joins no
/// <see cref="EnvironmentView"/> and raises a conformance finding (data-model §4, §6).
/// </summary>
public sealed record WorkloadItem(
    string Name,
    string? Environment,
    string SubscriptionId,
    string Region,
    string ResourceGroupName);
