using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdp.Cli.Rendering;

/// <summary>
/// Serializes <see cref="IPNetwork"/> as its canonical CIDR string (e.g. <c>10.2.0.0/24</c>) for the
/// <c>--json</c> contract (SC-008). Without this, <c>System.Text.Json</c> would emit the struct's
/// nested <c>IPAddress</c>/prefix shape — not the consumable CIDR a script expects. Used by the IPAM
/// renderers, whose <see cref="Pdp.ControlPlane.Ipam.RegionView"/> carries several <see cref="IPNetwork"/>
/// fields.
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
