using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory.Classification;

/// <summary>
/// Produces the informational tag-side drift findings (data-model §4): orphan / ambiguous from
/// classification, conformance from <see cref="TagSchema"/>, and invisible from the looks-managed-but-
/// untagged candidates. Pure and total — it never throws on data, so the inventory always succeeds
/// regardless of how much drift exists (FR-016). The owner-managed seed backend is excluded
/// everywhere (FR-018).
/// </summary>
public static class DriftDetector
{
    /// <summary>
    /// All findings for one sweep: conformance/orphan/ambiguous over the managed rows, plus invisible
    /// over the <c>rg-pdp-*</c>-named untagged candidates.
    /// </summary>
    public static IReadOnlyList<DriftFinding> Detect(
        IEnumerable<ResourceGroupRow> managedRows,
        IEnumerable<ResourceGroupRow> invisibleCandidates)
    {
        ArgumentNullException.ThrowIfNull(managedRows);
        ArgumentNullException.ThrowIfNull(invisibleCandidates);

        var findings = new List<DriftFinding>();

        foreach (var row in managedRows)
        {
            findings.AddRange(DetectManaged(row));
        }

        foreach (var row in invisibleCandidates)
        {
            var invisible = DetectInvisible(row);
            if (invisible is not null)
            {
                findings.Add(invisible);
            }
        }

        return findings;
    }

    /// <summary>Orphan / ambiguous / conformance findings for a single managed RG.</summary>
    public static IEnumerable<DriftFinding> DetectManaged(ResourceGroupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (TagSchema.IsSeedBackend(row.Name))
        {
            yield break;
        }

        var classification = ResourceGroupClassifier.Classify(row);

        switch (classification.Kind)
        {
            case ClassificationKind.Orphan:
                yield return Finding(DriftCategory.Orphan, row, offendingTag: null,
                    "Managed resource group carries no scope tag (pdp-fabric / pdp-platform / pdp-spoke / pdp-workload).");
                break;
            case ClassificationKind.Ambiguous:
                var present = TagSchema.ScopeTags.Where(t => row.Tags.ContainsKey(t));
                yield return Finding(DriftCategory.Ambiguous, row, offendingTag: null,
                    $"Managed resource group carries conflicting scope tags: {string.Join(", ", present)}.");
                break;
        }

        foreach (var conformance in DetectConformance(row))
        {
            yield return conformance;
        }
    }

    /// <summary>An invisible finding for an <c>rg-pdp-*</c>-named RG lacking <c>pdp-managed == 'true'</c>.</summary>
    public static DriftFinding? DetectInvisible(ResourceGroupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (TagSchema.IsSeedBackend(row.Name))
        {
            return null;
        }

        return Finding(DriftCategory.Invisible, row, TagSchema.Managed,
            "Resource group name matches 'rg-pdp-*' but it is not tagged pdp-managed == 'true'.");
    }

    private static IEnumerable<DriftFinding> DetectConformance(ResourceGroupRow row)
    {
        // Universal tags must be present on every managed RG.
        if (!row.Tags.ContainsKey(TagSchema.DeployedBy))
        {
            yield return Finding(DriftCategory.Conformance, row, TagSchema.DeployedBy,
                "Missing required universal tag pdp-deployed-by.");
        }
        else if (!TagSchema.IsValidDeployedBy(row.Tags[TagSchema.DeployedBy]))
        {
            yield return Finding(DriftCategory.Conformance, row, TagSchema.DeployedBy,
                $"pdp-deployed-by value '{row.Tags[TagSchema.DeployedBy]}' is not one of github-actions / control-plane / owner.");
        }

        if (row.Tags.TryGetValue(TagSchema.Fabric, out var fabricRegion))
        {
            if (!TagSchema.IsKnownRegion(fabricRegion))
            {
                yield return Finding(DriftCategory.Conformance, row, TagSchema.Fabric,
                    $"pdp-fabric value '{fabricRegion}' is not a known Azure region.");
            }
            else if (!string.Equals(fabricRegion, row.Location, StringComparison.OrdinalIgnoreCase))
            {
                yield return Finding(DriftCategory.Conformance, row, TagSchema.Fabric,
                    $"pdp-fabric region '{fabricRegion}' does not match the resource group location '{row.Location}'.");
            }
        }

        if (row.Tags.TryGetValue(TagSchema.Platform, out var platformValue) && !TagSchema.IsValidPlatformValue(platformValue))
        {
            yield return Finding(DriftCategory.Conformance, row, TagSchema.Platform,
                $"pdp-platform value '{platformValue}' must be 'true'.");
        }

        if (row.Tags.TryGetValue(TagSchema.Spoke, out var spokeName) && !TagSchema.IsValidResourceName(spokeName))
        {
            yield return Finding(DriftCategory.Conformance, row, TagSchema.Spoke,
                $"pdp-spoke value '{spokeName}' does not match [a-z0-9-]{{1,24}}.");
        }

        if (row.Tags.TryGetValue(TagSchema.Workload, out var workloadName))
        {
            if (!TagSchema.IsValidResourceName(workloadName))
            {
                yield return Finding(DriftCategory.Conformance, row, TagSchema.Workload,
                    $"pdp-workload value '{workloadName}' does not match [a-z0-9-]{{1,24}}.");
            }

            if (!row.Tags.ContainsKey(TagSchema.Env))
            {
                yield return Finding(DriftCategory.Conformance, row, TagSchema.Env,
                    "Workload resource group is missing the pdp-env grouping tag.");
            }
        }

        if (row.Tags.TryGetValue(TagSchema.Env, out var env) && !TagSchema.IsValidEnvName(env))
        {
            yield return Finding(DriftCategory.Conformance, row, TagSchema.Env,
                $"pdp-env value '{env}' does not match [a-z0-9-]{{1,16}}.");
        }
    }

    private static DriftFinding Finding(DriftCategory category, ResourceGroupRow row, string? offendingTag, string detail) =>
        new(category, row.Id, row.Name, row.SubscriptionId, offendingTag, detail);
}
