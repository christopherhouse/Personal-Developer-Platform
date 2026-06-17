namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// Whether a discovered subscription was actually queried. A subscription the credential cannot read
/// is reported as <see cref="Inaccessible"/>, never silently dropped — coverage honesty (FR-011 /
/// SC-008).
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>The subscription was enumerated and its resource groups were read.</summary>
    Queried,

    /// <summary>The subscription was discovered but the credential could not read it.</summary>
    Inaccessible,
}

/// <summary>
/// One discovered subscription and whether the sweep could read it (data-model §5). The union of
/// <see cref="SubscriptionStatus.Queried"/> subscriptions is the scope all taxonomy and drift was
/// computed over (data-model §6 invariant).
/// </summary>
public sealed record SubscriptionCoverage(
    string SubscriptionId,
    string DisplayName,
    SubscriptionStatus Status);
