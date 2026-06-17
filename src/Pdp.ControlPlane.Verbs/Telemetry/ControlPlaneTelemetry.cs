using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pdp.ControlPlane.Verbs.Telemetry;

/// <summary>
/// Control-plane observability (FR-O1 / SC-013): a custom <see cref="ActivitySource"/> and
/// <see cref="Meter"/> plus the OpenTelemetry/Azure Monitor wiring. Every verb invocation opens an
/// <see cref="Activity"/> stamped with <c>env_id</c> so a stuck/failed run is debuggable end to end
/// (the whole point for an AI-operated dispatcher — research §11). When no Application Insights
/// connection string is configured (the MVP), the sources/meters are still registered but no Azure
/// sink is attached — the App Insights resource ships with the spec-007 host.
/// </summary>
public static class ControlPlaneTelemetry
{
    /// <summary>The name of the control-plane <see cref="ActivitySource"/>/<see cref="Meter"/>.</summary>
    public const string SourceName = "Pdp.ControlPlane";

    /// <summary>The tag key under which <c>env_id</c> is stamped on every span (correlation key).</summary>
    public const string EnvIdTag = "pdp.env_id";

    /// <summary>The control-plane activity source (verb invocation → dispatch → run-state transition).</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>The control-plane meter (dispatch counts, reconcile sweeps, latencies).</summary>
    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// Starts an activity for a verb invocation, stamped with the <c>env_id</c> correlation key.
    /// Returns null when no listener is registered (the BCL no-ops the activity).
    /// </summary>
    public static Activity? StartVerb(string verbName, Guid envId)
    {
        var activity = ActivitySource.StartActivity(verbName, ActivityKind.Internal);
        activity?.SetTag(EnvIdTag, envId);
        return activity;
    }

    /// <summary>
    /// Registers OpenTelemetry tracing/metrics for the control-plane source/meter and, when an
    /// Application Insights connection string is present, exports to Azure Monitor.
    /// </summary>
    public static IServiceCollection AddControlPlaneTelemetry(
        this IServiceCollection services,
        ControlPlaneOptions options)
    {
        var otel = services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(SourceName))
            .WithMetrics(metrics => metrics.AddMeter(SourceName));

        if (!string.IsNullOrWhiteSpace(options.ApplicationInsightsConnectionString))
        {
            otel.UseAzureMonitor(o => o.ConnectionString = options.ApplicationInsightsConnectionString);
        }

        return services;
    }
}
