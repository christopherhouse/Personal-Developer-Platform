using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Pdp.ControlPlane.Dispatch.Tests;

/// <summary>
/// SC-012 (T070a, resolves analysis G1): a verb <b>acknowledges and begins tracking</b> a dispatch
/// within a small budget — target <b>P95 &lt; 5 s</b> — <i>independent of the underlying IaC run
/// duration</i>, because the verb returns once intent is recorded and the <c>workflow_dispatch</c> is
/// enqueued on the durable outbox (the GitHub call and the run itself happen out of band). Failure modes
/// return a clear error in the same budget <b>without dispatching</b>. GitHub is faked (WireMock) and the
/// real <see cref="GitHubWorkflowDispatcher"/> targets it, so the path is end-to-end on real Postgres.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class DispatchLatencyTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "88888888-8888-8888-8888-888888888888";

    // The control-plane dispatch-ack budget (SC-012). Generous headroom over the observed sub-second
    // ack so the assertion stays stable on a loaded CI agent without masking a real regression.
    private static readonly TimeSpan AckBudget = TimeSpan.FromSeconds(5);

    private readonly FakeGitHubServer _gitHub = new();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services => services.AddSingleton<IGitHubAppCredential>(new StubGitHubAppCredential(_gitHub.BaseUrl)));

        // The real dispatcher posts the workflow_dispatch to the fake GitHub out of band; ack timing must
        // not depend on it, so a fast 204 keeps the background send from ever backing up the outbox.
        _gitHub.Server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Dispatch_ack_returns_within_the_P95_budget()
    {
        using (var warmup = _host.Services.CreateScope())
        {
            await warmup.ServiceProvider.GetRequiredService<IIpamLedger>().RegisterRegionAsync("westus3", 2);
            // Warm the saga/handler/outbox path so the first measured ack is not paying JIT/compile cost.
            await CreateAsync(warmup.ServiceProvider, "warmup");
        }

        const int samples = 20;
        var durations = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            using var scope = _host.Services.CreateScope();
            var stopwatch = Stopwatch.StartNew();
            await CreateAsync(scope.ServiceProvider, $"app-lat-{i}");
            stopwatch.Stop();
            durations.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        durations.Sort();
        // P95: with 20 samples the 19th (index 18) value, so the slowest single ack may exceed and the
        // budget still holds — exactly the "P95" intent.
        var p95Index = (int)Math.Ceiling(0.95 * samples) - 1;
        var p95 = durations[p95Index];

        p95.ShouldBeLessThan(
            AckBudget.TotalMilliseconds,
            $"dispatch-ack P95 was {p95:F0} ms (samples: {string.Join(", ", durations.Select(d => $"{d:F0}"))})");
    }

    [Fact]
    public async Task Fail_fast_error_returns_within_budget_without_dispatching()
    {
        using var scope = _host.Services.CreateScope();
        var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

        // The region is deliberately NOT registered: the verb must fail fast on the precondition, before
        // any allocation or dispatch (FR-023). Measured the same way as the happy path.
        var stopwatch = Stopwatch.StartNew();
        await Should.ThrowAsync<RegionNotRegisteredException>(() => verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "westus3", "app-nope", Size: 24),
            Confirmation.ForApply()));
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(AckBudget, $"fail-fast took {stopwatch.ElapsedMilliseconds} ms");

        // Nothing was dispatched: the fake GitHub saw no workflow_dispatch POST.
        _gitHub.Server.LogEntries
            .Count(e => e.RequestMessage?.Method == "POST")
            .ShouldBe(0);
    }

    private static async Task CreateAsync(IServiceProvider services, string name)
    {
        var verbs = services.GetRequiredService<ISpokeVerbs>();
        await verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "westus3", name, Size: 24),
            Confirmation.ForApply());
    }
}
