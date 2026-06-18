using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// Kicks off the recurring <see cref="ReconcileSweep"/> when a long-lived host starts (the spec-007 Api
/// host). The first sweep is scheduled one interval out; <see cref="RunReconciler"/> reschedules itself
/// thereafter, so the durable poll runs forever on Wolverine's scheduled-message machinery and guarantees
/// completion even when a <c>workflow_run</c> webhook is missed (SC-006). A one-shot CLI process closes
/// its own loop (<c>CompletionPoller</c>) and does not register this. Scheduling is best-effort at
/// startup: the durable queue persists the message, so a transient enqueue failure is logged, not fatal.
/// </summary>
public sealed class ReconcilerScheduler(
    IServiceProvider services,
    ILogger<ReconcilerScheduler> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve through a scope: IMessageBus is scoped, and a hosted service is a singleton.
            await using var scope = services.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            var options = scope.ServiceProvider.GetRequiredService<ReconcilerOptions>();
            await bus.ScheduleAsync(new ReconcileSweep(), options.Interval).ConfigureAwait(false);
            logger.LogInformation(
                "Scheduled the first reconcile sweep in {Interval}.", options.Interval);
        }
        catch (Exception ex)
        {
            // The recurring sweep is the missed-webhook safety net; failing to seed it must not crash the
            // host. The next host start (or a manual reconcile) re-seeds it.
            logger.LogWarning(ex, "Failed to schedule the initial reconcile sweep at startup.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
