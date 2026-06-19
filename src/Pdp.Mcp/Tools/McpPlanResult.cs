using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Mcp.Tools;

/// <summary>
/// What a <c>Plan*</c> tool returns to the owner (contracts/mcp-tool-surface.md §plan/confirm): the
/// verb-layer <see cref="PlanResult"/> (the surfaced plan / its run link — no mutation has happened) plus
/// the single-use <see cref="ConfirmationToken"/> and the <see cref="TargetName"/> that the paired
/// <c>Apply*</c>/<c>Destroy*</c> tool must restate verbatim. Immutable; <c>System.Text.Json</c> serializable.
/// </summary>
/// <param name="ConfirmationToken">The opaque, single-use, ~5-minute token binding the operation + target.</param>
/// <param name="TargetName">The exact name the confirming call must restate (spoke name or region).</param>
/// <param name="Plan">The verb-layer plan result (env_id, proposed inputs, captured plan/run link).</param>
public sealed record McpPlanResult(
    string ConfirmationToken,
    string TargetName,
    PlanResult Plan);
