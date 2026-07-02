using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Mcp.Tools;

/// <summary>
/// What <c>PlanResetEnvironment</c> returns to the owner (issue #48): the verb-layer
/// <see cref="ResetPreview"/> (the wedged environment's status + latest run — nothing has changed) plus the
/// single-use <see cref="ConfirmationToken"/> and the <see cref="TargetName"/> that <c>ResetEnvironment</c>
/// must restate verbatim (Article VIII). Immutable; <c>System.Text.Json</c> serializable.
/// </summary>
/// <param name="ConfirmationToken">The opaque, single-use, ~15-minute token binding the reset + target.</param>
/// <param name="TargetName">The exact name the confirming reset must restate (spoke name or region).</param>
/// <param name="Preview">The verb-layer reset preview (env_id, status, latest run, notes).</param>
public sealed record McpResetPlan(
    string ConfirmationToken,
    string TargetName,
    ResetPreview Preview);
