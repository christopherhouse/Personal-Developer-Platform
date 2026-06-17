using FluentValidation;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Registry.Lifecycle;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.ControlPlane.Verbs.Telemetry;
using Wolverine;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The spoke verb implementation (US1 <see cref="CreateAsync"/>). Orchestrates the vend spine without
/// running OpenTofu in-process (Article II): it validates, allocates the block live from the ledger
/// (Gate-G1), then starts the lifecycle saga, which records intent + the dispatch run and cascades the
/// apply <c>workflow_dispatch</c> in one durable transaction (FR-011). The block is allocated <b>before</b>
/// the start transaction and released as compensation on any failure, so a failed create leaks nothing.
/// </summary>
public sealed class SpokeVerbs(
    IMessageBus bus,
    IEnvironmentRegistry registry,
    IIpamLedger ledger,
    IValidator<SpokeCreateRequest> validator,
    GitHubAppOptions gitHubOptions) : ISpokeVerbs
{
    private const string WorkflowFile = "spoke-vend.yml";

    /// <inheritdoc />
    public async Task<VerbResult> CreateAsync(
        SpokeCreateRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var owner = string.IsNullOrWhiteSpace(request.Owner) ? System.Environment.UserName : request.Owner;

        // Validate the region is registered up front (fail-fast, no write) — throws RegionNotRegistered.
        var regionView = await ledger.QueryAsync(request.Region, cancellationToken).ConfigureAwait(false);

        // Claim the environment (single-flight + convergent natural-key — FR-022/FR-022a). This writes a
        // Requested row; if allocation then fails we abort the claim so no orphan remains (FR-023).
        var envId = await registry
            .BeginCreateAsync(EnvironmentKind.Spoke, request.Subscription, request.Region, request.Name, owner, cancellationToken)
            .ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("spoke.create", envId);

        Allocation allocation;
        try
        {
            // Gate-G1: address space comes ONLY from the ledger, allocated by size (Article VI / FR-008).
            allocation = await ledger
                .AllocateAsync(request.Region, request.Name, request.Size, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await registry.AbortCreateAsync(envId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var runId = Guid.CreateVersion7();
        var inputs = BuildApplyInputs(envId, request, regionView.RegionIndex, allocation);

        var begin = new BeginSpokeProvisioning(
            envId,
            request.Subscription,
            request.Region,
            request.Name,
            owner,
            allocation.Network,
            runId,
            WorkflowFile,
            gitHubOptions.DefaultBranch,
            inputs);

        try
        {
            // Runs the saga Start in one durable transaction: intent + run row committed, apply dispatch
            // enqueued through the outbox (sent iff the transaction commits — FR-011).
            await bus.InvokeAsync(begin, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The start transaction rolled back — release the provisional block and drop the claim.
            await ledger.ReleaseAsync(request.Region, request.Name, cancellationToken).ConfigureAwait(false);
            await registry.AbortCreateAsync(envId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new VerbResult(
            envId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[]
            {
                $"Allocated {allocation.Network} for spoke '{request.Name}' in {request.Region}.",
                $"Dispatched {WorkflowFile} (mode=apply); tracking by env_id {envId}.",
            });
    }

    /// <summary>Builds the exact <c>spoke-vend.yml</c> apply inputs (env_id + mode + the spec-004 inputs).</summary>
    private static Dictionary<string, string> BuildApplyInputs(
        Guid envId,
        SpokeCreateRequest request,
        int regionIndex,
        Allocation allocation) => new()
        {
            ["env_id"] = envId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(RunPhase.Apply),
            ["region"] = request.Region,
            ["region_index"] = regionIndex.ToString(),
            ["target_subscription_id"] = request.Subscription,
            ["spoke_name"] = request.Name,
            ["spoke_cidr"] = allocation.Network.ToString(),
        };

    /// <inheritdoc />
    public Task<PlanResult> PlanCreateAsync(SpokeCreateRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Two-phase plan/confirm lands with US2 (spec 006 phase 4, T043).");

    /// <inheritdoc />
    public Task<PlanResult> PlanDestroyAsync(EnvRef environment, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Two-phase plan/confirm lands with US2 (spec 006 phase 4, T043).");

    /// <inheritdoc />
    public Task<VerbResult> DestroyAsync(EnvRef environment, Confirmation confirmation, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Spoke destroy + allocation release lands with US2 (spec 006 phase 4, T044).");
}
