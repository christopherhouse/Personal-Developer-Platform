using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pdp.Mcp.Hosting;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// The MCP-node startup gate (fix for the 2026-06-24 telemetry outage): the scale-to-zero MCP node never
/// provisions the shared <c>wolverine</c> message store, so if it cold-starts before the always-on Api
/// node builds the schema, Wolverine's Solo-mode startup throws <c>42P01</c> and ACA wedges the revision
/// in <c>ActivationFailed</c>. The gate converts that permanent crash into a bounded, self-healing wait.
/// These cover the wait/retry/timeout/resilience logic against a fake probe (the loop is the risky part;
/// the live Npgsql probe is a one-line catalog lookup). Tiny intervals keep the real-clock tests fast.
/// </summary>
public sealed class WolverineStoreReadinessGateTests
{
    private static WolverineStoreReadinessGate NewGate(IMessageStoreProbe probe, TimeSpan? timeout = null) =>
        new(
            probe,
            new WolverineStoreReadinessOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                Timeout = timeout ?? TimeSpan.FromSeconds(5),
            },
            TimeProvider.System,
            NullLogger<WolverineStoreReadinessGate>.Instance);

    [Fact]
    public async Task Completes_immediately_when_store_already_provisioned()
    {
        var probe = Substitute.For<IMessageStoreProbe>();
        probe.ExistsAsync(Arg.Any<CancellationToken>()).Returns(true);

        await NewGate(probe).StartAsync(CancellationToken.None);

        // Steady state: probed once, no waiting.
        await probe.Received(1).ExistsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Waits_then_completes_once_the_store_appears()
    {
        var probe = Substitute.For<IMessageStoreProbe>();
        // Absent for the first two probes (Api still provisioning), then present.
        probe.ExistsAsync(Arg.Any<CancellationToken>()).Returns(false, false, true);

        await NewGate(probe).StartAsync(CancellationToken.None);

        await probe.Received(3).ExistsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Treats_a_probe_error_as_not_ready_and_keeps_waiting()
    {
        var probe = Substitute.For<IMessageStoreProbe>();
        // A transient connection failure (DB warming / principal not yet granted) must not crash the gate.
        probe.ExistsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("connection refused"), _ => true);

        await NewGate(probe).StartAsync(CancellationToken.None);

        await probe.Received(2).ExistsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_TimeoutException_when_the_store_never_appears()
    {
        var probe = Substitute.For<IMessageStoreProbe>();
        probe.ExistsAsync(Arg.Any<CancellationToken>()).Returns(false);

        var gate = NewGate(probe, timeout: TimeSpan.FromMilliseconds(80));

        await Should.ThrowAsync<TimeoutException>(() => gate.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Honors_cancellation_while_waiting()
    {
        var probe = Substitute.For<IMessageStoreProbe>();
        probe.ExistsAsync(Arg.Any<CancellationToken>()).Returns(false);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => NewGate(probe).StartAsync(cts.Token));
    }
}
