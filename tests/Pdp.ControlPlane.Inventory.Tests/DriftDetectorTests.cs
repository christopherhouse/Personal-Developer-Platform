using Pdp.ControlPlane.Inventory.Classification;
using Pdp.ControlPlane.Inventory.Model;
using Shouldly;

namespace Pdp.ControlPlane.Inventory.Tests;

/// <summary>
/// Coverage of every drift category (data-model §4): orphan, ambiguous, the conformance variants,
/// invisible, and the seed-backend exclusion — plus the platform-shared recognition that keeps
/// foundations/dns/control-plane RGs out of the orphan bucket.
/// </summary>
public class DriftDetectorTests
{
    private static ResourceGroupRow Row(string name, params (string Key, string Value)[] tags) =>
        ResourceGroupRow.Create(
            id: $"/subscriptions/sub-1/resourceGroups/{name}",
            name: name,
            subscriptionId: "sub-1",
            location: "westus3",
            tags: tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)));

    private static (string Key, string Value) Managed => ("pdp-managed", "true");
    private static (string Key, string Value) DeployedBy => ("pdp-deployed-by", "github-actions");

    [Fact]
    public void Managed_rg_with_no_scope_tag_is_orphan()
    {
        var findings = DriftDetector.DetectManaged(Row("rg-pdp-westus3-mystery", Managed, DeployedBy)).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Orphan);
    }

    [Fact]
    public void Managed_rg_with_two_scope_tags_is_ambiguous()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-westus3-conflict", Managed, DeployedBy, ("pdp-spoke", "app1"), ("pdp-workload", "api"))).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Ambiguous);
    }

    [Fact]
    public void Platform_tagged_rg_is_not_orphan()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-westus3-foundations", Managed, DeployedBy, ("pdp-platform", "true"))).ToList();

        findings.ShouldNotContain(f => f.Category == DriftCategory.Orphan);
        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Bad_fabric_region_is_conformance()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-mars-fabric", Managed, DeployedBy, ("pdp-fabric", "mars-central"))).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Conformance && f.OffendingTag == "pdp-fabric");
    }

    [Fact]
    public void Fabric_region_not_matching_location_is_conformance()
    {
        // pdp-fabric is a valid region but disagrees with the RG location (westus3).
        var row = ResourceGroupRow.Create(
            "/subscriptions/sub-1/resourceGroups/rg-pdp-westus3-fabric",
            "rg-pdp-westus3-fabric",
            "sub-1",
            "westus3",
            [new("pdp-managed", "true"), new("pdp-deployed-by", "owner"), new("pdp-fabric", "eastus2")]);

        var findings = DriftDetector.DetectManaged(row).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Conformance && f.OffendingTag == "pdp-fabric");
    }

    [Fact]
    public void Bad_spoke_name_is_conformance()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-westus3-spoke-bad", Managed, DeployedBy, ("pdp-spoke", "Bad_Name"))).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Conformance && f.OffendingTag == "pdp-spoke");
    }

    [Fact]
    public void Bad_deployed_by_is_conformance()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-westus3-spoke-app1", Managed, ("pdp-deployed-by", "intern"), ("pdp-spoke", "app1"))).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Conformance && f.OffendingTag == "pdp-deployed-by");
    }

    [Fact]
    public void Workload_missing_env_is_conformance()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-westus3-workload-api", Managed, DeployedBy, ("pdp-workload", "api"))).ToList();

        findings.ShouldContain(f => f.Category == DriftCategory.Conformance && f.OffendingTag == "pdp-env");
    }

    [Fact]
    public void Clean_spoke_produces_no_findings()
    {
        var findings = DriftDetector.DetectManaged(
            Row("rg-pdp-westus3-spoke-app1", Managed, DeployedBy, ("pdp-spoke", "app1"))).ToList();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Untagged_rg_named_like_pdp_is_invisible()
    {
        var finding = DriftDetector.DetectInvisible(Row("rg-pdp-westus3-leftover"));

        finding.ShouldNotBeNull();
        finding!.Category.ShouldBe(DriftCategory.Invisible);
    }

    [Fact]
    public void Seed_backend_is_never_flagged()
    {
        // Even if (wrongly) tagged in a way that would otherwise be orphan/ambiguous.
        DriftDetector.DetectManaged(Row("RG-TF", Managed, DeployedBy)).ShouldBeEmpty();
        DriftDetector.DetectInvisible(Row("RG-TF")).ShouldBeNull();
    }

    [Fact]
    public void Detect_aggregates_managed_and_invisible_findings()
    {
        var managed = new[]
        {
            Row("rg-pdp-westus3-orphan", Managed, DeployedBy),
            Row("rg-pdp-westus3-spoke-app1", Managed, DeployedBy, ("pdp-spoke", "app1")),
        };
        var invisible = new[] { Row("rg-pdp-westus3-leftover") };

        var findings = DriftDetector.Detect(managed, invisible);

        findings.ShouldContain(f => f.Category == DriftCategory.Orphan);
        findings.ShouldContain(f => f.Category == DriftCategory.Invisible);
    }
}
