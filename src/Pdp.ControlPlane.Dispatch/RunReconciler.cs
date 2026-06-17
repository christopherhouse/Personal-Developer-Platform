using Wolverine;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// The recurring reconcile signal (research §5). The host kicks the first one at startup; the handler
/// reschedules itself, so the sweep runs durably forever on Wolverine's scheduled-message machinery
/// (no hosted timer to babysit). It guarantees completion even when a <c>workflow_run</c> webhook is
/// missed — a stranded run reaches recorded terminal status within ~2 min (SC-006). For the MVP (no
/// public ingress) this is the sole completion path.
/// </summary>
public sealed record ReconcileSweep;

/// <summary>Tunable cadence for the <see cref="ReconcileSweep"/> (≤ 60 s — SC-006).</summary>
public sealed class ReconcilerOptions
{
    /// <summary>How often the reconciler sweeps in-flight runs. Defaults to 60 s.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Handles a <see cref="ReconcileSweep"/>: advances any stranded in-flight runs to a recorded terminal
/// outcome via <see cref="IRunTracker.ReconcileInFlightAsync"/>, then schedules the next sweep.
/// </summary>
public static class RunReconciler
{
    /// <summary>Runs one sweep and reschedules the next at the configured interval.</summary>
    public static async Task Handle(
        ReconcileSweep sweep,
        IRunTracker tracker,
        IMessageContext context,
        ReconcilerOptions options,
        CancellationToken cancellationToken)
    {
        await tracker.ReconcileInFlightAsync(cancellationToken).ConfigureAwait(false);
        await context.ScheduleAsync(new ReconcileSweep(), options.Interval).ConfigureAwait(false);
    }
}
