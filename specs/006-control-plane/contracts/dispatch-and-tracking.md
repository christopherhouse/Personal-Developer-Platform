# Contract — Dispatch & Run Tracking (`Pdp.ControlPlane.Dispatch`)

The execution-plane boundary: how the control plane **dispatches** GitHub Actions workflows (never
running OpenTofu in-process — Article II) and how it **tracks** them to a terminal outcome correlated
by `env_id` (FR-002..FR-005). Two seams plus the webhook/workflow input contracts.

---

## 1. `IWorkflowDispatcher` — outbound dispatch (FR-002/FR-003)

```csharp
public interface IWorkflowDispatcher
{
    // Trigger a workflow_dispatch as the pdp-orchestrator GitHub App; returns our ProvisioningRun id.
    Task<Guid> DispatchAsync(WorkflowDispatch dispatch, CancellationToken ct = default);
}

public sealed record WorkflowDispatch(
    string WorkflowFile,          // e.g. "spoke-vend.yml", "fabric-vend.yml", "spoke-destroy.yml"
    string GitRef,                // default branch
    Guid   EnvId,                 // correlation surrogate
    RunPhase Mode,                // Plan | Apply | Destroy  -> "mode" input
    IReadOnlyDictionary<string,string> Inputs);  // env_id, mode, spoke_cidr, subscription, region, name…
```

**Contract notes**

- Auth: **GitHubJwt** (App private key → 10-min JWT) → `CreateInstallationToken` (1-h token) →
  installation-scoped `GitHubClient.Actions.Workflows.CreateDispatch(...)` (research §4). The GitHub
  App credential is the **only** non-Azure secret (FR-020); no PATs, no stored cloud secrets.
- `Inputs` MUST include `env_id` and `mode`; for spoke create, `spoke_cidr` MUST be the
  ledger-allocated block (Article VI; FR-008) — never a user-supplied value.
- Outbound calls MUST use `Microsoft.Extensions.Http.Resilience` (Polly v8) retry/timeout.
- Dispatch MUST be enqueued through Wolverine's **durable outbox** so it is sent **iff** the
  allocate+record transaction commits (no leaked allocation, no orphan dispatch — FR-011).

## 2. `IRunTracker` — inbound completion (FR-004/FR-005, SC-006)

```csharp
public interface IRunTracker
{
    // Correlate a workflow_run signal (webhook OR reconciler) to an env_id and record terminal state.
    Task RecordRunStatusAsync(WorkflowRunStatus status, TrackingSource source, CancellationToken ct = default);

    // Reconciler sweep: find non-terminal runs and query GitHub for their current status.
    Task<int> ReconcileInFlightAsync(CancellationToken ct = default);
}

public sealed record WorkflowRunStatus(long GitHubRunId, string RunName, string Url,
                                       RunOutcome Outcome);
```

**Contract notes — correlation**

- `RunName` MUST embed `env_id` and `mode` (`pdp <mode> <env_id>`); the tracker parses it to resolve
  the `ProvisioningRun`/`Environment` (research §4). Unparseable/foreign runs are ignored.
- `RecordRunStatusAsync` MUST be **idempotent**: duplicate/late deliveries (same `(EnvId,
  GitHubRunId)`) MUST NOT corrupt the recorded outcome; first-terminal-wins, recording `TrackedBy`.
- On terminal outcome it emits `RunCompleted`/`RunFailed` to the lifecycle saga (data-model §3).

## 3. Polling reconciler (FR-004, SC-006)

- A **Wolverine scheduled message** reschedules itself every **≤60 s** and calls
  `ReconcileInFlightAsync`, which lists environments with status ∈ {Provisioning, Destroying} (or runs
  in {Dispatched, InProgress}) and queries the GitHub Actions API for each run's status.
- A missed-webhook run MUST reach recorded terminal status within **~2 min** (SC-006).
- For the **MVP** (no public ingress — Q1), the reconciler is the **sole** completion path; the
  webhook path is built + tested for spec-007 hosting.

## 4. Webhook ingress topology (FR-005; Clarifications Q2)

- **`Pdp.ControlPlane.Ingress`** — a **YARP** reverse proxy; the **only** public-facing surface
  (Article IX exception). It forwards `POST /webhooks/github` to the internal API; it terminates no
  business logic.
- **`Pdp.ControlPlane.Api`** — the **internal** webhook handler built with
  `Octokit.Webhooks.AspNetCore`: validates the **HMAC-SHA256 signature** (`X-Hub-Signature-256`),
  deserializes the typed `workflow_run` payload, and enqueues it to the durable inbox →
  `RecordRunStatusAsync`. The handler has **no direct public exposure** (defense in depth).
- Production deployment of both containers (ACA, the public endpoint, managed identity) is **spec
  007** (FR-019). This spec builds and tests them locally.

## 5. Dispatched workflow input contract (execution plane)

Every env workflow (`spoke-vend.yml`, **`fabric-vend.yml`** [new], `spoke-destroy.yml`,
`fabric-destroy.yml`) MUST accept and honor:

| Input | Purpose |
|---|---|
| `env_id` | correlation surrogate; echoed into `run-name` |
| `mode` | `plan` \| `apply` \| `destroy` — gates `tofu plan` vs `apply`/`destroy` (Article VIII) |
| `spoke_cidr` | (spoke create) the ledger-allocated block (FR-008) — no longer a hand-fitted input |
| `subscription`, `region`, `name` | the existing typed inputs (spec 003/004) |

- Each workflow MUST set **`run-name: pdp ${{ inputs.mode }} ${{ inputs.env_id }}`** for correlation.
- `mode=plan` MUST run `tofu plan` only (no state mutation); `apply`/`destroy` perform the mutation.
- Auth to Azure remains **OIDC, no stored cloud secrets** (Articles I/II) — unchanged from specs
  003/004; the control plane adds no cloud credentials.

## 6. Plan-output retrieval (Article VIII; resolves analysis U1)

The plan-before-apply gate is only real if the control plane can **surface the actual `tofu plan`**.
Mechanism:

- A `mode=plan` run MUST **write the plan to a durable artifact**: the workflow runs
  `tofu plan -no-color` (and `tofu show -json` for a structured form), writes the human summary to the
  **GitHub Step Summary** *and* uploads `plan.txt` (+ `plan.json`) as a **run artifact**
  (`actions/upload-artifact`). The run MUST NOT proceed to apply.
- After the plan run reaches terminal (tracked as any other run — §1/§2), `IRunTracker` MUST
  **download the `plan.txt` artifact** via the GitHub Actions API
  (`Actions.Artifacts.ListWorkflowArtifacts` → `DownloadArtifact`, installation-token auth) and store
  it as `ProvisioningRun.PlanSummary` (data-model §2). That `PlanSummary` is what the verb's
  `PlanResult` surfaces (contracts/verb-surface.md) for confirmation.
- Retrieval is best-effort-bounded: if the artifact is missing/oversized, the control plane surfaces
  the run URL + status and requires the owner to review in GitHub before confirming (never silently
  treats a missing plan as "approved").
