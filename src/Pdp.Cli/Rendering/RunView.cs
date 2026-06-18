using System.Text.Json;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Rendering;

/// <summary>
/// Renders the registry-audit reads two ways from the identical typed objects (SC-008): the recorded
/// intent + provisioning-run trail for one environment (<c>run list</c>), or a single run's detail
/// (<c>run show</c>). This is "what did I ask for and what happened?" (intent/history) — distinct from
/// <c>pdp inventory</c>'s "what's deployed?" (ARG); the division of truth (FR-016).
/// </summary>
public static class RunView
{
    /// <summary>Renders an environment's intent header + its run audit trail (<c>run list</c>).</summary>
    public static void RenderTrail(
        EnvironmentRecord? environment,
        IReadOnlyList<RunRecord> runs,
        bool asJson,
        TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(new { environment, runs }, VerbResultView.Json));
            return;
        }

        if (environment is null)
        {
            writer.WriteLine("No such environment is registered.");
            return;
        }

        writer.WriteLine($"env_id   {environment.EnvId}");
        writer.WriteLine($"kind     {environment.Kind}");
        writer.WriteLine($"name     {environment.Name}  ({environment.Region}, sub {environment.Subscription})");
        writer.WriteLine($"status   {environment.Status}");
        if (environment.SpokeCidr is { } cidr)
        {
            writer.WriteLine($"cidr     {cidr}");
        }

        writer.WriteLine($"runs ({runs.Count}):");
        foreach (var run in runs)
        {
            writer.WriteLine(
                $"  {run.RunId}  {run.Phase,-7} {run.Outcome,-10} {run.WorkflowFile}" +
                (run.GitHubRunUrl is { } url ? $"  {url}" : string.Empty));
        }
    }

    /// <summary>Renders one provisioning run in full (<c>run show</c>).</summary>
    public static void RenderRun(string runId, RunRecord? run, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(run, VerbResultView.Json));
            return;
        }

        if (run is null)
        {
            writer.WriteLine($"No run '{runId}' is recorded.");
            return;
        }

        writer.WriteLine($"run_id        {run.RunId}");
        writer.WriteLine($"env_id        {run.EnvId}");
        writer.WriteLine($"phase         {run.Phase}");
        writer.WriteLine($"workflow      {run.WorkflowFile}");
        writer.WriteLine($"outcome       {run.Outcome}");
        if (run.GitHubRunId is { } id)
        {
            writer.WriteLine($"github_run    {id}");
        }

        if (run.GitHubRunUrl is { } url)
        {
            writer.WriteLine($"run_url       {url}");
        }

        if (run.TrackedBy is { } trackedBy)
        {
            writer.WriteLine($"tracked_by    {trackedBy}");
        }

        if (run.DispatchedAt is { } dispatched)
        {
            writer.WriteLine($"dispatched_at {dispatched:u}");
        }

        if (run.CompletedAt is { } completed)
        {
            writer.WriteLine($"completed_at  {completed:u}");
        }

        writer.WriteLine("dispatch inputs:");
        foreach (var (key, value) in run.DispatchInputs)
        {
            writer.WriteLine($"  {key} = {value}");
        }

        if (!string.IsNullOrWhiteSpace(run.PlanSummary))
        {
            writer.WriteLine("--- plan ---");
            writer.WriteLine(run.PlanSummary);
        }
    }
}
