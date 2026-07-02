using System.Text.Json.Nodes;
using Json.Schema;

namespace Pdp.ControlPlane.Verbs.Validation;

/// <summary>
/// Evaluates caller parameters against an archetype version's JSON schema (draft 2020-12,
/// JsonSchema.Net) — the third step of the spec-008 validation order (R3), run <b>before</b> any
/// intent row is written or run dispatched (FR-003/SC-003). <c>OutputFormat.List</c> flattens the
/// evaluation so every failing constraint maps to one <see cref="ParameterViolation"/> naming the
/// offending parameter, the keyword, and the schema's message — surfaced verbatim by the CLI and MCP.
/// </summary>
public static class ParameterSchemaEvaluator
{
    /// <summary>The path reported for document-level violations (e.g. a missing required parameter).</summary>
    public const string RootPath = "(root)";

    /// <summary>
    /// Evaluates <paramref name="parameters"/> against <paramref name="schemaJson"/> (a valid draft
    /// 2020-12 schema — the catalog sync guarantees parseability). Returns an empty list when the
    /// parameters conform.
    /// </summary>
    public static IReadOnlyList<ParameterViolation> Evaluate(string schemaJson, JsonObject parameters)
    {
        var schema = JsonSchema.FromText(schemaJson);
        var results = schema.Evaluate(parameters, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        return results.Details
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(error => new ParameterViolation(
                d.InstanceLocation.ToString() is { Length: > 0 } path ? path : RootPath,
                error.Key,
                error.Value)))
            .ToList();
    }
}
