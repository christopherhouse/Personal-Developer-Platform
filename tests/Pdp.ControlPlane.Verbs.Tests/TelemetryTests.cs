using System.Diagnostics;
using Pdp.ControlPlane.Verbs.Telemetry;
using Shouldly;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// SC-013 (T070): every verb invocation opens a trace <b>correlated by <c>env_id</c></b>, so a stuck or
/// failed run is traceable end to end from a single id (via the Azure Monitor OTel distro in the
/// spec-007 host; here, asserted directly off the <see cref="ActivitySource"/> with a listener — no live
/// Application Insights resource needed, matching "no new Azure resource provisioned by this spec").
/// </summary>
public sealed class TelemetryTests
{
    [Fact]
    public void StartVerb_emits_an_activity_tagged_with_env_id()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ControlPlaneTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var envId = Guid.CreateVersion7();
        using (var activity = ControlPlaneTelemetry.StartVerb("spoke.create", envId))
        {
            activity.ShouldNotBeNull();
            activity.DisplayName.ShouldBe("spoke.create");
        }

        var span = captured.ShouldHaveSingleItem();
        span.GetTagItem(ControlPlaneTelemetry.EnvIdTag).ShouldBe(envId);
    }

    [Fact]
    public void EnvId_tag_key_is_the_documented_correlation_key()
    {
        // The contract names the correlation tag explicitly so traces are queryable by env_id (SC-013).
        ControlPlaneTelemetry.EnvIdTag.ShouldBe("pdp.env_id");
    }
}
