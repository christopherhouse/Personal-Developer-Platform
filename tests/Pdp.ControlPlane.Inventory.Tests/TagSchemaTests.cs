using Pdp.ControlPlane.Inventory.Classification;
using Shouldly;

namespace Pdp.ControlPlane.Inventory.Tests;

/// <summary>
/// Value-rule validation for <see cref="TagSchema"/> (data-model §2 / conventions §2): the region set,
/// the name/env regexes, the deployed-by enum, the platform marker, and the seed-backend identity.
/// </summary>
public class TagSchemaTests
{
    [Theory]
    [InlineData("westus3", true)]
    [InlineData("eastus2", true)]
    [InlineData("WestUs3", true)] // case-insensitive
    [InlineData("mars-central", false)]
    [InlineData("", false)]
    public void IsKnownRegion_validates_against_the_region_set(string region, bool expected) =>
        TagSchema.IsKnownRegion(region).ShouldBe(expected);

    [Theory]
    [InlineData("app1", true)]
    [InlineData("a-b-c-9", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaa", true)]   // 24 chars
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa", false)] // 25 chars
    [InlineData("Has-Upper", false)]
    [InlineData("under_score", false)]
    [InlineData("", false)]
    public void IsValidResourceName_enforces_lowercase_alnum_hyphen_1_to_24(string value, bool expected) =>
        TagSchema.IsValidResourceName(value).ShouldBe(expected);

    [Theory]
    [InlineData("demo", true)]
    [InlineData("aaaaaaaaaaaaaaaa", true)]   // 16 chars
    [InlineData("aaaaaaaaaaaaaaaaa", false)] // 17 chars
    [InlineData("Dev", false)]
    public void IsValidEnvName_enforces_lowercase_alnum_hyphen_1_to_16(string value, bool expected) =>
        TagSchema.IsValidEnvName(value).ShouldBe(expected);

    [Theory]
    [InlineData("github-actions", true)]
    [InlineData("control-plane", true)]
    [InlineData("owner", true)]
    [InlineData("intern", false)]
    public void IsValidDeployedBy_accepts_only_enumerated_actors(string value, bool expected) =>
        TagSchema.IsValidDeployedBy(value).ShouldBe(expected);

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    public void IsValidPlatformValue_requires_true(string value, bool expected) =>
        TagSchema.IsValidPlatformValue(value).ShouldBe(expected);

    [Theory]
    [InlineData("RG-TF", true)]
    [InlineData("rg-tf", true)] // case-insensitive
    [InlineData("rg-pdp-westus3-fabric", false)]
    public void IsSeedBackend_identifies_the_owner_state_rg(string name, bool expected) =>
        TagSchema.IsSeedBackend(name).ShouldBe(expected);
}
