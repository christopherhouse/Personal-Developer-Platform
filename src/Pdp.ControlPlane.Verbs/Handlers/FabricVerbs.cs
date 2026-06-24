using FluentValidation;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Registry.Lifecycle;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.ControlPlane.Verbs.PlanConfirm;
using Pdp.ControlPlane.Verbs.Telemetry;
using Wolverine;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The fabric verb implementation (spec 003 wrapped) — the single surface the <c>pdp</c> CLI and the
/// future MCP wrap. Mirrors <see cref="SpokeVerbs"/>: every mutating verb follows the Article II/VIII
/// spine <b>validate → register region → record intent → (plan → confirm) → apply → track</b> without
/// running OpenTofu in-process. A fabric owns no per-spoke block, so there is no Gate-G1 allocation;
/// instead <see cref="CreateAsync"/> registers the region in the ledger (idempotent) and dispatches the
/// new <c>fabric-vend.yml</c> (FR-012a). A fabric is identified by its region — the registry natural key
/// is <c>(Fabric, PlatformSubscriptionId, Region)</c>, with the region carried as the environment name.
/// </summary>
public sealed class FabricVerbs(
    IMessageBus bus,
    IEnvironmentRegistry registry,
    IIpamLedger ledger,
    IValidator<FabricCreateRequest> validator,
    GitHubAppOptions gitHubOptions,
    ControlPlaneOptions controlPlaneOptions) : IFabricVerbs
{
    private const string VendWorkflow = "fabric-vend.yml";
    private const string DestroyWorkflow = "fabric-destroy.yml";

    private string PlatformSubscription => controlPlaneOptions.PlatformSubscriptionId;

    private static bool IsSucceededPlan(ProvisioningRun? run) =>
        run is { Phase: RunPhase.Plan, Outcome: RunOutcome.Succeeded };

    /// <inheritdoc />
    public async Task<PlanResult> PlanCreateAsync(
        FabricCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var owner = ResolveOwner(request.Owner);

        // Register the region (idempotent: a repeat with the same index returns the existing pool) so a
        // plan is only ever surfaced for a region whose address space exists — fail-fast otherwise.
        await ledger.RegisterRegionAsync(request.Region, request.RegionIndex, cancellationToken).ConfigureAwait(false);
        var envId = await registry
            .BeginCreateAsync(EnvironmentKind.Fabric, PlatformSubscription, request.Region, request.Region, owner, cancellationToken)
            .ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("fabric.plan-create", envId);

        var runId = Guid.CreateVersion7();
        var planInputs = BuildVendInputs(envId, request, RunPhase.Plan);
        var applyInputs = BuildVendInputs(envId, request, RunPhase.Apply);

        var begin = new BeginFabricProvisioning(
            envId, PlatformSubscription, request.Region, owner,
            runId, VendWorkflow, gitHubOptions.DefaultBranch, planInputs, RunPhase.Plan);

        await StartOrAbortAsync(begin, envId, cancellationToken).ConfigureAwait(false);

        return new PlanResult(envId, RunPhase.Plan, PlanSummary: null, applyInputs, RunUrl: null);
    }

    /// <inheritdoc />
    public async Task<VerbResult> CreateAsync(
        FabricCreateRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var owner = ResolveOwner(request.Owner);

        // A pending plan for this fabric (Provisioning with a completed plan run) → this Create is the
        // Article VIII confirmation that releases the gated apply.
        var existing = await registry
            .FindByNaturalKeyAsync(EnvironmentKind.Fabric, PlatformSubscription, request.Region, cancellationToken)
            .ConfigureAwait(false);
        if (existing is { Status: EnvironmentStatus.Provisioning })
        {
            var latest = (await registry.GetRunsAsync(existing.EnvId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();
            if (IsSucceededPlan(latest))
            {
                return await ConfirmCreateAsync(existing, request.RegionIndex, confirmation, cancellationToken).ConfigureAwait(false);
            }

            // Plan not yet succeeded → "plan not ready"/"plan failed" vs genuine single-flight (FR-019).
            throw PlanGate.RejectionFor(existing.EnvId, existing.Status, latest);
        }

        // Direct one-shot vend (no prior plan): validate → register region → dispatch mode=apply → track.
        await ledger.RegisterRegionAsync(request.Region, request.RegionIndex, cancellationToken).ConfigureAwait(false);
        var envId = await registry
            .BeginCreateAsync(EnvironmentKind.Fabric, PlatformSubscription, request.Region, request.Region, owner, cancellationToken)
            .ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("fabric.create", envId);

        var runId = Guid.CreateVersion7();
        var applyInputs = BuildVendInputs(envId, request, RunPhase.Apply);

        var begin = new BeginFabricProvisioning(
            envId, PlatformSubscription, request.Region, owner,
            runId, VendWorkflow, gitHubOptions.DefaultBranch, applyInputs, RunPhase.Apply);

        await StartOrAbortAsync(begin, envId, cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            envId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[]
            {
                $"Registered region '{request.Region}' (index {request.RegionIndex}) in the IPAM ledger.",
                $"Dispatched {VendWorkflow} (mode=apply); tracking by env_id {envId}.",
            });
    }

    /// <inheritdoc />
    public async Task<PlanResult> PlanDestroyAsync(EnvRef environment, CancellationToken cancellationToken = default)
    {
        var env = await ResolveActiveAsync(environment, cancellationToken).ConfigureAwait(false);
        var regionView = await ledger.QueryAsync(env.Region, cancellationToken).ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("fabric.plan-destroy", env.EnvId);

        var runId = Guid.CreateVersion7();
        var planInputs = BuildDestroyInputs(env, regionView.RegionIndex, RunPhase.Plan);
        var destroyInputs = BuildDestroyInputs(env, regionView.RegionIndex, RunPhase.Destroy);

        var begin = new BeginFabricDestroy(
            env.EnvId, env.Subscription, env.Region, env.Owner,
            runId, DestroyWorkflow, gitHubOptions.DefaultBranch, planInputs, RunPhase.Plan);
        await bus.InvokeAsync(begin, cancellationToken).ConfigureAwait(false);

        return new PlanResult(env.EnvId, RunPhase.Plan, PlanSummary: null, destroyInputs, RunUrl: null);
    }

    /// <inheritdoc />
    public async Task<VerbResult> DestroyAsync(
        EnvRef environment,
        Confirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        var env = await ResolveAsync(environment, cancellationToken).ConfigureAwait(false);

        // Article VIII / FR-007: destroy confirmation is mandatory and unbypassable — the owner must
        // restate the region. Rejected BEFORE any dispatch.
        ConfirmationGuard.RequireMatch(confirmation, env.Name);

        using var activity = ControlPlaneTelemetry.StartVerb("fabric.destroy", env.EnvId);

        var regionView = await ledger.QueryAsync(env.Region, cancellationToken).ConfigureAwait(false);

        if (env.Status == EnvironmentStatus.Destroying)
        {
            var latest = (await registry.GetRunsAsync(env.EnvId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();
            if (IsSucceededPlan(latest))
            {
                return await ConfirmDestroyAsync(env, regionView.RegionIndex, cancellationToken).ConfigureAwait(false);
            }

            // Destroy plan not yet succeeded → "plan not ready"/"plan failed" vs single-flight (FR-019).
            throw PlanGate.RejectionFor(env.EnvId, env.Status, latest);
        }

        if (env.Status is EnvironmentStatus.Requested or EnvironmentStatus.Provisioning)
        {
            throw new OperationInProgressException(env.EnvId, env.Status);
        }

        var runId = Guid.CreateVersion7();
        var destroyInputs = BuildDestroyInputs(env, regionView.RegionIndex, RunPhase.Destroy);
        var begin = new BeginFabricDestroy(
            env.EnvId, env.Subscription, env.Region, env.Owner,
            runId, DestroyWorkflow, gitHubOptions.DefaultBranch, destroyInputs, RunPhase.Destroy);
        await bus.InvokeAsync(begin, cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            env.EnvId,
            EnvironmentStatus.Destroying,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[]
            {
                $"Confirmed; dispatched {DestroyWorkflow} (mode=destroy) for fabric '{env.Region}'.",
                $"The hub carve-out survives; tracking by env_id {env.EnvId}.",
            });
    }

    /// <summary>Releases the gated apply for a fabric whose plan was surfaced (the confirm path).</summary>
    private async Task<VerbResult> ConfirmCreateAsync(
        Environment env,
        int regionIndex,
        Confirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (!confirmation.IsConfirmed)
        {
            throw new ConfirmationRequiredException(env.Name);
        }

        using var activity = ControlPlaneTelemetry.StartVerb("fabric.confirm-create", env.EnvId);

        var runId = Guid.CreateVersion7();
        var applyInputs = BuildVendInputs(env, regionIndex, RunPhase.Apply);

        await bus.InvokeAsync(
            new ConfirmationGiven(env.EnvId, runId, VendWorkflow, gitHubOptions.DefaultBranch, RunPhase.Apply, applyInputs),
            cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            env.EnvId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[] { $"Confirmed plan; dispatched {VendWorkflow} (mode=apply) for fabric '{env.Region}'." });
    }

    /// <summary>Releases the gated destroy for a fabric whose destroy plan was surfaced.</summary>
    private async Task<VerbResult> ConfirmDestroyAsync(
        Environment env,
        short regionIndex,
        CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7();
        var destroyInputs = BuildDestroyInputs(env, regionIndex, RunPhase.Destroy);

        await bus.InvokeAsync(
            new ConfirmationGiven(env.EnvId, runId, DestroyWorkflow, gitHubOptions.DefaultBranch, RunPhase.Destroy, destroyInputs),
            cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            env.EnvId,
            EnvironmentStatus.Destroying,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[] { $"Confirmed destroy plan; dispatched {DestroyWorkflow} (mode=destroy) for fabric '{env.Region}'." });
    }

    /// <summary>
    /// Runs the saga Start in one durable transaction; on a rolled-back start, drops the registry claim
    /// so a failed create leaves no orphan row (FR-023). A fabric holds no allocation to release.
    /// </summary>
    private async Task StartOrAbortAsync(
        BeginFabricProvisioning begin,
        Guid envId,
        CancellationToken cancellationToken)
    {
        try
        {
            await bus.InvokeAsync(begin, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await registry.AbortCreateAsync(envId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Environment> ResolveAsync(EnvRef reference, CancellationToken cancellationToken)
    {
        var env = reference.EnvId is { } id
            ? await registry.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : reference.HasNaturalKey
                ? await registry
                    .FindByNaturalKeyAsync(reference.Kind!.Value, reference.Subscription!, reference.Name!, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        return env ?? throw new EnvironmentNotFoundException(reference);
    }

    private async Task<Environment> ResolveActiveAsync(EnvRef reference, CancellationToken cancellationToken)
    {
        var env = await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        if (env.Status is EnvironmentStatus.Requested or EnvironmentStatus.Provisioning or EnvironmentStatus.Destroying)
        {
            throw new OperationInProgressException(env.EnvId, env.Status);
        }

        return env;
    }

    private static string ResolveOwner(string? owner) =>
        string.IsNullOrWhiteSpace(owner) ? System.Environment.UserName : owner;

    /// <summary>Builds the <c>fabric-vend.yml</c> inputs from a create request (env_id + mode + region).</summary>
    private static Dictionary<string, string> BuildVendInputs(
        Guid envId,
        FabricCreateRequest request,
        RunPhase mode) => new()
        {
            ["env_id"] = envId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = request.Region,
            ["region_index"] = request.RegionIndex.ToString(),
        };

    /// <summary>Builds the <c>fabric-vend.yml</c> apply inputs from a recorded fabric (confirm path).</summary>
    private static Dictionary<string, string> BuildVendInputs(
        Environment env,
        int regionIndex,
        RunPhase mode) => new()
        {
            ["env_id"] = env.EnvId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = env.Region,
            ["region_index"] = regionIndex.ToString(),
        };

    /// <summary>Builds the <c>fabric-destroy.yml</c> inputs (env_id + mode + the typed destroy-confirm).</summary>
    private static Dictionary<string, string> BuildDestroyInputs(
        Environment env,
        short regionIndex,
        RunPhase mode) => new()
        {
            ["env_id"] = env.EnvId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = env.Region,
            ["region_index"] = regionIndex.ToString(),
            // The workflow's typed-confirmation gate restates the region; the control plane has already
            // enforced the owner's confirmation (ConfirmationGuard), so it supplies the match.
            ["destroy-confirm"] = env.Region,
        };
}
