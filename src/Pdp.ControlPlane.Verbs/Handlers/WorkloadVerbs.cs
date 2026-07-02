using System.Text.Json.Nodes;
using FluentValidation;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Catalog;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Registry.Lifecycle;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.ControlPlane.Verbs.Telemetry;
using Wolverine;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The workload verb implementation (spec 008) — the spec-006 spine reused end to end: the same
/// registry claim, the same saga (via <see cref="BeginWorkloadDeploy"/> — <b>no IPAM step</b>,
/// workloads carve no address space), the same dispatch + <c>env_id</c> tracking. What is new is the
/// catalog: the archetype must be <b>active</b>, the deploy resolves and permanently <b>stamps</b>
/// the newest active version (FR-005), and the caller's parameters must satisfy that version's JSON
/// schema (FR-003) — all <i>before</i> any intent row exists (validation order R3:
/// shape → catalog → schema → spoke → intent).
///
/// <para>US2 (T036) completes <see cref="PlanDestroyAsync"/>/<see cref="DestroyAsync"/> — until then
/// they refuse loudly rather than half-implement the Article VIII destroy gate.</para>
/// </summary>
public sealed class WorkloadVerbs(
    IMessageBus bus,
    IEnvironmentRegistry registry,
    ICatalogStore catalog,
    IValidator<WorkloadDeployRequest> validator,
    GitHubAppOptions gitHubOptions) : IWorkloadVerbs
{
    private const string DeployWorkflow = "workload-deploy.yml";

    private static bool IsSucceededPlan(ProvisioningRun? run) =>
        run is { Phase: RunPhase.Plan, Outcome: RunOutcome.Succeeded };

    /// <inheritdoc />
    public async Task<PlanResult> PlanDeployAsync(
        WorkloadDeployRequest request,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareDeployAsync(request, cancellationToken).ConfigureAwait(false);
        var envId = await ClaimAndStampAsync(request, prepared, cancellationToken).ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("workload.plan-deploy", envId);

        var runId = Guid.CreateVersion7();
        var planInputs = BuildDeployInputs(envId, request, prepared, RunPhase.Plan);
        var applyInputs = BuildDeployInputs(envId, request, prepared, RunPhase.Apply);

        var begin = new BeginWorkloadDeploy(envId, new WorkloadDispatchInputs(
            request.Subscription, prepared.Region, request.WorkloadName, prepared.Owner,
            runId, DeployWorkflow, gitHubOptions.DefaultBranch, planInputs, RunPhase.Plan));

        await StartOrAbortAsync(begin, envId, cancellationToken).ConfigureAwait(false);

        // The plan run is dispatched; its PlanSummary/run URL surface once the run is tracked terminal
        // (the caller polls). ProposedInputs are the exact inputs a confirmed apply will dispatch.
        return new PlanResult(envId, RunPhase.Plan, PlanSummary: null, applyInputs, RunUrl: null);
    }

    /// <inheritdoc />
    public async Task<VerbResult> DeployAsync(
        WorkloadDeployRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);

        // If a deploy plan was already surfaced (the environment is Provisioning with a completed plan
        // run), this Deploy is the Article VIII confirmation → release the gated apply.
        var existing = await registry
            .FindByNaturalKeyAsync(EnvironmentKind.Workload, request.Subscription, request.WorkloadName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is { Status: EnvironmentStatus.Provisioning })
        {
            var latest = (await registry.GetRunsAsync(existing.EnvId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();
            if (IsSucceededPlan(latest))
            {
                return await ConfirmDeployAsync(existing, request, confirmation, cancellationToken).ConfigureAwait(false);
            }

            // The plan hasn't succeeded yet: distinguish "plan still running" / "plan failed" from a
            // genuine concurrent mutation (FR-019).
            throw PlanGate.RejectionFor(existing.EnvId, existing.Status, latest);
        }

        // Direct one-shot deploy: full validation chain, then dispatch mode=apply.
        var prepared = await PrepareDeployAsync(request, cancellationToken).ConfigureAwait(false);
        var envId = await ClaimAndStampAsync(request, prepared, cancellationToken).ConfigureAwait(false);

        using var activity = ControlPlaneTelemetry.StartVerb("workload.deploy", envId);

        var runId = Guid.CreateVersion7();
        var applyInputs = BuildDeployInputs(envId, request, prepared, RunPhase.Apply);

        var begin = new BeginWorkloadDeploy(envId, new WorkloadDispatchInputs(
            request.Subscription, prepared.Region, request.WorkloadName, prepared.Owner,
            runId, DeployWorkflow, gitHubOptions.DefaultBranch, applyInputs, RunPhase.Apply));

        await StartOrAbortAsync(begin, envId, cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            envId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[]
            {
                $"Deploying workload '{request.WorkloadName}' (archetype '{request.Archetype}' " +
                $"{prepared.Version.Version}) into spoke '{request.SpokeName}'.",
                $"Dispatched {DeployWorkflow} (mode=apply); tracking by env_id {envId}.",
            });
    }

    /// <inheritdoc />
    public Task<PlanResult> PlanDestroyAsync(EnvRef workload, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Workload destroy lands with spec 008 US2 (T036); until then, no destroy path exists.");

    /// <inheritdoc />
    public Task<VerbResult> DestroyAsync(
        EnvRef workload,
        Confirmation confirmation,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Workload destroy lands with spec 008 US2 (T036); until then, no destroy path exists.");

    /// <summary>
    /// Everything a deploy resolves and proves <b>before</b> any intent exists (R3, minus shape which
    /// the callers run first): the active archetype + newest active version, schema-conformant
    /// parameters (canonicalized once for compare/persist/dispatch), the Active target spoke (whose
    /// region the workload copies), and the repeat-deploy guard.
    /// </summary>
    private async Task<PreparedDeploy> PrepareDeployAsync(
        WorkloadDeployRequest request,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var owner = ResolveOwner(request.Owner);

        // Catalog: unknown and retired are refused distinctly (FR-002 / US4-AS2).
        var archetype = await catalog.FindArchetypeAsync(request.Archetype, cancellationToken).ConfigureAwait(false)
            ?? throw ArchetypeNotDeployableException.Unknown(request.Archetype);
        if (archetype.Status != ArchetypeStatus.Active)
        {
            throw ArchetypeNotDeployableException.Retired(request.Archetype);
        }

        var version = await catalog.ResolveNewestVersionAsync(request.Archetype, cancellationToken).ConfigureAwait(false)
            ?? throw ArchetypeNotDeployableException.Unknown(request.Archetype);

        // Schema: every violation is surfaced at once; nothing is recorded or dispatched (FR-003).
        var violations = Validation.ParameterSchemaEvaluator.Evaluate(version.ParameterSchema, request.Parameters);
        if (violations.Count > 0)
        {
            throw new WorkloadParameterValidationException(request.Archetype, version.Version, violations);
        }

        // Spoke: must exist and be Active (US1-AS5).
        var spoke = await registry
            .FindByNaturalKeyAsync(EnvironmentKind.Spoke, request.Subscription, request.SpokeName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EnvironmentNotFoundException(
                EnvRef.ByNaturalKey(EnvironmentKind.Spoke, request.Subscription, request.SpokeName));
        if (spoke.Status != EnvironmentStatus.Active)
        {
            throw new SpokeNotActiveException(request.SpokeName, spoke.Status);
        }

        var canonicalParameters = CanonicalJson.Serialize(request.Parameters);

        // Repeat-deploy guard (contracts/workload-verbs.md): an existing DEPLOYED workload with
        // identical configuration converges idempotently; differing parameters/archetype are refused —
        // the path is destroy → deploy. Terminal Failed/Destroyed rows redeploy freely (the natural-key
        // claim converges them), and non-terminal rows hit the single-flight guard at the claim.
        var existing = await registry
            .FindByNaturalKeyAsync(EnvironmentKind.Workload, request.Subscription, request.WorkloadName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is { Status: EnvironmentStatus.Active })
        {
            var details = await registry.FindWorkloadDetailsAsync(existing.EnvId, cancellationToken).ConfigureAwait(false);
            if (details is not null && !SameConfiguration(details, request.Archetype, canonicalParameters))
            {
                throw new WorkloadParametersChangedException(request.WorkloadName);
            }
        }

        return new PreparedDeploy(owner, spoke.Region, version, canonicalParameters);
    }

    /// <summary>
    /// Claims the natural key (idempotent convergence + single-flight — FR-022/FR-022a) and writes the
    /// workload detail row with the <b>stamped</b> version (FR-005). If stamping fails the claim is
    /// aborted so nothing orphans (FR-023).
    /// </summary>
    private async Task<Guid> ClaimAndStampAsync(
        WorkloadDeployRequest request,
        PreparedDeploy prepared,
        CancellationToken cancellationToken)
    {
        var envId = await registry
            .BeginCreateAsync(
                EnvironmentKind.Workload, request.Subscription, prepared.Region, request.WorkloadName,
                prepared.Owner, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await registry.UpsertWorkloadDetailsAsync(
                new WorkloadDetails
                {
                    EnvId = envId,
                    SpokeSubscription = request.Subscription,
                    SpokeName = request.SpokeName,
                    ArchetypeName = prepared.Version.ArchetypeName,
                    ArchetypeVersion = prepared.Version.Version,
                    PdpEnv = request.Environment,
                    Parameters = prepared.CanonicalParameters,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await registry.AbortCreateAsync(envId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return envId;
    }

    /// <summary>Releases the gated apply for a workload whose deploy plan was surfaced (the confirm path).</summary>
    private async Task<VerbResult> ConfirmDeployAsync(
        Environment env,
        WorkloadDeployRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken)
    {
        // Deploy confirmation may be implicit (a --yes after the plan is shown); require only that the
        // owner did confirm.
        if (!confirmation.IsConfirmed)
        {
            throw new ConfirmationRequiredException(env.Name);
        }

        // The apply must dispatch EXACTLY what was planned: the stamped version + stored parameters.
        // A confirming request that drifted from the plan is the repeat-deploy refusal, not a re-plan.
        var details = await registry.FindWorkloadDetailsAsync(env.EnvId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Workload {env.EnvId} has no detail row; the deploy plan cannot be confirmed.");
        if (!SameConfiguration(details, request.Archetype, CanonicalJson.Serialize(request.Parameters)))
        {
            throw new WorkloadParametersChangedException(env.Name);
        }

        var version = await catalog
            .FindVersionAsync(details.ArchetypeName, details.ArchetypeVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Stamped archetype version '{details.ArchetypeName}' {details.ArchetypeVersion} is missing " +
                "from the catalog projection.");

        using var activity = ControlPlaneTelemetry.StartVerb("workload.confirm-deploy", env.EnvId);

        var runId = Guid.CreateVersion7();
        var applyInputs = BuildDeployInputs(env, details, version, RunPhase.Apply);

        await bus.InvokeAsync(
            new ConfirmationGiven(env.EnvId, runId, DeployWorkflow, gitHubOptions.DefaultBranch, RunPhase.Apply, applyInputs),
            cancellationToken).ConfigureAwait(false);

        return new VerbResult(
            env.EnvId,
            EnvironmentStatus.Provisioning,
            runId,
            GitHubRunUrl: null,
            RunOutcome.Dispatched,
            new[]
            {
                $"Confirmed plan; dispatched {DeployWorkflow} (mode=apply) for workload '{env.Name}' " +
                $"(archetype '{details.ArchetypeName}' {details.ArchetypeVersion}).",
            });
    }

    /// <summary>
    /// Runs the saga Start in one durable transaction; a rolled-back start drops the claim (and its
    /// cascade-deleted detail row) so a failed deploy leaks nothing (FR-011/FR-023).
    /// </summary>
    private async Task StartOrAbortAsync(
        BeginWorkloadDeploy begin,
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

    /// <summary>
    /// True when a stored detail row matches the requested archetype + canonical parameters. The
    /// stored value round-trips through Postgres jsonb (which normalizes key order/whitespace), so the
    /// compare re-canonicalizes both sides — cosmetic differences never read as a config change.
    /// </summary>
    private static bool SameConfiguration(WorkloadDetails details, string archetype, string canonicalParameters) =>
        string.Equals(details.ArchetypeName, archetype, StringComparison.Ordinal) &&
        string.Equals(
            CanonicalJson.Serialize(JsonNode.Parse(details.Parameters)),
            canonicalParameters,
            StringComparison.Ordinal);

    private static string ResolveOwner(string? owner) =>
        string.IsNullOrWhiteSpace(owner) ? System.Environment.UserName : owner;

    /// <summary>Builds the <c>workload-deploy.yml</c> inputs from a request (the plan / one-shot path).</summary>
    private static Dictionary<string, string> BuildDeployInputs(
        Guid envId,
        WorkloadDeployRequest request,
        PreparedDeploy prepared,
        RunPhase mode) => BuildDeployInputs(
            envId, mode, prepared.Region, request.Subscription, request.SpokeName, request.WorkloadName,
            prepared.Version, prepared.CanonicalParameters, request.Environment);

    /// <summary>Builds the <c>workload-deploy.yml</c> inputs from recorded rows (the confirm path).</summary>
    private static Dictionary<string, string> BuildDeployInputs(
        Environment env,
        WorkloadDetails details,
        ArchetypeVersion version,
        RunPhase mode) => BuildDeployInputs(
            env.EnvId, mode, env.Region, env.Subscription, details.SpokeName, env.Name,
            version, CanonicalJson.Serialize(JsonNode.Parse(details.Parameters)), details.PdpEnv);

    private static Dictionary<string, string> BuildDeployInputs(
        Guid envId,
        RunPhase mode,
        string region,
        string subscription,
        string spokeName,
        string workloadName,
        ArchetypeVersion version,
        string canonicalParameters,
        string pdpEnv) => new()
        {
            ["env_id"] = envId.ToString(),
            ["mode"] = RunNameCorrelation.ModeToken(mode),
            ["region"] = region,
            ["target_subscription_id"] = subscription,
            ["spoke_name"] = spokeName,
            ["workload_name"] = workloadName,
            // The catalog supplies WHERE the module lives and WHICH tag pins its content (R7); the
            // caller never chooses either.
            ["archetype_path"] = version.ModulePath,
            ["archetype_ref"] = $"archetype/{version.ArchetypeName}/{version.Version}",
            ["parameters_json"] = canonicalParameters,
            ["pdp_env"] = pdpEnv,
        };

    /// <summary>The pre-intent resolution of one deploy (R3 steps 2–4 + the repeat-deploy guard).</summary>
    private sealed record PreparedDeploy(
        string Owner,
        string Region,
        ArchetypeVersion Version,
        string CanonicalParameters);
}
