using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory.Classification;

/// <summary>The taxonomy bucket a managed RG falls into (data-model §3).</summary>
public enum ClassificationKind
{
    /// <summary>Exactly one scope tag, <c>pdp-fabric</c>.</summary>
    Fabric,

    /// <summary>Exactly one scope tag, <c>pdp-platform</c> — platform-shared infrastructure.</summary>
    Platform,

    /// <summary>Exactly one scope tag, <c>pdp-spoke</c>.</summary>
    Spoke,

    /// <summary>Exactly one scope tag, <c>pdp-workload</c>.</summary>
    Workload,

    /// <summary>Zero scope tags — managed but unclassifiable (orphan drift, FR-012).</summary>
    Orphan,

    /// <summary>More than one scope tag — conflicting classification (ambiguous drift, FR-015).</summary>
    Ambiguous,
}

/// <summary>
/// The deterministic verdict for one managed RG: its <see cref="Kind"/> and, when classified, exactly
/// one populated taxonomy item. <see cref="Orphan"/>/<see cref="Ambiguous"/> carry no item (they yield
/// a drift finding instead — Phase 5). Pure value; produced by <see cref="ResourceGroupClassifier"/>.
/// </summary>
public sealed record ClassificationResult(
    ClassificationKind Kind,
    FabricItem? Fabric = null,
    PlatformItem? Platform = null,
    SpokeItem? Spoke = null,
    WorkloadItem? Workload = null);
