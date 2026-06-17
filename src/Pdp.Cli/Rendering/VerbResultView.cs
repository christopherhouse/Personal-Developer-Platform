using System.Text.Json;
using System.Text.Json.Serialization;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Rendering;

/// <summary>
/// Renders the one typed <see cref="VerbResult"/> two ways from the <b>identical object</b> (SC-008):
/// a grouped human table (default) or <c>--json</c> (<see cref="JsonSerializer"/>). Neither re-queries;
/// the CLI is a thin adapter over the verb layer (contracts/cli-surface.md §3).
/// </summary>
public static class VerbResultView
{
    /// <summary>The shared serializer options for the <c>--json</c> contract (camelCase, enums as text).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes <paramref name="result"/> to <paramref name="writer"/> as JSON or a human table.</summary>
    public static void Render(VerbResult result, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(result, Json));
            return;
        }

        writer.WriteLine($"env_id   {result.EnvId}");
        writer.WriteLine($"status   {result.Status}");
        if (result.RunId is { } runId)
        {
            writer.WriteLine($"run_id   {runId}");
        }

        if (result.Outcome is { } outcome)
        {
            writer.WriteLine($"outcome  {outcome}");
        }

        if (!string.IsNullOrWhiteSpace(result.GitHubRunUrl))
        {
            writer.WriteLine($"run_url  {result.GitHubRunUrl}");
        }

        foreach (var message in result.Messages)
        {
            writer.WriteLine($"  {message}");
        }
    }
}
