using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Verbs.Model;

/// <summary>
/// What a <c>PlanReset</c> verb surfaces to the owner <b>before</b> a recovery reset (issue #48): the
/// wedged environment's identity, its current <b>non-terminal</b> lifecycle status, and its latest
/// provisioning run so the owner can judge — from the run's phase/outcome/age — that it is genuinely
/// stuck (its dispatch died without ever recording terminal) and not merely mid-flight. Mutates nothing.
/// A reset only unwedges the registry single-flight (FR-022a); it does <b>not</b> touch Azure resources or
/// the IPAM allocation, which the owner then tears down through the normal destroy verb. Immutable;
/// <c>System.Text.Json</c> serializable.
/// </summary>
/// <param name="EnvId">The wedged environment's surrogate id.</param>
/// <param name="Kind">Fabric or spoke.</param>
/// <param name="Name">The target name the confirming reset must restate (spoke name or region).</param>
/// <param name="CurrentStatus">The non-terminal status the reset would clear (Requested/Provisioning/Destroying).</param>
/// <param name="LatestRun">The most-recent provisioning run, so the owner can confirm it is stranded; null if none.</param>
/// <param name="Notes">Human-facing notes describing what the reset will (and will not) do.</param>
public sealed record ResetPreview(
    Guid EnvId,
    EnvironmentKind Kind,
    string Name,
    EnvironmentStatus CurrentStatus,
    RunRecord? LatestRun,
    IReadOnlyList<string> Notes);
