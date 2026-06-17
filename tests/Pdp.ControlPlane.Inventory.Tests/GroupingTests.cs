using NSubstitute;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;
using Shouldly;

namespace Pdp.ControlPlane.Inventory.Tests;

/// <summary>
/// Headline-question projections (US2, contracts §1 / data-model §5): the environments list, the
/// spokes-with-subscription-and-region list, and the "what is in environment X?" projection —
/// including the contract that an unknown environment returns a clean empty result, not an error.
/// </summary>
public class GroupingTests
{
    private static ResourceGroupRow Row(string name, string subscriptionId, string location, params (string Key, string Value)[] tags) =>
        ResourceGroupRow.Create(
            id: $"/subscriptions/{subscriptionId}/resourceGroups/{name}",
            name: name,
            subscriptionId: subscriptionId,
            location: location,
            tags: tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)));

    private static InventoryService ServiceWith(params ResourceGroupRow[] rows)
    {
        var reader = Substitute.For<IResourceGraphReader>();
        reader.DiscoverSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new SubscriptionCoverage("sub-app", "Apps", SubscriptionStatus.Queried) });
        reader.QueryManagedResourceGroupsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(rows);
        reader.QueryLooksManagedUntaggedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        return new InventoryService(reader);
    }

    [Fact]
    public async Task GetEnvironments_lists_each_distinct_env()
    {
        var service = ServiceWith(
            Row("rg-pdp-westus3-workload-api", "sub-app", "westus3", ("pdp-workload", "api"), ("pdp-env", "demo")),
            Row("rg-pdp-westus3-workload-web", "sub-app", "westus3", ("pdp-workload", "web"), ("pdp-env", "dev")));

        var environments = await service.GetEnvironmentsAsync();

        environments.Select(e => e.Name).ShouldBe(["demo", "dev"]);
    }

    [Fact]
    public async Task GetSpokes_lists_each_spoke_with_subscription_and_region()
    {
        var service = ServiceWith(
            Row("rg-pdp-westus3-spoke-app1", "sub-app", "westus3", ("pdp-spoke", "app1")));

        var spokes = await service.GetSpokesAsync();

        var spoke = spokes.ShouldHaveSingleItem();
        spoke.Name.ShouldBe("app1");
        spoke.SubscriptionId.ShouldBe("sub-app");
        spoke.Region.ShouldBe("westus3");
    }

    [Fact]
    public async Task GetEnvironment_returns_exactly_that_environments_workloads()
    {
        var service = ServiceWith(
            Row("rg-pdp-westus3-workload-api", "sub-app", "westus3", ("pdp-workload", "api"), ("pdp-env", "demo")),
            Row("rg-pdp-westus3-workload-web", "sub-app", "westus3", ("pdp-workload", "web"), ("pdp-env", "demo")),
            Row("rg-pdp-westus3-workload-job", "sub-app", "westus3", ("pdp-workload", "job"), ("pdp-env", "dev")));

        var environment = await service.GetEnvironmentAsync("demo");

        environment.ShouldNotBeNull();
        environment!.Name.ShouldBe("demo");
        environment.Workloads.Select(w => w.Name).ShouldBe(["api", "web"]);
    }

    [Fact]
    public async Task GetEnvironment_is_case_insensitive()
    {
        var service = ServiceWith(
            Row("rg-pdp-westus3-workload-api", "sub-app", "westus3", ("pdp-workload", "api"), ("pdp-env", "demo")));

        var environment = await service.GetEnvironmentAsync("DEMO");

        environment.ShouldNotBeNull();
        environment!.Name.ShouldBe("demo");
    }

    [Fact]
    public async Task GetEnvironment_unknown_returns_null_not_error()
    {
        var service = ServiceWith(
            Row("rg-pdp-westus3-workload-api", "sub-app", "westus3", ("pdp-workload", "api"), ("pdp-env", "demo")));

        var environment = await service.GetEnvironmentAsync("nope");

        environment.ShouldBeNull();
    }
}
