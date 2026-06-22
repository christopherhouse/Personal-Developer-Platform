using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// Serializes <see cref="IPNetwork"/> as its canonical CIDR string (e.g. <c>10.2.0.0/24</c>). Without it,
/// <c>System.Text.Json</c> reflects over the struct's nested <see cref="IPAddress"/> — and reading
/// <see cref="IPAddress.ScopeId"/> THROWS a <see cref="System.Net.Sockets.SocketException"/> for IPv4
/// addresses, which breaks any serialization of a <see cref="RegionView"/> (the MCP tool output and the
/// CLI <c>--json</c> contract, SC-008). Lives here, beside <see cref="RegionView"/>, so every consumer —
/// the CLI renderers and the <c>pdp-mcp</c> tool serializer — shares one converter.
/// </summary>
public sealed class IpNetworkJsonConverter : JsonConverter<IPNetwork>
{
    /// <inheritdoc />
    public override IPNetwork Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IPNetwork.Parse(reader.GetString()!);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, IPNetwork value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
