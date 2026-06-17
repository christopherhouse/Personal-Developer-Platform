namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// The tag-side drift categories surfaced informationally by the inventory (data-model §4). All four
/// are <b>findings, not failures</b> — their presence never fails a snapshot (FR-016 / clarify Q5).
/// </summary>
public enum DriftCategory
{
    /// <summary>Managed RG with zero scope tags — unclassifiable into the taxonomy (FR-012).</summary>
    Orphan,

    /// <summary>A bad tag value or a missing required tag on an otherwise managed RG (FR-013).</summary>
    Conformance,

    /// <summary>An <c>rg-pdp-*</c>-named RG that lacks <c>pdp-managed == 'true'</c> (FR-014).</summary>
    Invisible,

    /// <summary>Managed RG carrying more than one scope tag — conflicting classification (FR-015).</summary>
    Ambiguous,
}
