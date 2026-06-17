using System.Text.Json;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;
using Shouldly;

namespace Pdp.ControlPlane.Inventory.Tests;

/// <summary>
/// Structured-output proof (US4, FR-010 / SC-006, contracts §4): an <see cref="InventorySnapshot"/>
/// serializes via the canonical <see cref="InventoryJson"/> options exposing the full taxonomy,
/// per-item subscription/region, environment grouping, drift, and coverage — with enums as strings —
/// and round-trips back to an equal graph. No text scraping required.
/// </summary>
public class SerializationTests
{
    private static InventorySnapshot SampleSnapshot() => new(
        Environments: [new EnvironmentView("demo", [new WorkloadItem("api", "demo", "sub-app", "westus3", "rg-pdp-westus3-workload-api")])],
        Fabrics: [new FabricItem("westus3", "sub-platform", "rg-pdp-westus3-fabric", "westus3")],
        Platform: [new PlatformItem("sub-platform", "westus3", "rg-pdp-westus3-foundations")],
        Spokes: [new SpokeItem("app1", "sub-app", "westus3", "rg-pdp-westus3-spoke-app1")],
        Workloads: [new WorkloadItem("api", "demo", "sub-app", "westus3", "rg-pdp-westus3-workload-api")],
        Drift: [new DriftFinding(DriftCategory.Orphan, "/subscriptions/sub-app/resourceGroups/rg-x", "rg-x", "sub-app", null, "no scope tag")],
        Coverage: [new SubscriptionCoverage("sub-app", "Apps", SubscriptionStatus.Queried)]);

    [Fact]
    public void Snapshot_serializes_with_all_top_level_sections()
    {
        var json = InventoryJson.Serialize(SampleSnapshot());

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var section in new[] { "environments", "fabrics", "platform", "spokes", "workloads", "drift", "coverage" })
        {
            root.TryGetProperty(section, out _).ShouldBeTrue($"missing section: {section}");
        }
    }

    [Fact]
    public void Spokes_expose_subscription_and_region()
    {
        var json = InventoryJson.Serialize(SampleSnapshot());

        using var document = JsonDocument.Parse(json);
        var spoke = document.RootElement.GetProperty("spokes")[0];

        spoke.GetProperty("name").GetString().ShouldBe("app1");
        spoke.GetProperty("subscriptionId").GetString().ShouldBe("sub-app");
        spoke.GetProperty("region").GetString().ShouldBe("westus3");
    }

    [Fact]
    public void Workloads_expose_subscription_region_and_environment()
    {
        var json = InventoryJson.Serialize(SampleSnapshot());

        using var document = JsonDocument.Parse(json);
        var workload = document.RootElement.GetProperty("workloads")[0];

        workload.GetProperty("subscriptionId").GetString().ShouldBe("sub-app");
        workload.GetProperty("region").GetString().ShouldBe("westus3");
        workload.GetProperty("environment").GetString().ShouldBe("demo");
    }

    [Fact]
    public void DriftCategory_and_status_serialize_as_strings()
    {
        var json = InventoryJson.Serialize(SampleSnapshot());

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("drift")[0].GetProperty("category").GetString().ShouldBe("orphan");
        document.RootElement.GetProperty("coverage")[0].GetProperty("status").GetString().ShouldBe("queried");
    }

    [Fact]
    public void Environment_grouping_survives_serialization()
    {
        var json = InventoryJson.Serialize(SampleSnapshot());

        using var document = JsonDocument.Parse(json);
        var environment = document.RootElement.GetProperty("environments")[0];

        environment.GetProperty("name").GetString().ShouldBe("demo");
        environment.GetProperty("workloads")[0].GetProperty("name").GetString().ShouldBe("api");
    }

    [Fact]
    public void Snapshot_round_trips_back_to_an_equal_graph()
    {
        var original = SampleSnapshot();

        var json = InventoryJson.Serialize(original);
        var restored = JsonSerializer.Deserialize<InventorySnapshot>(json, InventoryJson.Options);

        restored.ShouldNotBeNull();
        restored!.Fabrics.Count.ShouldBe(1);
        restored.Platform.ShouldHaveSingleItem().ResourceGroupName.ShouldBe("rg-pdp-westus3-foundations");
        restored.Spokes.ShouldHaveSingleItem().Name.ShouldBe("app1");
        restored.Drift.ShouldHaveSingleItem().Category.ShouldBe(DriftCategory.Orphan);
        restored.Coverage.ShouldHaveSingleItem().Status.ShouldBe(SubscriptionStatus.Queried);
    }
}
