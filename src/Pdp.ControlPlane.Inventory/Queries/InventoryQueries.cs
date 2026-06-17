namespace Pdp.ControlPlane.Inventory.Queries;

/// <summary>
/// The Azure Resource Graph KQL the inventory runs (research §1). Both projections query the
/// <c>ResourceContainers</c> table at <b>resource-group</b> granularity (clarify Q4) and return the
/// full <c>tags</c> bag so the pure engine can apply the <c>pdp-*</c> rules in .NET, not in KQL.
/// </summary>
public static class InventoryQueries
{
    /// <summary>
    /// Every resource group carrying <c>pdp-managed == 'true'</c> — the inclusion filter (FR-003).
    /// The seed backend <c>RG-TF</c> is untagged and so never matches (FR-018).
    /// </summary>
    public const string ManagedResourceGroups =
        """
        ResourceContainers
        | where type =~ 'microsoft.resources/subscriptions/resourcegroups'
        | where tags['pdp-managed'] =~ 'true'
        | project id, name, subscriptionId, location, tags
        """;

    /// <summary>
    /// Resource groups named <c>rg-pdp-*</c> that lack <c>pdp-managed == 'true'</c> — invisible-drift
    /// candidates (FR-014). The seed backend <c>RG-TF</c> does not match the <c>rg-pdp-</c> prefix, so
    /// it is excluded by construction (FR-018).
    /// </summary>
    public const string LooksManagedUntagged =
        """
        ResourceContainers
        | where type =~ 'microsoft.resources/subscriptions/resourcegroups'
        | where name startswith 'rg-pdp-'
        | where isnull(tags['pdp-managed']) or tags['pdp-managed'] !~ 'true'
        | project id, name, subscriptionId, location, tags
        """;
}
