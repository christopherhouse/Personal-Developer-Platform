using System.Text.Json;
using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.Cli.Rendering;

/// <summary>
/// Renders the spec-005 inventory projections two ways from the identical typed object (SC-008): grouped
/// human tables (default) or <c>--json</c>. "What's deployed?" comes from Azure Resource Graph via the
/// verb layer (FR-016); the CLI only formats the result, never re-queries.
/// </summary>
public static class InventoryView
{
    /// <summary>Renders the full snapshot (<c>pdp inventory</c>).</summary>
    public static void RenderSnapshot(InventorySnapshot snapshot, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(snapshot, VerbResultView.Json));
            return;
        }

        writer.WriteLine($"fabrics ({snapshot.Fabrics.Count}):");
        foreach (var fabric in snapshot.Fabrics)
        {
            writer.WriteLine($"  {fabric.Region,-12} {fabric.ResourceGroupName}  [{fabric.SubscriptionId}]");
        }

        writer.WriteLine($"spokes ({snapshot.Spokes.Count}):");
        foreach (var spoke in snapshot.Spokes)
        {
            writer.WriteLine($"  {spoke.Name,-16} {spoke.Region,-12} {spoke.ResourceGroupName}  [{spoke.SubscriptionId}]");
        }

        writer.WriteLine($"environments ({snapshot.Environments.Count}):");
        foreach (var environment in snapshot.Environments)
        {
            writer.WriteLine($"  {environment.Name} ({environment.Workloads.Count} workload(s))");
        }

        writer.WriteLine($"drift findings: {snapshot.Drift.Count}");
        writer.WriteLine($"subscriptions scanned: {snapshot.Coverage.Count}");
    }

    /// <summary>Renders the deployed environments (<c>pdp env list</c>).</summary>
    public static void RenderEnvironments(IReadOnlyList<EnvironmentView> environments, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(environments, VerbResultView.Json));
            return;
        }

        writer.WriteLine($"environments ({environments.Count}):");
        foreach (var environment in environments)
        {
            writer.WriteLine($"  {environment.Name} ({environment.Workloads.Count} workload(s))");
        }
    }

    /// <summary>Renders one environment's contents (<c>pdp env show &lt;name&gt;</c>).</summary>
    public static void RenderEnvironment(string name, EnvironmentView? environment, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(environment, VerbResultView.Json));
            return;
        }

        if (environment is null)
        {
            writer.WriteLine($"No environment '{name}' is deployed.");
            return;
        }

        writer.WriteLine($"environment {environment.Name}");
        writer.WriteLine($"workloads ({environment.Workloads.Count}):");
        foreach (var workload in environment.Workloads)
        {
            writer.WriteLine($"  {workload.Name,-16} {workload.Region,-12} {workload.ResourceGroupName}  [{workload.SubscriptionId}]");
        }
    }
}
