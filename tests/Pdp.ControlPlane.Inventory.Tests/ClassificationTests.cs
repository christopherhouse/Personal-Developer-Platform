using Pdp.ControlPlane.Inventory.Classification;
using Pdp.ControlPlane.Inventory.Model;
using Shouldly;

namespace Pdp.ControlPlane.Inventory.Tests;

/// <summary>
/// Exhaustive, Azure-free coverage of the pure classifier (data-model §3): each taxonomy branch, the
/// region-from-<c>location</c> rule, and the scope-count edge cases (orphan / ambiguous).
/// </summary>
public class ClassificationTests
{
    private static ResourceGroupRow Row(string name, string location, params (string Key, string Value)[] tags) =>
        ResourceGroupRow.Create(
            id: $"/subscriptions/sub-1/resourceGroups/{name}",
            name: name,
            subscriptionId: "sub-1",
            location: location,
            tags: tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)));

    [Fact]
    public void Single_fabric_tag_classifies_as_fabric_with_tag_region()
    {
        var row = Row("rg-pdp-westus3-foundations", "westus3", ("pdp-fabric", "westus3"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Fabric);
        result.Fabric.ShouldNotBeNull();
        result.Fabric!.Region.ShouldBe("westus3");
        result.Fabric.Location.ShouldBe("westus3");
        result.Fabric.SubscriptionId.ShouldBe("sub-1");
        result.Fabric.ResourceGroupName.ShouldBe("rg-pdp-westus3-foundations");
    }

    [Fact]
    public void Single_platform_tag_classifies_as_platform_with_region_from_location()
    {
        var row = Row("rg-pdp-westus3-foundations", "westus3", ("pdp-platform", "true"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Platform);
        result.Platform.ShouldNotBeNull();
        result.Platform!.Region.ShouldBe("westus3");
        result.Platform.ResourceGroupName.ShouldBe("rg-pdp-westus3-foundations");
    }

    [Fact]
    public void Single_spoke_tag_classifies_as_spoke_with_region_from_location()
    {
        // Spokes carry no region tag (spec 004) — Region must come from the RG location.
        var row = Row("rg-pdp-westus3-spoke-app1", "westus3", ("pdp-spoke", "app1"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Spoke);
        result.Spoke.ShouldNotBeNull();
        result.Spoke!.Name.ShouldBe("app1");
        result.Spoke.Region.ShouldBe("westus3");
        result.Spoke.ResourceGroupName.ShouldBe("rg-pdp-westus3-spoke-app1");
    }

    [Fact]
    public void Single_workload_tag_classifies_as_workload_with_environment()
    {
        var row = Row("rg-pdp-westus3-workload-api", "westus3", ("pdp-workload", "api"), ("pdp-env", "demo"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Workload);
        result.Workload.ShouldNotBeNull();
        result.Workload!.Name.ShouldBe("api");
        result.Workload.Environment.ShouldBe("demo");
        result.Workload.Region.ShouldBe("westus3");
    }

    [Fact]
    public void Workload_without_env_tag_classifies_as_workload_with_null_environment()
    {
        var row = Row("rg-pdp-westus3-workload-api", "westus3", ("pdp-workload", "api"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Workload);
        result.Workload!.Environment.ShouldBeNull();
    }

    [Fact]
    public void No_scope_tag_classifies_as_orphan_with_no_item()
    {
        var row = Row("rg-pdp-westus3-mystery", "westus3", ("pdp-managed", "true"), ("pdp-deployed-by", "owner"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Orphan);
        result.Fabric.ShouldBeNull();
        result.Spoke.ShouldBeNull();
        result.Workload.ShouldBeNull();
    }

    [Fact]
    public void Multiple_scope_tags_classify_as_ambiguous_with_no_item()
    {
        var row = Row("rg-pdp-westus3-conflict", "westus3", ("pdp-spoke", "app1"), ("pdp-workload", "api"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Ambiguous);
        result.Spoke.ShouldBeNull();
        result.Workload.ShouldBeNull();
    }

    [Fact]
    public void Tag_lookup_is_case_insensitive()
    {
        var row = Row("rg-pdp-westus3-spoke-app1", "westus3", ("PDP-Spoke", "app1"));

        var result = ResourceGroupClassifier.Classify(row);

        result.Kind.ShouldBe(ClassificationKind.Spoke);
        result.Spoke!.Name.ShouldBe("app1");
    }
}
