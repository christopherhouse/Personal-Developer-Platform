# Contract — Workload Verbs, MCP Tools, CLI Commands

Extends the spec-006 verb contract; nothing existing changes shape. All types are
immutable `sealed record`s in `Pdp.ControlPlane.Verbs.Model`, System.Text.Json
serializable.

## Verb interface

```csharp
public interface IWorkloadVerbs
{
    // validate (shape → catalog → schema → spoke) → record intent → dispatch plan run
    Task<PlanResult> PlanDeployAsync(WorkloadDeployRequest request, CancellationToken ct = default);

    // confirmation path: env in Provisioning with succeeded plan → ConfirmationGiven;
    // otherwise direct one-shot apply (mirrors SpokeVerbs.CreateAsync)
    Task<VerbResult> DeployAsync(WorkloadDeployRequest request, Confirmation confirmation, CancellationToken ct = default);

    Task<PlanResult> PlanDestroyAsync(EnvRef workload, CancellationToken ct = default);

    // ConfirmationGuard.RequireMatch(confirmation, workload.Name) BEFORE any dispatch
    Task<VerbResult> DestroyAsync(EnvRef workload, Confirmation confirmation, CancellationToken ct = default);
}
```

## Request / result types

```csharp
public sealed record WorkloadDeployRequest(
    string Subscription,        // target subscription (must equal the spoke's)
    string SpokeName,           // existing managed spoke (kind=spoke, Active)
    string WorkloadName,        // ^[a-z0-9-]{1,24}$ ; unique per subscription
    string Archetype,           // catalog name; version resolved server-side (newest active)
    string Environment,         // pdp-env value, ^[a-z0-9-]{1,16}$
    JsonObject Parameters,      // validated against the archetype's JSON schema
    string? Owner = null);
```

`PlanResult` / `VerbResult` / `EnvRef` / `Confirmation` are reused unchanged. Workload
`EnvRef` natural key = `(kind: workload, subscription, name)`.

**Repeat-deploy semantics** (spec edge case): `PlanDeployAsync`/`DeployAsync` against an
existing workload with **identical** `Parameters` (canonical-JSON compare vs the stored
`workloads.parameters`) follows the established idempotent-convergence behavior; with
**differing** `Parameters` (or a different archetype) the request is refused with
`WorkloadParametersChangedException` — in-place reconfiguration is out of scope, the
path is destroy → deploy.

New exception: `WorkloadParameterValidationException` — carries
`IReadOnlyList<ParameterViolation>` (`Path`, `Keyword`, `Message`) from JsonSchema.Net
`OutputFormat.List`; CLI exit code 2, MCP tool returns the violations verbatim
(schema-derived error naming the offending parameters, FR-003).
`ArchetypeNotDeployableException` — unknown vs retired distinguished in the message
(FR-002 / US4-AS2). `WorkloadParametersChangedException` — repeat deploy with differing
parameters (see repeat-deploy semantics above). `SpokeHasActiveWorkloadsException` —
thrown by **SpokeVerbs**
plan-destroy/destroy when workloads survive; message lists survivor names (FR-021).

## Saga messages (Registry/Lifecycle)

```csharp
public sealed record BeginWorkloadDeploy(Guid EnvId, WorkloadDispatchInputs Inputs);   // no IPAM step
public sealed record BeginWorkloadDestroy(Guid EnvId, WorkloadDispatchInputs Inputs);  // no release step
```

`ConfirmationGiven`, `DispatchWorkflowCommand`, `RunCompleted`/`RunFailed` reused as-is.

## Dispatch inputs (verb → workflow)

Built by `WorkloadVerbs`, persisted on the `provisioning_runs.dispatch_inputs` jsonb as
today:

| Input | Source |
|---|---|
| `env_id`, `mode` | saga (plan/apply/destroy) |
| `region` | copied from the spoke's registry row |
| `target_subscription_id`, `spoke_name`, `workload_name` | request |
| `archetype_path` | catalog `module_path` |
| `archetype_ref` | full tag `archetype/<name>/<version>` (stamped version on destroy) |
| `parameters_json` | the validated `Parameters`, canonical JSON |
| `pdp_env` | request `Environment` |
| `destroy-confirm` | `env.Name` (destroy workflows only — second gate layer) |

## MCP tools (`Pdp.Mcp/Tools/WorkloadTools.cs`, extends `OwnerTool`)

| Tool | Signature (conceptual) | Behavior |
|---|---|---|
| `PlanWorkloadDeploy` | subscription, spokeName, workloadName, archetype, environment, parameters (JSON) | verb `PlanDeployAsync` → `tokens.Issue(WorkloadDeploy, workloadName, plannedRequest)` → `McpPlanResult` (token + plan). Schema violations returned as data, no token issued. |
| `ApplyWorkloadDeploy` | confirmationToken, workloadName (verbatim) | `tokens.Redeem<WorkloadDeployRequest>` → `DeployAsync(..., Confirmation.ForApply())` → `tokens.Consume` |
| `PlanWorkloadDestroy` | subscription, workloadName | `PlanDestroyAsync` → token (`WorkloadDestroy`) |
| `DestroyWorkload` | confirmationToken, workloadName (verbatim) | redeem → `DestroyAsync(..., Confirmation.ForDestroy(workloadName))` → consume |

`ConfirmationOperation` enum gains `WorkloadDeploy`, `WorkloadDestroy`. Token
semantics unchanged (opaque 256-bit, 15-min TTL, single-use, verbatim-name match,
mismatch does not burn). A chat turn can never destroy without token + verbatim
restatement — Article VIII wording matches spoke tools.

`ListWorkloadEnvironments` and `WhatsDeployed` are **unchanged** — they light up via
tags alone (SC-004). `ShowEnvironment` works for workloads for free (kind=workload).

## CLI (`pdp workload …`, `Commands/WorkloadCommand.cs`)

```
pdp workload deploy  -s <sub> --spoke <spoke> -n <name> --archetype <name> \
                     --env <pdp-env> [--param key=value ...] [--parameters-file params.json] \
                     [--yes] [--no-wait] [--json]
pdp workload destroy -s <sub> -n <name> --confirm <name> [--no-wait] [--json]
```

Flow mirrors `SpokeCommand`: plan → `CompletionPoller.AwaitPlanAsync` → show plan +
prompt (`--yes` skips prompt for **deploy only**; destroy has no bypass — `--confirm`
must restate the name, exactly like `pdp spoke destroy`) → gated verb → await terminal.
Exit codes unchanged: 0 success · 1 run failed · 2 validation (incl. schema violations,
printed one per line as `<path>: <message>`) · 3 in-progress.

`--param` values parse as JSON scalars with string fallback; `--parameters-file` wins
on conflict. Both merge into the `Parameters` JsonObject.
