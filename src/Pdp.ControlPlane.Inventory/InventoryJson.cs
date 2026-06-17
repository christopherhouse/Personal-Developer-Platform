using System.Text.Json;
using System.Text.Json.Serialization;
using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory;

/// <summary>
/// The machine-readable serialization contract for an <see cref="InventorySnapshot"/> (FR-010 /
/// SC-006, contracts §4). Centralized here so every consumer — the demo's <c>--json</c>, and the
/// spec-006 verb/CLI/MCP layers — emits the identical, stable shape: camelCase property names and
/// enums (<see cref="DriftCategory"/>, <see cref="SubscriptionStatus"/>) as strings, not integers.
/// The immutable records serialize through their primary constructors with no per-type attributes.
/// </summary>
public static class InventoryJson
{
    /// <summary>The canonical options: web defaults (camelCase) + string enums + indented output.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Serializes a snapshot to the canonical structured JSON.</summary>
    public static string Serialize(InventorySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);
}
