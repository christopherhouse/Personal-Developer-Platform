using System.Text.Json;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Rendering;

/// <summary>
/// Renders a <see cref="PlanResult"/> two ways from the identical object (SC-008): the captured
/// <c>tofu plan</c> output + the exact inputs a confirmed mutation would dispatch (default human form),
/// or <c>--json</c>. This is what the owner reviews <b>before</b> confirming an apply/destroy (Article
/// VIII / FR-006). When the plan summary could not be captured, the run URL is surfaced for review in
/// GitHub — a missing plan is never treated as approved.
/// </summary>
public static class PlanResultView
{
    /// <summary>Writes <paramref name="plan"/> to <paramref name="writer"/> as JSON or a human table.</summary>
    public static void Render(PlanResult plan, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(plan, VerbResultView.Json));
            return;
        }

        writer.WriteLine($"env_id   {plan.EnvId}");
        writer.WriteLine($"phase    {plan.Phase}");
        writer.WriteLine("proposed inputs:");
        foreach (var (key, value) in plan.ProposedInputs)
        {
            writer.WriteLine($"  {key} = {value}");
        }

        writer.WriteLine("--- plan ---");
        writer.WriteLine(string.IsNullOrWhiteSpace(plan.PlanSummary)
            ? $"(plan summary unavailable — review the run at {plan.RunUrl ?? "GitHub Actions"} before confirming)"
            : plan.PlanSummary);
    }
}
