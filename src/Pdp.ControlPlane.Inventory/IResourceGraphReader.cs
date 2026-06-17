using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory;

/// <summary>
/// The single Azure-touching abstraction (contracts §2) — the testability seam. The production
/// adapter wraps <c>Azure.ResourceManager.ResourceGraph</c>; unit tests substitute it with NSubstitute
/// to drive the pure engine with fabricated rows (no Azure). It receives its
/// <c>Azure.Core.TokenCredential</c>/<c>ArmClient</c> by injection (FR-019) and never constructs a
/// credential or hardcodes subscriptions.
/// </summary>
public interface IResourceGraphReader
{
    /// <summary>
    /// The subscriptions the injected credential can enumerate (FR-001), each marked
    /// <see cref="SubscriptionStatus.Queried"/> or <see cref="SubscriptionStatus.Inaccessible"/>
    /// (coverage honesty — FR-011). MUST mark unreadable subscriptions rather than throw.
    /// </summary>
    Task<IReadOnlyList<SubscriptionCoverage>> DiscoverSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every <c>pdp-managed == 'true'</c> resource group across the given subscription scope,
    /// fully paged via <c>SkipToken</c> — no silent 1000-row truncation (FR-003; research §2).
    /// </summary>
    Task<IReadOnlyList<ResourceGroupRow>> QueryManagedResourceGroupsAsync(
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>rg-pdp-*</c>-named resource groups that lack <c>pdp-managed == 'true'</c> — the
    /// invisible-drift candidates (FR-014; research §1). A separate, narrower projection.
    /// </summary>
    Task<IReadOnlyList<ResourceGroupRow>> QueryLooksManagedUntaggedAsync(
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken = default);
}
