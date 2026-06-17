namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// A named environment (the <c>pdp-env</c> grouping key) and the workloads tagged into it
/// (data-model §5). Answers "what environments do I have?" (by <see cref="Name"/>) and "what is in
/// environment X?" (by <see cref="Workloads"/>) — FR-005/007/008.
/// </summary>
public sealed record EnvironmentView(
    string Name,
    IReadOnlyList<WorkloadItem> Workloads);
