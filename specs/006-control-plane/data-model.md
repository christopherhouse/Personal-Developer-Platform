# Phase 1 — Data Model: Action Layer (Control Plane)

The control plane's persisted state lives in the **existing platform Postgres** in a **new `registry`
schema** (Clarifications Q3), beside the spec-002 `ipam` schema and Wolverine's `wolverine` schema.
Two registry entities — **Environment** (intent + lifecycle) and **Provisioning Run** (audit) — plus
the **Wolverine saga state** for the lifecycle, plus the **in-memory verb/result types** that the CLI
and future MCP consume. Division of truth holds: these record **intent and history**; *what is
deployed* is answered by the spec-005 inventory over Azure Resource Graph (FR-016), never from here.

Types are indicative C# records / EF Core entities; the binding intent is the fields, invariants, and
FR mapping. snake_case columns via `EFCore.NamingConventions`.

---

## 1. `Environment` (table `registry.environments`) — intent + lifecycle (FR-014)

The recorded **intent** for one managed environment.

| Field | Type | Notes |
|---|---|---|
| `EnvId` | `Guid` (UUIDv7) | **PK**; the surrogate correlation key passed to/from workflows (research §8). |
| `Kind` | `EnvironmentKind` enum | `Fabric` \| `Spoke` (workload deferred to spec 008). |
| `Subscription` | `string` | Target subscription id (Azure GUID). For fabric = platform subscription. |
| `Region` | `string` | Registered region (e.g. `westus3`). |
| `Name` | `string` | Spoke name; for fabric, the region (a fabric is identified by its region). |
| `Owner` | `string` | The requesting principal (owner identity; single-owner platform). |
| `Status` | `EnvironmentStatus` enum | `Requested` → `Provisioning` → `Active` \| `Failed`; `Destroying` → `Destroyed` \| `Failed`. |
| `SpokeCidr` | `IPNetwork?` | The block allocated from the ledger at vend (spoke only; null for fabric). Mirrors the ledger allocation (FR-008). |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | Audit timestamps. |

**Invariants**

- **Unique natural key `(Kind, Subscription, Name)`** — a unique index; the basis for idempotent
  convergence (FR-022). A re-create resolves to the existing `EnvId`, never a new surrogate.
- `EnvId` is immutable once assigned.
- `SpokeCidr`, when present, MUST equal the live ledger allocation for `(Region, Name)` (the ledger is
  the authority — Article VI; the registry mirrors it for reporting, it is not a second source).
- **Single-flight (FR-022a)**: a mutating verb is rejected while `Status ∈ {Requested, Provisioning,
  Destroying}` (non-terminal). Enforced at the verb boundary against the live row + the saga.
- `Status` is **intent/history**, NOT a deployment-truth claim; consumers asking "is it really
  deployed?" use inventory (FR-016).

**Lifecycle (state transitions)**

```text
                 create verb (plan→confirm→apply)
   (none) ──────────────────────────────────────▶ Requested
                                                      │ dispatch apply
                                                      ▼
                                                 Provisioning ──run failed──▶ Failed
                                                      │ run succeeded
                                                      ▼
                                                   Active
                                                      │ destroy verb (confirm→destroy)
                                                      ▼
                                                 Destroying ──run failed──▶ Failed
                                                      │ run succeeded (+ release allocation)
                                                      ▼
                                                  Destroyed
```

- Re-create on a `Destroyed`/`Failed` row is permitted (no in-flight run) and converges on the same
  `EnvId` (natural key).
- Reaching `Destroyed` for a spoke MUST follow a successful `ReleaseAsync` of its allocation (FR-009).

---

## 2. `ProvisioningRun` (table `registry.provisioning_runs`) — audit trail (FR-015)

One row per **dispatched execution-plane run** (any phase: plan, apply, destroy).

| Field | Type | Notes |
|---|---|---|
| `RunId` | `Guid` (UUIDv7) | **PK** (our id). |
| `EnvId` | `Guid` | **FK → environments**; the correlation key. |
| `Phase` | `RunPhase` enum | `Plan` \| `Apply` \| `Destroy` (the `mode` dispatched — research §3). |
| `WorkflowFile` | `string` | e.g. `spoke-vend.yml`, `fabric-vend.yml`, `spoke-destroy.yml`. |
| `DispatchInputs` | `jsonb` | The exact inputs sent (`env_id`, `mode`, `spoke_cidr`, subscription, region, name…). |
| `GitHubRunId` | `long?` | Actions run id, resolved by `run-name` correlation (research §4). |
| `GitHubRunUrl` | `string?` | Human link to the run. |
| `Outcome` | `RunOutcome` enum | `Dispatched` → `InProgress` → `Succeeded` \| `Failed` \| `Cancelled` \| `TimedOut`. |
| `PlanSummary` | `string?` | Captured plan output/summary surfaced for confirmation (Plan phase). |
| `DispatchedAt` / `CompletedAt` | `DateTimeOffset?` | Lifecycle timestamps. |
| `TrackedBy` | `TrackingSource` enum | `Webhook` \| `Reconciler` — which signal recorded the terminal outcome (SC-006 observability). |

