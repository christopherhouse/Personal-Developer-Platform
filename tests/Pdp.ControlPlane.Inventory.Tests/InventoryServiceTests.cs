using NSubstitute;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;
using Shouldly;

namespace Pdp.ControlPlane.Inventory.Tests;

/// <summary>
/// Orchestration tests for <see cref="InventoryService.GetSnapshotAsync"/> over a faked
/// <see cref="IResourceGraphReader"/> (NSubstitute) — no Azure. Proves the snapshot assembles the
/// taxonomy, groups workloads into environments, preserves subscription coverage, queries only the
/// readable subscriptions, and excludes the seed backend.
/// </summary>
public class InventoryServiceTests
{
    private static ResourceGroupRow Row(string name, string subscriptionId, string location, params (string Key, string Value)[] tags) =>
        ResourceGroupRow.Create(
            id: $"/subscriptions/{subscriptionId}/resourceGroups/{name}",
            name: name,
            subscriptionId: subscriptionId,
            location: location,
            tags: tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)));

    private static IResourceGraphReader FakeReader(
        IReadOnlyList<SubscriptionCoverage> coverage,
        IReadOnlyList<ResourceGroupRow> managedRows)
    {
        var reader = Substitute.For<IResourceGraphReader>();
        reader.DiscoverSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(coverage);
        reader.QueryManagedResourceGroupsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(managedRows);
        reader.QueryLooksManagedUntaggedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        return reader;
    }

    [Fact]
    public async Task Snapshot_assembles_taxonomy_environments_and_coverage()
    {
        var coverage = new[]
        {
            new SubscriptionCoverage("sub-platform", "Platform", SubscriptionStatus.Queried),
            new SubscriptionCoverage("sub-app", "Apps", SubscriptionStatus.Queried),
        };
        var rows = new[]
        {
            Row("rg-pdp-westus3-foundations", "sub-platform", "westus3", ("pdp-fabric", "westus3")),
            Row("rg-pdp-westus3-spoke-app1", "sub-app", "westus3", ("pdp-spoke", "app1")),
            Row("rg-pdp-westus3-workload-api", "sub-app", "westus3", ("pdp-workload", "api"), ("pdp-env", "demo")),
            Row("rg-pdp-westus3-workload-web", "sub-app", "westus3", ("pdp-workload", "web"), ("pdp-env", "demo")),
        };
        var service = new InventoryService(FakeReader(coverage, rows));

        var snapshot = await service.GetSnapshotAsync();

        snapshot.Fabrics.ShouldHaveSingleItem().Region.ShouldBe("westus3");
        snapshot.Spokes.ShouldHaveSingleItem().Name.ShouldBe("app1");
        snapshot.Workloads.Count.ShouldBe(2);
        snapshot.Coverage.ShouldBe(coverage);

        var environment = snapshot.Environments.ShouldHaveSingleItem();
        environment.Name.ShouldBe("demo");
        environment.Workloads.Select(w => w.Name).ShouldBe(["api", "web"]);
    }

    [Fact]
    public async Task Inaccessible_subscriptions_are_kept_in_coverage_but_not_queried()
    {
        var coverage = new[]
        {
            new SubscriptionCoverage("sub-ok", "Ok", SubscriptionStatus.Queried),
            new SubscriptionCoverage("sub-bad", "Disabled", SubscriptionStatus.Inaccessible),
        };
        var reader = FakeReader(coverage, []);
        var service = new InventoryService(reader);

        var snapshot = await service.GetSnapshotAsync();

        snapshot.Coverage.Count.ShouldBe(2);
        // Only the readable subscription is passed to the ARG query (coverage honesty, FR-011).
        await reader.Received(1).QueryManagedResourceGroupsAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "sub-ok"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workload_without_env_is_listed_flat_but_joins_no_environment()
    {
        var coverage = new[] { new SubscriptionCoverage("sub-app", "Apps", SubscriptionStatus.Queried) };
        var rows = new[]
        {
            Row("rg-pdp-westus3-workload-api", "sub-app", "westus3", ("pdp-workload", "api")),
        };
        var service = new InventoryService(FakeReader(coverage, rows));

        var snapshot = await service.GetSnapshotAsync();

        snapshot.Workloads.ShouldHaveSingleItem().Name.ShouldBe("api");
        snapshot.Environments.ShouldBeEmpty();
    }

    [Fact]
    public async Task Seed_backend_is_never_classified()
    {
        var coverage = new[] { new SubscriptionCoverage("sub-platform", "Platform", SubscriptionStatus.Queried) };
        // Even if the seed backend were (wrongly) tagged, it must never appear in the taxonomy (FR-018).
        var rows = new[]
        {
            Row("RG-TF", "sub-platform", "westus3", ("pdp-fabric", "westus3")),
        };
        var service = new InventoryService(FakeReader(coverage, rows));

        var snapshot = await service.GetSnapshotAsync();

        snapshot.Fabrics.ShouldBeEmpty();
        snapshot.Spokes.ShouldBeEmpty();
        snapshot.Workloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Orphan_and_ambiguous_rows_produce_no_taxonomy_item()
    {
        var coverage = new[] { new SubscriptionCoverage("sub-app", "Apps", SubscriptionStatus.Queried) };
        var rows = new[]
        {
            Row("rg-pdp-westus3-orphan", "sub-app", "westus3", ("pdp-managed", "true")),
            Row("rg-pdp-westus3-ambiguous", "sub-app", "westus3", ("pdp-spoke", "a"), ("pdp-workload", "b")),
        };
        var service = new InventoryService(FakeReader(coverage, rows));

        var snapshot = await service.GetSnapshotAsync();

        snapshot.Fabrics.ShouldBeEmpty();
        snapshot.Spokes.ShouldBeEmpty();
        snapshot.Workloads.ShouldBeEmpty();
    }
}
