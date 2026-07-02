using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Registry.Lifecycle;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;
using Wolverine;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// Spec-008 US1 (T021): the workload deploy verbs on real Postgres — the R3 validation order rejects
/// <b>before</b> any intent or dispatch (schema violations, unknown/retired archetypes,
/// missing/inactive spokes), the newest active version is resolved and permanently stamped (FR-005),
/// repeat deploys converge only on identical configuration, and the Article VIII plan→confirm gate
/// dispatches the apply only after confirmation. GitHub is faked at the dispatcher seam.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class WorkloadVerbsTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "88888888-8888-8888-8888-888888888888";
    private const string SpokeName = "app1";
    private const string ArchetypeName = "container-app-sql";

    private const string ParameterSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["containerImage"],
          "properties": {
            "containerImage": { "type": "string", "minLength": 1 },
            "cpu": { "type": "number", "enum": [0.25, 0.5, 1.0] }
          }
        }
        """;

    private readonly IWorkflowDispatcher _dispatcher = Substitute.For<IWorkflowDispatcher>();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services => services.AddSingleton(_dispatcher));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static WorkloadDeployRequest Request(
        string name = "demo-api",
        string archetype = ArchetypeName,
        JsonObject? parameters = null) =>
        new(Subscription, SpokeName, name, archetype, "dev",
            parameters ?? new JsonObject { ["containerImage"] = "nginx:latest" });

    private async Task SeedCatalogAsync(
        ArchetypeStatus status = ArchetypeStatus.Active,
        params string[] versions)
    {
        await using var registry = fixture.CreateRegistryContext();
        var archetype = new Archetype
        {
            Name = ArchetypeName,
            Description = "Container app + SQL serverless.",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var version in versions.Length > 0 ? versions : ["v1.0.0"])
        {
            archetype.Versions.Add(new ArchetypeVersion
            {
                ArchetypeName = ArchetypeName,
                Version = version,
                ModulePath = "archetypes/container-app-sql",
                ParameterSchema = ParameterSchema,
                ContentHash = $"hash-{version}",
                RegisteredAt = DateTimeOffset.UtcNow,
            });
        }

        registry.Archetypes.Add(archetype);
        await registry.SaveChangesAsync();
    }

    private async Task SeedSpokeAsync(EnvironmentStatus status = EnvironmentStatus.Active)
    {
        await using var registry = fixture.CreateRegistryContext();
        registry.Environments.Add(new Environment
        {
            EnvId = Guid.CreateVersion7(),
            Kind = EnvironmentKind.Spoke,
            Subscription = Subscription,
            Region = "westus3",
            Name = SpokeName,
            Owner = "owner",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await registry.SaveChangesAsync();
    }

    private IWorkloadVerbs Verbs(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkloadVerbs>();

    // --- R3 rejections: nothing recorded, nothing dispatched --------------------------------------

    [Fact]
    public async Task Schema_violation_rejects_before_any_intent_or_dispatch()
    {
        await SeedCatalogAsync();
        await SeedSpokeAsync();

        using var scope = _host.Services.CreateScope();
        var invalid = Request(parameters: new JsonObject
        {
            ["containerImage"] = "nginx:latest",
            ["cpu"] = 3,
        });

        var ex = await Should.ThrowAsync<WorkloadParameterValidationException>(
            () => Verbs(scope).PlanDeployAsync(invalid));

        // The violation names the offending parameter and the resolved version (FR-003).
        ex.Version.ShouldBe("v1.0.0");
        ex.Violations.ShouldContain(v => v.Path.Contains("cpu"));

        // No intent row (AS3/SC-003) — the spoke row is the only environment — and no dispatch.
        await using var registry = fixture.CreateRegistryContext();
        (await registry.Environments.CountAsync(e => e.Kind == EnvironmentKind.Workload)).ShouldBe(0);
        (await registry.Workloads.CountAsync()).ShouldBe(0);
        await _dispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default);
    }

    [Fact]
    public async Task Unknown_parameter_is_rejected_by_additionalProperties()
    {
        await SeedCatalogAsync();
        await SeedSpokeAsync();

        using var scope = _host.Services.CreateScope();
        var invalid = Request(parameters: new JsonObject
        {
            ["containerImage"] = "nginx:latest",
            ["notAParameter"] = true,
        });

        var ex = await Should.ThrowAsync<WorkloadParameterValidationException>(
            () => Verbs(scope).PlanDeployAsync(invalid));
        ex.Violations.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Unknown_and_retired_archetypes_are_refused_distinctly()
    {
        await SeedSpokeAsync();

        using (var scope = _host.Services.CreateScope())
        {
            var unknown = await Should.ThrowAsync<ArchetypeNotDeployableException>(
                () => Verbs(scope).PlanDeployAsync(Request(archetype: "no-such-thing")));
            unknown.IsRetired.ShouldBeFalse();
            unknown.Message.ShouldContain("not in the catalog");
        }

        await SeedCatalogAsync(ArchetypeStatus.Retired);
        using (var scope = _host.Services.CreateScope())
        {
            var retired = await Should.ThrowAsync<ArchetypeNotDeployableException>(
                () => Verbs(scope).PlanDeployAsync(Request()));
            retired.IsRetired.ShouldBeTrue();
            retired.Message.ShouldContain("retired");
        }

        await _dispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default);
    }

    [Fact]
    public async Task Missing_or_inactive_spoke_is_refused_before_intent()
    {
        await SeedCatalogAsync();

        using (var scope = _host.Services.CreateScope())
        {
            await Should.ThrowAsync<EnvironmentNotFoundException>(
                () => Verbs(scope).PlanDeployAsync(Request()));
        }

        await SeedSpokeAsync(EnvironmentStatus.Failed);
        using (var scope = _host.Services.CreateScope())
        {
            var ex = await Should.ThrowAsync<SpokeNotActiveException>(
                () => Verbs(scope).PlanDeployAsync(Request()));
            ex.Status.ShouldBe(EnvironmentStatus.Failed);
        }

        await using var registry = fixture.CreateRegistryContext();
        (await registry.Environments.CountAsync(e => e.Kind == EnvironmentKind.Workload)).ShouldBe(0);
        await _dispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default);
    }

    // --- Version resolution + stamping (FR-005) ----------------------------------------------------

    [Fact]
    public async Task Deploy_resolves_the_newest_version_by_semver_and_stamps_it()
    {
        // v1.10.0 > v1.9.0 by SemVer precedence (text ordering would invert them).
        await SeedCatalogAsync(ArchetypeStatus.Active, "v1.9.0", "v1.10.0");
        await SeedSpokeAsync();

        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var result = await Verbs(scope).DeployAsync(Request(), Confirmation.ForApply());
            envId = result.EnvId;
            result.Status.ShouldBe(EnvironmentStatus.Provisioning);
            result.Outcome.ShouldBe(RunOutcome.Dispatched);
        }

        await using var registry = fixture.CreateRegistryContext();
        var environment = await registry.Environments.SingleAsync(e => e.EnvId == envId);
        environment.Kind.ShouldBe(EnvironmentKind.Workload);
        environment.Region.ShouldBe("westus3"); // copied from the spoke
        environment.SpokeCidr.ShouldBeNull();   // workloads carve no address space

        var details = await registry.Workloads.SingleAsync(w => w.EnvId == envId);
        details.ArchetypeVersion.ShouldBe("v1.10.0");
        details.SpokeName.ShouldBe(SpokeName);
        details.PdpEnv.ShouldBe("dev");

        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
        run.WorkflowFile.ShouldBe("workload-deploy.yml");
        var inputs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(run.DispatchInputs)!;
        inputs["archetype_ref"].ShouldBe("archetype/container-app-sql/v1.10.0");
        inputs["archetype_path"].ShouldBe("archetypes/container-app-sql");
        inputs["mode"].ShouldBe("apply");
        inputs["spoke_name"].ShouldBe(SpokeName);
        inputs["pdp_env"].ShouldBe("dev");
        inputs["parameters_json"].ShouldContain("nginx:latest");
    }

    // --- Repeat-deploy semantics -------------------------------------------------------------------

    [Fact]
    public async Task Repeat_deploy_converges_on_identical_parameters_and_refuses_changed_ones()
    {
        await SeedCatalogAsync();
        await SeedSpokeAsync();

        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var result = await Verbs(scope).DeployAsync(
                Request(parameters: new JsonObject { ["containerImage"] = "nginx:latest", ["cpu"] = 0.5 }),
                Confirmation.ForApply());
            envId = result.EnvId;

            // Drive the apply run to success so the workload lands Active and its saga retires —
            // exactly what the tracked run would do live.
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new RunCompleted(envId, RunPhase.Apply));
        }

        // Identical configuration (cosmetically reordered keys) converges on the SAME env_id (FR-022).
        using (var scope = _host.Services.CreateScope())
        {
            var repeat = await Verbs(scope).DeployAsync(
                Request(parameters: new JsonObject { ["cpu"] = 0.5, ["containerImage"] = "nginx:latest" }),
                Confirmation.ForApply());
            repeat.EnvId.ShouldBe(envId);

            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new RunCompleted(envId, RunPhase.Apply));
        }

        // Differing parameters are refused — in-place reconfiguration is destroy → deploy.
        using (var scope = _host.Services.CreateScope())
        {
            await Should.ThrowAsync<WorkloadParametersChangedException>(
                () => Verbs(scope).DeployAsync(
                    Request(parameters: new JsonObject { ["containerImage"] = "nginx:latest", ["cpu"] = 1.0 }),
                    Confirmation.ForApply()));
        }

        await using var registry = fixture.CreateRegistryContext();
        (await registry.Environments.CountAsync(e => e.Kind == EnvironmentKind.Workload)).ShouldBe(1);
    }

    // --- Article VIII plan → confirm ----------------------------------------------------------------

    [Fact]
    public async Task Plan_then_confirm_dispatches_the_apply_with_the_stamped_inputs()
    {
        await SeedCatalogAsync();
        await SeedSpokeAsync();
        var request = Request();

        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var plan = await Verbs(scope).PlanDeployAsync(request);
            envId = plan.EnvId;
            plan.Phase.ShouldBe(RunPhase.Plan);
            plan.ProposedInputs["mode"].ShouldBe("apply");
            plan.ProposedInputs["archetype_ref"].ShouldBe("archetype/container-app-sql/v1.0.0");
        }

        await WaitForDispatchAsync(envId, RunPhase.Plan);

        // Confirming before the plan run finished is the distinct "plan not ready" rejection (FR-019).
        using (var scope = _host.Services.CreateScope())
        {
            await Should.ThrowAsync<PlanNotReadyException>(
                () => Verbs(scope).DeployAsync(request, Confirmation.ForApply()));
        }

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d => d.EnvId == envId && d.Mode == RunPhase.Apply),
            Arg.Any<CancellationToken>());

        // The plan run reaches Succeeded (as the webhook/reconciler would record it)…
        await using (var registry = fixture.CreateRegistryContext())
        {
            var planRun = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
            planRun.Outcome = RunOutcome.Succeeded;
            await registry.SaveChangesAsync();
        }

        using (var scope = _host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new RunCompleted(envId, RunPhase.Plan));
        }

        // …and the confirming Deploy releases the gated apply with exactly the planned inputs.
        using (var scope = _host.Services.CreateScope())
        {
            var result = await Verbs(scope).DeployAsync(request, Confirmation.ForApply());
            result.EnvId.ShouldBe(envId);
            result.Status.ShouldBe(EnvironmentStatus.Provisioning);
        }

        await WaitForDispatchAsync(envId, RunPhase.Apply);
        await _dispatcher.Received().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d =>
                d.EnvId == envId &&
                d.Mode == RunPhase.Apply &&
                d.WorkflowFile == "workload-deploy.yml" &&
                d.Inputs["archetype_ref"] == "archetype/container-app-sql/v1.0.0"),
            Arg.Any<CancellationToken>());
    }

    private async Task WaitForDispatchAsync(Guid envId, RunPhase mode)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var seen = _dispatcher.ReceivedCalls()
                .Select(c => c.GetArguments()[0])
                .OfType<WorkflowDispatch>()
                .Any(d => d.EnvId == envId && d.Mode == mode);
            if (seen)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No {mode} dispatch for env {envId} in time.");
    }
}
