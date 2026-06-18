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
    /// The platform subscription that hosts the regional fabrics (spec 003). A fabric environment is
    /// identified by <c>(Fabric, PlatformSubscriptionId, Region)</c> in the registry (data-model §1) —
    /// the fabric has no per-spoke target subscription. Defaults to the live platform subscription
    /// (the public id in <c>infra/fabric/variables.tf</c>); override per environment via configuration.
    /// </summary>
    public string PlatformSubscriptionId { get; set; } = "8bd05b2f-62c5-4def-9869-f0617ebb3970";

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
