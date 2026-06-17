using System.Text.Json;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Pdp.ControlPlane.Inventory.Model;
using Pdp.ControlPlane.Inventory.Queries;

namespace Pdp.ControlPlane.Inventory;

/// <summary>
/// The production <see cref="IResourceGraphReader"/> — a thin adapter over
/// <c>Azure.ResourceManager.ResourceGraph</c> (research §2). It is the single Azure-touching type; the
/// classification / grouping / drift engine never sees an Azure SDK type. The <see cref="ArmClient"/>
/// (and therefore the <c>TokenCredential</c>) is <b>injected</b> (FR-019) — the reader never constructs
/// a credential or hardcodes subscriptions. Read-only: it issues only ARG queries (Article III).
/// </summary>
public sealed class ResourceGraphReader : IResourceGraphReader
{
    private readonly ArmClient _armClient;

    /// <param name="armClient">An <see cref="ArmClient"/> built over the caller's credential (FR-019).</param>
    public ResourceGraphReader(ArmClient armClient)
    {
        ArgumentNullException.ThrowIfNull(armClient);
        _armClient = armClient;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriptionCoverage>> DiscoverSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var coverage = new List<SubscriptionCoverage>();

        await foreach (var subscription in _armClient.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var data = subscription.Data;
            // A subscription the credential can enumerate but that is not Enabled cannot be reliably
            // queried — report it as Inaccessible rather than silently dropping it (FR-011 / SC-008).
            var status = data.State == SubscriptionState.Enabled
                ? SubscriptionStatus.Queried
                : SubscriptionStatus.Inaccessible;

            coverage.Add(new SubscriptionCoverage(
                data.SubscriptionId ?? string.Empty,
                data.DisplayName ?? string.Empty,
                status));
        }

        return coverage;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ResourceGroupRow>> QueryManagedResourceGroupsAsync(
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken = default) =>
        RunPagedQueryAsync(InventoryQueries.ManagedResourceGroups, subscriptionIds, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ResourceGroupRow>> QueryLooksManagedUntaggedAsync(
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken = default) =>
        RunPagedQueryAsync(InventoryQueries.LooksManagedUntagged, subscriptionIds, cancellationToken);

    /// <summary>
    /// Runs <paramref name="query"/> scoped to the given subscriptions, paging to completion via
    /// <c>SkipToken</c> (no silent 1000-row truncation; research §2). <c>AllowPartialScopes</c> keeps a
    /// single unreadable scope from failing the sweep (coverage honesty). An empty scope list short-
    /// circuits to an empty result (ARG rejects a scopeless query).
    /// </summary>
    private async Task<IReadOnlyList<ResourceGroupRow>> RunPagedQueryAsync(
        string query,
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriptionIds);
        if (subscriptionIds.Count == 0)
        {
            return [];
        }

        var tenant = await GetTenantAsync(cancellationToken).ConfigureAwait(false);

        var content = new ResourceQueryContent(query)
        {
            Options = new ResourceQueryRequestOptions
            {
                ResultFormat = ResultFormat.ObjectArray,
                AllowPartialScopes = true,
            },
        };
        foreach (var subscriptionId in subscriptionIds)
        {
            content.Subscriptions.Add(subscriptionId);
        }

        var rows = new List<ResourceGroupRow>();
        string? skipToken = null;
        do
        {
            content.Options.SkipToken = skipToken;
            var response = await tenant.GetResourcesAsync(content, cancellationToken).ConfigureAwait(false);
            rows.AddRange(ParseRows(response.Value.Data));
            skipToken = response.Value.SkipToken;
        }
        while (!string.IsNullOrEmpty(skipToken));

        return rows;
    }

    private async Task<TenantResource> GetTenantAsync(CancellationToken cancellationToken)
    {
        await foreach (var tenant in _armClient.GetTenants().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            return tenant;
        }

        throw new InvalidOperationException("No accessible tenant for the injected credential.");
    }

    /// <summary>Parses an ARG <c>ObjectArray</c> result payload into typed rows.</summary>
    private static IEnumerable<ResourceGroupRow> ParseRows(BinaryData data)
    {
        using var document = JsonDocument.Parse(data);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tagsElement.EnumerateObject())
                {
                    if (tag.Value.ValueKind == JsonValueKind.String)
                    {
                        tags[tag.Name] = tag.Value.GetString()!;
                    }
                }
            }

            yield return new ResourceGroupRow(
                GetString(element, "id"),
                GetString(element, "name"),
                GetString(element, "subscriptionId"),
                GetString(element, "location"),
                tags);
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;
}
