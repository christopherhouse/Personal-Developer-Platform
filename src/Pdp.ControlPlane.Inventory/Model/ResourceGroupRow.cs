namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// One resource group as returned by <see cref="IResourceGraphReader"/>, projected from the ARG
/// <c>ResourceContainers</c> query (research §1, data-model §1). This is the <b>only</b> Azure-shaped
/// type in the component; everything downstream of it is a pure, in-memory projection. <see cref="Tags"/>
/// lookups are case-insensitive (Azure tag keys are) — build instances via <see cref="Create"/> so the
/// classifier and drift detector can match tag names regardless of the source casing.
/// </summary>
public sealed record ResourceGroupRow(
    string Id,
    string Name,
    string SubscriptionId,
    string Location,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>
    /// Builds a row whose <see cref="Tags"/> dictionary is case-insensitive regardless of the source
    /// comparer. The ARG adapter and the unit tests both use this so tag matching is identical.
    /// </summary>
    public static ResourceGroupRow Create(
        string id,
        string name,
        string subscriptionId,
        string location,
        IEnumerable<KeyValuePair<string, string>>? tags = null)
    {
        var caseInsensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                caseInsensitive[tag.Key] = tag.Value;
            }
        }

        return new ResourceGroupRow(id, name, subscriptionId, location, caseInsensitive);
    }
}
