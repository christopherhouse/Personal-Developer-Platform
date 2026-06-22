using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// Regression for the live MCP failure: a tool returning a <see cref="RegionView"/> (cidr-typed
/// <see cref="IPNetwork"/> fields) threw <c>SocketException</c> during result serialization, because
/// <c>System.Text.Json</c> reflecting over <see cref="IPNetwork"/> reads <see cref="IPAddress.ScopeId"/>,
/// which is invalid for IPv4. The MCP server registers <see cref="IpNetworkJsonConverter"/> on the tool
/// serializer options (Program.cs) — this asserts a fully-populated RegionView, including the
/// <c>List&lt;IPNetwork&gt;</c> free blocks, serializes to canonical CIDR strings without throwing.
/// </summary>
public sealed class IpamSerializationTests
{
    private static JsonSerializerOptions ToolOptions()
    {
        // Mirror Program.cs: the SDK defaults + the CIDR-string converter.
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.Converters.Add(new IpNetworkJsonConverter());
        return options;
    }

    [Fact]
    public void RegionView_serializes_every_cidr_field_as_a_string_without_throwing()
    {
        var view = new RegionView(
            Region: "westus3",
            RegionIndex: 3,
            Supernet: IPNetwork.Parse("10.3.0.0/16"),
            HubCarveout: IPNetwork.Parse("10.3.252.0/22"),
            Allocations:
            [
                new AllocationView("spoke-a", IPNetwork.Parse("10.3.0.0/24"), 24, AllocationKind.Spoke, DateTimeOffset.UnixEpoch),
            ],
            Free: new FreeSpace(AddressCount: 64000, Blocks: [IPNetwork.Parse("10.3.1.0/24"), IPNetwork.Parse("10.3.2.0/23")]));

        // Without the converter this throws SocketException on the first IPv4 IPNetwork.
        var json = JsonSerializer.Serialize(view, ToolOptions());

        json.ShouldContain("\"10.3.0.0/16\"");   // Supernet
        json.ShouldContain("\"10.3.252.0/22\""); // HubCarveout
        json.ShouldContain("\"10.3.0.0/24\"");   // AllocationView.Network
        json.ShouldContain("\"10.3.1.0/24\"");   // FreeSpace.Blocks[0] — the List<IPNetwork> element
        json.ShouldContain("\"10.3.2.0/23\"");   // FreeSpace.Blocks[1]
    }
}
