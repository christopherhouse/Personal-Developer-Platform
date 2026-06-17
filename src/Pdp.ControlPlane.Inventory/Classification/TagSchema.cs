using System.Text.RegularExpressions;

namespace Pdp.ControlPlane.Inventory.Classification;

/// <summary>
/// The binding <c>pdp-*</c> tag schema and value rules (<c>docs/conventions.md</c> §2, data-model §2)
/// plus the owner-managed seed-backend identifiers that are always excluded (FR-018). The single
/// source of truth the classifier and drift detector validate against — keep it free of Azure types
/// so it stays a pure, fully unit-testable rule set.
/// </summary>
public static partial class TagSchema
{
    // --- Tag names -------------------------------------------------------------------------------

    /// <summary>Universal inclusion tag; presence + <c>true</c> is the inventory filter (FR-003).</summary>
    public const string Managed = "pdp-managed";

    /// <summary>Universal provenance tag; one of <see cref="DeployedByValues"/>.</summary>
    public const string DeployedBy = "pdp-deployed-by";

    /// <summary>Fabric scope tag; value is the fabric's Azure region.</summary>
    public const string Fabric = "pdp-fabric";

    /// <summary>
    /// Platform-shared scope tag (value <c>true</c>): marks a platform-shared resource group
    /// (foundations / dns / control-plane) that is managed but belongs to none of the
    /// fabric/spoke/workload scopes. Presence makes the RG a recognized <c>Platform</c> item rather
    /// than an orphan. Region comes from the RG location.
    /// </summary>
    public const string Platform = "pdp-platform";

    /// <summary>Spoke scope tag; value is the spoke name.</summary>
    public const string Spoke = "pdp-spoke";

    /// <summary>Workload scope tag; value is the workload name.</summary>
    public const string Workload = "pdp-workload";

    /// <summary>Workload environment grouping key; the "what environments?" answer.</summary>
    public const string Env = "pdp-env";

    /// <summary>The only accepted value for <see cref="Managed"/> and <see cref="Platform"/>.</summary>
    public const string ManagedValue = "true";

    /// <summary>The scope tags whose count drives classification (data-model §3).</summary>
    public static IReadOnlyList<string> ScopeTags { get; } = [Fabric, Platform, Spoke, Workload];

    /// <summary>Universal tags required on every managed RG (data-model §2).</summary>
    public static IReadOnlyList<string> UniversalTags { get; } = [Managed, DeployedBy];

    /// <summary>The enumerated actors allowed for <see cref="DeployedBy"/>.</summary>
    public static IReadOnlySet<string> DeployedByValues { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "github-actions", "control-plane", "owner" };

    // --- Seed backend (owner-managed; always excluded — FR-018) ----------------------------------

    /// <summary>The owner's OpenTofu state resource group, never classified or flagged.</summary>
    public const string SeedBackendResourceGroup = "RG-TF";

    /// <summary>The owner's OpenTofu state storage account name.</summary>
    public const string SeedBackendStorageAccount = "cmhtfstatesa";

    // --- Known Azure regions (for pdp-fabric / location conformance) -----------------------------

    /// <summary>
    /// Public-cloud Azure region names a <see cref="Fabric"/> tag (and an RG location) may take.
    /// Matched case-insensitively; extend by PR as regions are onboarded (spec 009).
    /// </summary>
    public static IReadOnlySet<string> KnownRegions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "eastus", "eastus2", "westus", "westus2", "westus3", "centralus", "northcentralus",
            "southcentralus", "westcentralus", "canadacentral", "canadaeast", "brazilsouth",
            "brazilsoutheast", "northeurope", "westeurope", "uksouth", "ukwest", "francecentral",
            "francesouth", "germanywestcentral", "germanynorth", "norwayeast", "norwaywest",
            "switzerlandnorth", "switzerlandwest", "swedencentral", "polandcentral", "italynorth",
            "spaincentral", "australiaeast", "australiasoutheast", "australiacentral",
            "australiacentral2", "eastasia", "southeastasia", "japaneast", "japanwest",
            "koreacentral", "koreasouth", "centralindia", "southindia", "westindia",
            "jioindiawest", "uaenorth", "uaecentral", "qatarcentral", "israelcentral",
            "southafricanorth", "southafricawest",
        };

    // --- Value-rule helpers ----------------------------------------------------------------------

    /// <summary>True when the RG carries <c>pdp-managed == 'true'</c> (case-insensitive).</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string> tags) =>
        tags.TryGetValue(Managed, out var value) && string.Equals(value, ManagedValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="value"/> is the accepted <see cref="Platform"/> marker value.</summary>
    public static bool IsValidPlatformValue(string value) =>
        string.Equals(value, ManagedValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the RG is the owner-managed seed backend (excluded everywhere — FR-018).</summary>
    public static bool IsSeedBackend(string resourceGroupName) =>
        string.Equals(resourceGroupName, SeedBackendResourceGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="region"/> is a known Azure region.</summary>
    public static bool IsKnownRegion(string region) => KnownRegions.Contains(region);

    /// <summary>True when <paramref name="value"/> is an accepted <see cref="DeployedBy"/> actor.</summary>
    public static bool IsValidDeployedBy(string value) => DeployedByValues.Contains(value);

    /// <summary>Validates a spoke or workload name against <c>[a-z0-9-]{1,24}</c>.</summary>
    public static bool IsValidResourceName(string value) => ResourceNameRegex().IsMatch(value);

    /// <summary>Validates an environment name against <c>[a-z0-9-]{1,16}</c>.</summary>
    public static bool IsValidEnvName(string value) => EnvNameRegex().IsMatch(value);

    [GeneratedRegex("^[a-z0-9-]{1,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceNameRegex();

    [GeneratedRegex("^[a-z0-9-]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNameRegex();
}
