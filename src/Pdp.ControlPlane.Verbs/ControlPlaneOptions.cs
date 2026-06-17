namespace Pdp.ControlPlane.Verbs;

/// <summary>
/// Top-level configuration for the control plane (bound from <see cref="SectionName"/>). Holds the
/// platform Postgres connection (the shared server hosting the <c>ipam</c>, <c>registry</c>, and
/// <c>wolverine</c> schemas — research §7), the observability sink, and the reconciler cadence. The
/// GitHub App credential is configured separately via <c>GitHubApp</c>
/// (<see cref="Dispatch.GitHubAppOptions"/>).
/// </summary>
public sealed class ControlPlaneOptions
{
    /// <summary>Configuration section name (<c>ControlPlane</c>).</summary>
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// The platform Postgres connection string. One DB, one connection — enabling the atomic
    /// allocate-record-dispatch transaction across the <c>ipam</c> + <c>registry</c> schemas
    /// (research §6).
    /// </summary>
    public string PostgresConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Application Insights connection string for Azure Monitor OTel export (SC-013). When null, the
    /// MVP falls back to the console/OTLP exporter — no Azure resource is provisioned here (the App
    /// Insights resource ships with the spec-007 host — FR-O1).
    /// </summary>
    public string? ApplicationInsightsConnectionString { get; set; }

    /// <summary>
    /// How often the polling reconciler sweeps in-flight runs (≤ 60 s — SC-006). The reconciler
    /// guarantees completion even when a webhook delivery is missed.
    /// </summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(60);
}
