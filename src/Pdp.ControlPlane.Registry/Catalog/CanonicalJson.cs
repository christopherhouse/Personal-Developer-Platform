using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// Deterministic JSON serialization for content hashing (spec 008, R2): object keys sorted ordinal,
/// no insignificant whitespace. Two files that differ only in property order or formatting produce
/// the same canonical form — so a version's content hash captures <i>meaning</i>, and cosmetic edits
/// to <c>archetypes/catalog.json</c> can never trip the immutability guard.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Serializes <paramref name="node"/> to its canonical form.</summary>
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;

            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(key));
                    builder.Append(':');
                    Write(value, builder);
                }

                builder.Append('}');
                break;

            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                break;

            default:
                // Scalars (string/number/bool) — ToJsonString emits the escaped JSON literal.
                builder.Append(node.ToJsonString());
                break;
        }
    }
}
