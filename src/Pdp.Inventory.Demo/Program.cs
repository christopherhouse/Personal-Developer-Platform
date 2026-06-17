using System.Diagnostics;
using Azure.Identity;
using Azure.ResourceManager;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;

// Pdp.Inventory.Demo — the thin, demonstrable harness for spec 005 (FR-022). NOT the spec-006 `pdp`
// CLI. It supplies the owner's local credential (DefaultAzureCredential / `az login`) to the
// read-only inventory component and renders one live snapshot. Read-only throughout (Article III):
// it issues only Azure Resource Graph queries — no mutation, nothing to tear down.

// FR-019: the component takes an injected TokenCredential; the demo is the only place a concrete
// credential is constructed. Spec 006 injects a managed identity here instead, with no component change.
var credential = new DefaultAzureCredential();
var armClient = new ArmClient(credential);

var reader = new ResourceGraphReader(armClient);
IInventoryService inventory = new InventoryService(reader);

var emitJson = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
var measure = args.Contains("--time", StringComparer.OrdinalIgnoreCase);

if (measure)
{
    await MeasureLatencyAsync(inventory);
    return 0;
}

var snapshot = await inventory.GetSnapshotAsync();

if (emitJson)
{
    // FR-010 / SC-006: the same typed snapshot, serialized — no re-query, no text scraping. Pure
    // JSON to stdout so it is machine-consumable.
    Console.WriteLine(InventoryJson.Serialize(snapshot));
    return 0;
}

Console.WriteLine("PDP inventory — live from Azure Resource Graph (read-only)");
Console.WriteLine();

RenderCoverage(snapshot.Coverage);
RenderEnvironments(snapshot.Environments);
RenderSpokesBySubscriptionAndRegion(snapshot.Spokes);
RenderFabrics(snapshot.Fabrics);
RenderPlatform(snapshot.Platform);
RenderDrift(snapshot.Drift);
RenderHeadline(snapshot);

return 0;

static void RenderCoverage(IReadOnlyList<SubscriptionCoverage> coverage)
{
    Console.WriteLine($"Subscriptions discovered: {coverage.Count}");
    foreach (var subscription in coverage)
    {
        Console.WriteLine($"  [{subscription.Status}] {subscription.DisplayName} ({subscription.SubscriptionId})");
    }

    Console.WriteLine();
}

static void RenderEnvironments(IReadOnlyList<EnvironmentView> environments)
{
    Console.WriteLine($"Environments: {environments.Count}");
    foreach (var environment in environments)
    {
        Console.WriteLine($"  {environment.Name} — {environment.Workloads.Count} workload(s)");
        foreach (var workload in environment.Workloads)
        {
            Console.WriteLine($"      {workload.Name}  sub={workload.SubscriptionId}  region={workload.Region}  rg={workload.ResourceGroupName}");
        }
    }

    Console.WriteLine();
}

static void RenderSpokesBySubscriptionAndRegion(IReadOnlyList<SpokeItem> spokes)
{
    // US2 headline: "which spokes exist, in which subscription and region?" — grouped scoped view.
    Console.WriteLine($"Spokes by subscription / region: {spokes.Count} total");
    var groups = spokes
        .GroupBy(s => s.SubscriptionId, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
    foreach (var subscription in groups)
    {
        Console.WriteLine($"  subscription {subscription.Key}");
        foreach (var region in subscription.GroupBy(s => s.Region, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"    region {region.Key}: {string.Join(", ", region.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}");
        }
    }

    Console.WriteLine();
}

static void RenderFabrics(IReadOnlyList<FabricItem> fabrics)
{
    Console.WriteLine($"Fabrics: {fabrics.Count}");
    foreach (var fabric in fabrics)
    {
        Console.WriteLine($"  {fabric.Region}  sub={fabric.SubscriptionId}  location={fabric.Location}  rg={fabric.ResourceGroupName}");
    }

    Console.WriteLine();
}

static async Task MeasureLatencyAsync(IInventoryService inventory)
{
    // SC-005: full sweep P95 < 5 s; scoped query < 2 s. One warm-up (JIT + first ARG round-trip),
    // then timed samples.
    const int samples = 8;
    await inventory.GetSnapshotAsync();

    var full = new List<double>();
    for (var i = 0; i < samples; i++)
    {
        var sw = Stopwatch.StartNew();
        await inventory.GetSnapshotAsync();
        sw.Stop();
        full.Add(sw.Elapsed.TotalSeconds);
    }

    var scoped = new List<double>();
    for (var i = 0; i < samples; i++)
    {
        var sw = Stopwatch.StartNew();
        await inventory.GetSpokesAsync();
        sw.Stop();
        scoped.Add(sw.Elapsed.TotalSeconds);
    }

    Report("Full sweep  (GetSnapshotAsync)", full, budget: 5.0);
    Report("Scoped query (GetSpokesAsync) ", scoped, budget: 2.0);

    static void Report(string label, List<double> samples, double budget)
    {
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        var median = samples[samples.Count / 2];
        var verdict = p95 < budget ? "PASS" : "FAIL";
        Console.WriteLine($"{label}: median {median:F3}s  P95 {p95:F3}s  (budget {budget:F0}s) [{verdict}]");
    }
}

static void RenderPlatform(IReadOnlyList<PlatformItem> platform)
{
    Console.WriteLine($"Platform-shared: {platform.Count}");
    foreach (var item in platform)
    {
        Console.WriteLine($"  {item.ResourceGroupName}  sub={item.SubscriptionId}  region={item.Region}");
    }

    Console.WriteLine();
}

static void RenderDrift(IReadOnlyList<DriftFinding> drift)
{
    Console.WriteLine($"Drift findings (informational): {drift.Count}");
    foreach (var finding in drift.OrderBy(d => d.Category).ThenBy(d => d.ResourceGroupName, StringComparer.OrdinalIgnoreCase))
    {
        var tag = finding.OffendingTag is null ? string.Empty : $"  tag={finding.OffendingTag}";
        Console.WriteLine($"  [{finding.Category}] {finding.ResourceGroupName}  sub={finding.SubscriptionId}{tag}");
        Console.WriteLine($"      {finding.Detail}");
    }

    Console.WriteLine();
}

static void RenderHeadline(InventorySnapshot snapshot)
{
    Console.WriteLine("Headline:");
    Console.WriteLine($"  Environments deployed : {string.Join(", ", snapshot.Environments.Select(e => e.Name))}");
    Console.WriteLine($"  Spokes                : {snapshot.Spokes.Count}");
    Console.WriteLine($"  Fabrics               : {snapshot.Fabrics.Count}");
}