**Invariants**

- Written at **dispatch time** (`Dispatched`) inside the same transaction/outbox as the intent write
  (research §6), so a committed environment always has its run record (no orphan dispatch).
- `GitHubRunId`/`Url` populated once the run is correlated (by `run-name` → `env_id`).
- Terminal `Outcome` set by **either** the webhook **or** the reconciler — whichever first observes
  terminal (idempotent; dedupe by `(EnvId, GitHubRunId)`), recording `TrackedBy` (SC-006).
- A `Plan`-phase run never mutates Azure; its `PlanSummary` gates confirmation (Article VIII).

---

## 3. `EnvironmentSaga` (Wolverine saga state, `registry.environment_saga`) — lifecycle engine (research §2)

The durable, event-driven lifecycle, persisted via Wolverine's EF Core saga storage.

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | **Saga id == `EnvId`** (one saga per environment). |
| `Status` | `EnvironmentStatus` | Mirrors the environment's lifecycle (the saga drives it). |
| `CurrentPhase` | `RunPhase?` | The phase currently in flight (plan/apply/destroy), or null. |
| `PendingConfirmation` | `bool` | True after a plan run completes, awaiting confirm (Article VIII). |
| `CurrentRunId` | `Guid?` | The `ProvisioningRun` in flight. |

- `Start(CreateRequested|DestroyRequested)` → writes intent + cascades `DispatchWorkflow(mode=plan)`.
- `Handle(RunCompleted{Plan})` → set `PendingConfirmation`, surface `PlanSummary`.
- `Handle(ConfirmationGiven)` → cascade `DispatchWorkflow(mode=apply|destroy)`.
- `Handle(RunCompleted{Apply})` → `Active`; `Handle(RunCompleted{Destroy})` → release allocation →
  `Destroyed` → `MarkCompleted()`.
- `Handle(RunFailed)` → `Failed` (allocation **not** released on failed destroy; provisional
  allocation released on failed create — FR-011/FR-025).
- **Existence + non-terminal status = the single-flight guard** (FR-022a).

---

## 4. Verb request/result types (in-memory; the surface CLI + MCP consume — FR-001/FR-010/FR-018)

Indicative shapes; full signatures in `contracts/`.

- **`SpokeCreateRequest`** `{ Subscription, Region, Name, Size (prefix length, default /24), Owner }`
  — **note: no `Cidr`** (Gate-G1 closed; allocated live — FR-008).
- **`FabricCreateRequest`** `{ Region, RegionIndex, Owner }`.
- **`DestroyRequest`** `{ EnvId | (Kind, Subscription, Name), Confirmation }`.
- **`PlanResult`** `{ EnvId, Phase=Plan, PlanSummary, ProposedInputs, RunUrl }` — surfaced before
  apply/destroy (Article VIII).
- **`VerbResult`** `{ EnvId, Status, RunId, GitHubRunUrl, Outcome, Messages[] }` — the typed outcome
  every verb returns; rendered human-readable or as `--json` (SC-008).
- **`IpamQueryResult`** — projection of `RegionView` (spec 002) for the `ipam query` verb.
- **Inventory results** — reused **as-is** from `Pdp.ControlPlane.Inventory` (spec 005); the control
  plane injects its credential (FR-013), it does not redefine the model.

All result types are immutable records, `System.Text.Json`-serializable (the `--json` contract).

---

## 5. Enumerations

| Enum | Values |
|---|---|
| `EnvironmentKind` | `Fabric`, `Spoke` |
| `EnvironmentStatus` | `Requested`, `Provisioning`, `Active`, `Destroying`, `Destroyed`, `Failed` |
| `RunPhase` | `Plan`, `Apply`, `Destroy` |
| `RunOutcome` | `Dispatched`, `InProgress`, `Succeeded`, `Failed`, `Cancelled`, `TimedOut` |
| `TrackingSource` | `Webhook`, `Reconciler` |

---

## 6. Schema / persistence notes

- **Schema `registry`** in the existing platform Postgres DB; **separate** from `ipam` and
  `wolverine`. EF Core migrations create it; **`registry` is independently droppable** for teardown
  (Article IV / FR-024 / SC-010).
- The `environments` ↔ `ipam.allocations` relationship is **by value** (`Region`+`Name` ↔ allocation
  `Name`), not a cross-schema FK — the ledger remains the authority and the registry only mirrors the
  CIDR for reporting (Article VI; no second source of address truth).
- Wolverine durability (durable inbox/outbox + saga storage) is provisioned in its own `wolverine`
  schema via `PersistMessagesWithPostgresql(...)` + `UseEntityFrameworkCoreTransactions()` — the same
  transaction spans `ipam` + `registry` writes for the atomic allocate-record-dispatch (research §6).
- **No Azure resource** is modeled or created here; App Insights is spec-007 hosting (FR-O1).
