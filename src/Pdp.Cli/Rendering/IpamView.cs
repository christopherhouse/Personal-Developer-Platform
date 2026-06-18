using System.Text.Json;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.Cli.Rendering;

/// <summary>
/// Renders IPAM results two ways from the identical typed object (SC-008): a grouped human table
/// (default) or <c>--json</c> (the same <see cref="RegionView"/>/<see cref="Allocation"/>). Neither
/// re-queries — the CLI is a thin adapter over the verb layer (contracts/cli-surface.md §3).
/// </summary>
public static class IpamView
{
    /// <summary>Renders a single freshly-allocated block (<c>ipam allocate</c>).</summary>
    public static void RenderAllocation(Allocation allocation, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(allocation, VerbResultView.Json));
            return;
        }

        writer.WriteLine($"name     {allocation.Name}");
        writer.WriteLine($"network  {allocation.Network}");
        writer.WriteLine($"size     /{allocation.PrefixLength}");
        writer.WriteLine($"kind     {allocation.Kind}");
    }

    /// <summary>Renders one region's utilization (<c>ipam query --region</c>).</summary>
    public static void RenderRegion(RegionView region, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(region, VerbResultView.Json));
            return;
        }

        WriteRegion(region, writer);
    }

    /// <summary>Renders every registered region (<c>ipam query</c>).</summary>
    public static void RenderRegions(IReadOnlyList<RegionView> regions, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(regions, VerbResultView.Json));
            return;
        }

        for (var i = 0; i < regions.Count; i++)
        {
            if (i > 0)
            {
                writer.WriteLine();
            }

            WriteRegion(regions[i], writer);
        }
    }

    private static void WriteRegion(RegionView region, TextWriter writer)
    {
        writer.WriteLine($"region   {region.Region} (index {region.RegionIndex})");
        writer.WriteLine($"supernet {region.Supernet}");
        writer.WriteLine($"hub      {(region.HubCarveout?.ToString() ?? "(none — platform pool)")}");

        writer.WriteLine($"allocations ({region.Allocations.Count}):");
        foreach (var allocation in region.Allocations)
        {
            writer.WriteLine($"  {allocation.Network,-18} {allocation.Name} ({allocation.Kind})");
        }

        writer.WriteLine($"free     {region.Free.AddressCount} addresses across {region.Free.Blocks.Count} block(s)");
        foreach (var block in region.Free.Blocks)
        {
            writer.WriteLine($"  {block}");
        }
    }
}
