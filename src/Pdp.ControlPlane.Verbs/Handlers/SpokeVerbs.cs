using FluentValidation;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
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
/// The spoke verb implementation (spec 004 wrapped; Gate-G1 closed) — the single front-end-agnostic
/// surface the <c>pdp</c> CLI and the future MCP wrap. Every mutating verb follows the spine
/// <b>validate → allocate → record intent → (plan → confirm) → apply → track</b> (Articles II/VI/VIII)
/// without running OpenTofu in-process: the lifecycle saga records intent + the dispatch run and cascades
/// the <c>workflow_dispatch</c> in one durable transaction (FR-011).
///
/// <para>US2 adds the two-phase Article VIII gate: <see cref="PlanCreateAsync"/> dispatches a
/// <c>mode=plan</c> run, and a confirming <see cref="CreateAsync"/> on the planned environment releases
/// the gated apply via <see cref="ConfirmationGiven"/>. <see cref="DestroyAsync"/> requires an explicit,
/// unbypassable confirmation and releases the IPAM allocation on a successful destroy (FR-009),
/// completing spec 004's deferred teardown.</para>
/// </summary>
public sealed class SpokeVerbs(
    IMessageBus bus,
    IEnvironmentRegistry registry,
    IIpamLedger ledger,
    IValidator<SpokeCreateRequest> validator,
    GitHubAppOptions gitHubOptions) : ISpokeVerbs
{
    private const string VendWorkflow = "spoke-vend.yml";
    private const string DestroyWorkflow = "spoke-destroy.yml";

    private static bool IsSucceededPlan(ProvisioningRun? run) =>
        run is { Phase: RunPhase.Plan, Outcome: RunOutcome.Succeeded };

    /// <inheritdoc />
    public async Task<PlanResult> PlanCreateAsync(
        SpokeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var owner = ResolveOwner(request.Owner);

        // Fail-fast region check + claim + allocate (Gate-G1) — identical preconditions to a direct
        // create; the only difference is the first dispatched run is mode=plan, not mode=apply.
        // QueryAsync and BeginCreateAsync have no data dependency on each other; run them concurrently.
        // If QueryAsync fails (e.g. region not registered) after BeginCreateAsync has already claimed
        // the row, abort the claim so no orphan remains (FR-023).
        var regionViewTask = ledger.QueryAsync(request.Region, cancellationToken);
        var envIdTask = registry
            .BeginCreateAsync(EnvironmentKind.Spoke, request.Subscription, request.Region, request.Name, owner, cancellationToken);
        try
        {
            await Task.WhenAll(regionViewTask, envIdTask).ConfigureAwait(false);
        }
        catch
        {
            if (envIdTask.IsCompletedSuccessfully)
            {
                await registry.AbortCreateAsync(envIdTask.Result, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        var regionView = regionViewTask.Result;
        var envId = envIdTask.Result;

        using var activity = ControlPlaneTelemetry.StartVerb("spoke.plan-create", envId);

        var allocation = await AllocateOrAbortAsync(request, envId, cancellationToken).ConfigureAwait(false);

        var runId = Guid.CreateVersion7();
        var planInputs = BuildVendInputs(envId, request, regionView.RegionIndex, allocation, RunPhase.Plan);
        var applyInputs = BuildVendInputs(envId, request, regionView.RegionIndex, allocation, RunPhase.Apply);

        var begin = new BeginSpokeProvisioning(
            envId, request.Subscription, request.Region, request.Name, owner,
            allocation.Network, runId, VendWorkflow, gitHubOptions.DefaultBranch, planInputs, RunPhase.Plan);

        await StartOrReleaseAsync(begin, request.Region, request.Name, envId, cancellationToken).ConfigureAwait(false);

        // The plan run has been dispatched; its captured PlanSummary/run URL are surfaced once the run is
        // tracked terminal (the caller polls — contracts/cli-surface.md §4). ProposedInputs are the exact
        // inputs a confirmed apply will dispatch.
        return new PlanResult(envId, RunPhase.Plan, PlanSummary: null, applyInputs, RunUrl: null);
    }

    /// <inheritdoc />
    public async Task<VerbResult> CreateAsync(
        SpokeCreateRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var owner = ResolveOwner(request.Owner);

        // If a plan was already surfaced for this spoke (the environment is Provisioning with a completed
        // plan run), this Create is the Article VIII confirmation → release the gated apply.
        var existing = await registry
            .FindByNaturalKeyAsync(EnvironmentKind.Spoke, request.Subscription, request.Name, cancellationToken)
            .ConfigureAwait(false);
        if (existing is { Status: EnvironmentStatus.Provisioning })
        {
            var latest = await registry.GetLatestRunAsync(existing.EnvId, cancellationToken).ConfigureAwait(false);
            if (IsSucceededPlan(latest))
            {
                return await ConfirmCreateAsync(existing, confirmation, cancellationToken).ConfigureAwait(false);
            }

            // The plan hasn't succeeded yet: distinguish "plan still running" / "plan failed" from a genuine
            // concurrent mutation (FR-019) — the apply is a pure read+dispatch and never blocks/reconciles.
            throw PlanGate.RejectionFor(existing.EnvId, existing.Status, latest);
        }

        // Direct one-shot vend (no prior plan): validate → allocate → dispatch mode=apply → track.
        // QueryAsync and BeginCreateAsync have no data dependency on each other; run them concurrently.
        // If QueryAsync fails (e.g. region not registered) after BeginCreateAsync has already claimed
        // the row, abort the claim so no orphan remains (FR-023).
        var regionViewTask = ledger.QueryAsync(request.Region, cancellationToken);
        var envIdTask = registry
            .BeginCreateAsync(EnvironmentKind.Spoke, request.Subscription, request.Region, request.Name, owner, cancellationToken);
        try
        {
            await Task.WhenAll(regionViewTask, envIdTask).ConfigureAwait(false);
        }
        catch
        {
            if (envIdTask.IsCompletedSuccessfully)
            {
                await registry.AbortCreateAsync(envIdTask.Result, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        var regionView = regionViewTask.Result;
        var envId = envIdTask.Result;

        using var activity = ControlPlaneTelemetry.StartVerb("spoke.create", envId);

        var allocation = await AllocateOrAbortAsync(request, envId, cancellationToken).ConfigureAwait(false);

        var runId = Guid.CreateVersion7();
        var applyInputs = BuildVendInputs(envId, request, regionView.RegionIndex, allocation, RunPhase.Apply);

        var begin = new BeginSpokeProvisioning(
            envId, request.Subscription, request.Region, request.Name, owner,
            allocation.Network, runId, VendWorkflow, gitHubOptions.DefaultBranch, applyInputs, RunPhase.Apply);

        await StartOrReleaseAsync(begin, request.Region, request.Name, envId, cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            envId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[]
            {
                $"Allocated {allocation.Network} for spoke '{request.Name}' in {request.Region}.",
                $"Dispatched {VendWorkflow} (mode=apply); tracking by env_id {envId}.",
            });
    }

    /// <inheritdoc />
    public async Task<PlanResult> PlanDestroyAsync(EnvRef environment, CancellationToken cancellationToken = default)
    {
        var env = await ResolveActiveAsync(environment, cancellationToken).ConfigureAwait(false);
        var regionView = await ledger.QueryAsync(env.Region, cancellationToken).ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("spoke.plan-destroy", env.EnvId);

        var runId = Guid.CreateVersion7();
        var planInputs = BuildDestroyInputs(env, regionView.RegionIndex, RunPhase.Plan);
        var destroyInputs = BuildDestroyInputs(env, regionView.RegionIndex, RunPhase.Destroy);

        var begin = new BeginSpokeDestroy(
            env.EnvId, env.Subscription, env.Region, env.Name, env.Owner,
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
        // restate the spoke name. Rejected BEFORE any dispatch.
        ConfirmationGuard.RequireMatch(confirmation, env.Name);

        using var activity = ControlPlaneTelemetry.StartVerb("spoke.destroy", env.EnvId);

        var regionView = await ledger.QueryAsync(env.Region, cancellationToken).ConfigureAwait(false);

        // If a destroy plan was already surfaced (env Destroying with a completed plan run), confirm it;
        // otherwise this is the one-shot confirmed destroy.
        if (env.Status == EnvironmentStatus.Destroying)
        {
            var latest = await registry.GetLatestRunAsync(env.EnvId, cancellationToken).ConfigureAwait(false);
            if (IsSucceededPlan(latest))
            {
                return await ConfirmDestroyAsync(env, regionView.RegionIndex, cancellationToken).ConfigureAwait(false);
            }

            // Destroy plan not yet succeeded → "plan not ready"/"plan failed" vs single-flight (FR-019).
            throw PlanGate.RejectionFor(env.EnvId, env.Status, latest);
        }

        if (env.Status is EnvironmentStatus.Requested or EnvironmentStatus.Provisioning)
        {
            // A create is in flight — refuse to race a destroy against it (single-flight, FR-022a).
            throw new OperationInProgressException(env.EnvId, env.Status);
        }

        var runId = Guid.CreateVersion7();
        var destroyInputs = BuildDestroyInputs(env, regionView.RegionIndex, RunPhase.Destroy);
        var begin = new BeginSpokeDestroy(
            env.EnvId, env.Subscription, env.Region, env.Name, env.Owner,
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
                $"Confirmed; dispatched {DestroyWorkflow} (mode=destroy) for spoke '{env.Name}'.",
                $"The allocation will be released on success; tracking by env_id {env.EnvId}.",
            });
    }

    /// <summary>Releases the gated apply for an environment whose plan was surfaced (the confirm path).</summary>
    private async Task<VerbResult> ConfirmCreateAsync(
        Environment env,
        Confirmation confirmation,
        CancellationToken cancellationToken)
    {
        // Create confirmation may be implicit (a --yes after the plan is shown); require only that the
        // owner did confirm.
        if (!confirmation.IsConfirmed)
        {
            throw new ConfirmationRequiredException(env.Name);
        }

        using var activity = ControlPlaneTelemetry.StartVerb("spoke.confirm-create", env.EnvId);

        var regionView = await ledger.QueryAsync(env.Region, cancellationToken).ConfigureAwait(false);
        var runId = Guid.CreateVersion7();
        var applyInputs = BuildVendInputs(env, regionView.RegionIndex, RunPhase.Apply);

        await bus.InvokeAsync(
            new ConfirmationGiven(env.EnvId, runId, VendWorkflow, gitHubOptions.DefaultBranch, RunPhase.Apply, applyInputs),
            cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            env.EnvId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[] { $"Confirmed plan; dispatched {VendWorkflow} (mode=apply) for spoke '{env.Name}'." });
    }

    /// <summary>Releases the gated destroy for an environment whose destroy plan was surfaced.</summary>
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
            new[] { $"Confirmed destroy plan; dispatched {DestroyWorkflow} (mode=destroy) for spoke '{env.Name}'." });
    }

    /// <summary>Allocates the block (Gate-G1), aborting the registry claim on failure (FR-023/FR-011).</summary>
    private async Task<Allocation> AllocateOrAbortAsync(
        SpokeCreateRequest request,
        Guid envId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ledger
                .AllocateAsync(request.Region, request.Name, request.Size, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await registry.AbortCreateAsync(envId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs the saga Start in one durable transaction; on a rolled-back start, releases the provisional
    /// block and drops the claim so a failed create leaks nothing (FR-011).
    /// </summary>
    private async Task StartOrReleaseAsync(
        BeginSpokeProvisioning begin,
        string region,
        string name,
        Guid envId,
        CancellationToken cancellationToken)
    {
        try
        {
            await bus.InvokeAsync(begin, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ledger.ReleaseAsync(region, name, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Builds the <c>spoke-vend.yml</c> inputs from a create request (env_id + mode + spec-004 inputs).</summary>
    private static Dictionary<string, string> BuildVendInputs(
        Guid envId,
        SpokeCreateRequest request,
        short regionIndex,
        Allocation allocation,
        RunPhase mode) => new()
        {
            ["env_id"] = envId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = request.Region,
            ["region_index"] = regionIndex.ToString(),
            ["target_subscription_id"] = request.Subscription,
            ["spoke_name"] = request.Name,
            ["spoke_cidr"] = allocation.Network.ToString(),
        };

    /// <summary>Builds the <c>spoke-vend.yml</c> apply inputs from a recorded environment (confirm path).</summary>
    private static Dictionary<string, string> BuildVendInputs(
        Environment env,
        short regionIndex,
        RunPhase mode) => new()
        {
            ["env_id"] = env.EnvId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = env.Region,
            ["region_index"] = regionIndex.ToString(),
            ["target_subscription_id"] = env.Subscription,
            ["spoke_name"] = env.Name,
            ["spoke_cidr"] = env.SpokeCidr?.ToString() ?? string.Empty,
        };

    /// <summary>Builds the <c>spoke-destroy.yml</c> inputs (env_id + mode + the typed destroy-confirm).</summary>
    private static Dictionary<string, string> BuildDestroyInputs(
        Environment env,
        short regionIndex,
        RunPhase mode) => new()
        {
            ["env_id"] = env.EnvId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = env.Region,
            ["region_index"] = regionIndex.ToString(),
            ["target_subscription_id"] = env.Subscription,
            ["spoke_name"] = env.Name,
            // The workflow's typed-confirmation gate restates the spoke name; the control plane has
            // already enforced the owner's confirmation (ConfirmationGuard), so it supplies the match.
            ["destroy-confirm"] = env.Name,
        };
}
