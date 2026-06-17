using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory.Classification;

/// <summary>
/// Maps a <b>managed</b> resource group row to exactly one taxonomy outcome (data-model §3). Pure and
/// deterministic — row in, verdict out — so every branch is unit-testable with fabricated rows and no
/// Azure. Region for spokes/workloads comes from the RG <see cref="ResourceGroupRow.Location"/> (spokes
/// carry no region tag — spec 004); for fabrics the <c>pdp-fabric</c> tag value is the declared region
/// (its agreement with <see cref="ResourceGroupRow.Location"/> is a conformance concern, checked
/// separately — it never changes the classification here).
/// </summary>
public static class ResourceGroupClassifier
{
    /// <summary>Classifies one managed RG into fabric / spoke / workload, or orphan / ambiguous.</summary>
    public static ClassificationResult Classify(ResourceGroupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var scopeTags = TagSchema.ScopeTags.Where(tag => row.Tags.ContainsKey(tag)).ToList();

        if (scopeTags.Count == 0)
        {
            return new ClassificationResult(ClassificationKind.Orphan);
        }

        if (scopeTags.Count > 1)
        {
            return new ClassificationResult(ClassificationKind.Ambiguous);
        }

        var scope = scopeTags[0];

        if (string.Equals(scope, TagSchema.Fabric, StringComparison.OrdinalIgnoreCase))
        {
            var region = row.Tags[TagSchema.Fabric];
            return new ClassificationResult(
                ClassificationKind.Fabric,
                Fabric: new FabricItem(region, row.SubscriptionId, row.Name, row.Location));
        }

        if (string.Equals(scope, TagSchema.Platform, StringComparison.OrdinalIgnoreCase))
        {
            // Platform-shared infra carries no region tag — region is the RG location.
            return new ClassificationResult(
                ClassificationKind.Platform,
                Platform: new PlatformItem(row.SubscriptionId, row.Location, row.Name));
        }

        if (string.Equals(scope, TagSchema.Spoke, StringComparison.OrdinalIgnoreCase))
        {
            return new ClassificationResult(
                ClassificationKind.Spoke,
                Spoke: new SpokeItem(row.Tags[TagSchema.Spoke], row.SubscriptionId, row.Location, row.Name));
        }

        // Sole remaining scope tag is pdp-workload.
        var environment = row.Tags.TryGetValue(TagSchema.Env, out var env) ? env : null;
        return new ClassificationResult(
            ClassificationKind.Workload,
            Workload: new WorkloadItem(row.Tags[TagSchema.Workload], environment, row.SubscriptionId, row.Location, row.Name));
    }
}
