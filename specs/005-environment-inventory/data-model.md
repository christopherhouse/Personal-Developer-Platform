# Phase 1 Data Model — Environment Inventory

The typed domain model returned by the inventory component, plus the deterministic classification
and drift decision tables that produce it. All types are **immutable records** (read-only result;
no persistence — there is no database in this spec). Field names are indicative; the binding surface
is `contracts/inventory-interfaces.md`.

---

## 1. Input row (the ARG boundary)

`ResourceGroupRow` — one row per resource group returned by `IResourceGraphReader`, projected from
the ARG `ResourceContainers` query (research §1). This is the **only** Azure-shaped type; everything
downstream is pure.

| Field | Type | Source |
|---|---|---|
| `Id` | string | ARG `id` (full ARM resource-group ID) |
| `Name` | string | ARG `name` |
| `SubscriptionId` | string (GUID) | ARG `subscriptionId` |
| `Location` | string | ARG `location` (the region authority for spokes/workloads) |
| `Tags` | `IReadOnlyDictionary<string,string>` | ARG `tags` (case-insensitive lookups) |

A second, smaller projection (`name`, `subscriptionId`, `location`) feeds **invisible**-drift
detection for `rg-pdp-*`-named groups lacking `pdp-managed == 'true'` (research §1).

---

## 2. Tag schema (validation source — `docs/conventions.md` §2)

| Tag | Required on | Allowed values | Role in model |
|---|---|---|---|
| `pdp-managed` | every managed RG | `true` | inclusion filter (FR-003) |
| `pdp-deployed-by` | every managed RG | `github-actions` \| `control-plane` \| `owner` | conformance (FR-013) |
| `pdp-fabric` | fabric RGs | Azure region name | type = fabric; region check |
| `pdp-platform` | platform-shared RGs (foundations / DNS / control plane) | `true` | type = platform; region from `Location` |
| `pdp-spoke` | spoke RGs | `[a-z0-9-]{1,24}` | type = spoke |
| `pdp-workload` | workload RGs | `[a-z0-9-]{1,24}` | type = workload |
| `pdp-env` | workload RGs | `[a-z0-9-]{1,16}` | environment grouping key |

Scope tags are present **exactly when the scope applies** (never a sentinel). `pdp-env` is a
**workload** tag; spokes/fabrics are environment-agnostic.

---

## 3. Classification decision table (pure)

Applied to each **managed** RG (`pdp-managed =~ 'true'`); `scopeCount` = number of present scope tags
among {`pdp-fabric`, `pdp-platform`, `pdp-spoke`, `pdp-workload`}.

| Condition | Outcome | Notes |
|---|---|---|
| `scopeCount == 1` and tag is `pdp-fabric` | **FabricItem** | region = tag value; cross-checked vs `Location` |
| `scopeCount == 1` and tag is `pdp-platform` | **PlatformItem** | region = `Location`; platform-shared infra |
| `scopeCount == 1` and tag is `pdp-spoke` | **SpokeItem** | region = `Location`; name = tag value |
| `scopeCount == 1` and tag is `pdp-workload` | **WorkloadItem** | region = `Location`; grouped by `pdp-env` |
| `scopeCount == 0` | **orphan drift** + no taxonomy item | managed but unclassifiable (FR-012) |
| `scopeCount > 1` | **ambiguous drift** + no taxonomy item | conflicting scope tags (FR-015) |

A classified item is **additionally** scanned for conformance drift (§4) — being classifiable and
being convention-clean are independent (e.g., a spoke with a malformed name is still a spoke **and**
a conformance finding).

---

## 4. Drift categories

`DriftCategory` (enum): `Orphan`, `Conformance`, `Invisible`, `Ambiguous`.

| Category | Trigger | FR |
|---|---|---|
| **Orphan** | managed RG with zero scope tags / unclassifiable | FR-012 |
| **Ambiguous** | managed RG with >1 scope tag | FR-015 |
| **Conformance** | bad tag value or missing required tag: invalid `pdp-fabric` region; `pdp-spoke`/`pdp-workload`/`pdp-env` regex fail; `pdp-deployed-by` not in enum; workload RG missing `pdp-env`; any RG missing a universal tag; `pdp-fabric` ≠ `Location` | FR-013 |
| **Invisible** | RG named `rg-pdp-*` but lacking `pdp-managed == 'true'` | FR-014 |

`DriftFinding` record: `{ Category, ResourceGroupId, ResourceGroupName, SubscriptionId, OffendingTag?, Detail }`.
Findings are **informational** — their presence never fails the inventory call (FR-016 / clarify Q5).

**Exclusion**: the owner-managed seed backend (`RG-TF` resource group / `cmhtfstatesa` account) is
never classified and never a drift finding (FR-018).

---

## 5. Output model (the snapshot)

```text
InventorySnapshot
├── Environments        : IReadOnlyList<EnvironmentView>     # workloads grouped by pdp-env (SC-004)
├── Fabrics             : IReadOnlyList<FabricItem>
├── Platform            : IReadOnlyList<PlatformItem>        # platform-shared infra (pdp-platform)
├── Spokes              : IReadOnlyList<SpokeItem>
├── Workloads           : IReadOnlyList<WorkloadItem>        # flat list (also referenced by Environments)
├── Drift               : IReadOnlyList<DriftFinding>
└── Coverage            : IReadOnlyList<SubscriptionCoverage> # one per discovered subscription
```

| Type | Key fields |
|---|---|
| `EnvironmentView` | `Name` (pdp-env), `Workloads : IReadOnlyList<WorkloadItem>` |
| `FabricItem` | `Region` (pdp-fabric), `SubscriptionId`, `ResourceGroupName`, `Location` |
| `PlatformItem` | `SubscriptionId`, `Region` (=Location), `ResourceGroupName` |
| `SpokeItem` | `Name` (pdp-spoke), `SubscriptionId`, `Region` (=Location), `ResourceGroupName` |
| `WorkloadItem` | `Name` (pdp-workload), `Environment` (pdp-env, nullable→conformance), `SubscriptionId`, `Region`, `ResourceGroupName` |
| `SubscriptionCoverage` | `SubscriptionId`, `DisplayName`, `Status` ∈ {`Queried`, `Inaccessible`} |
| `ManagedResourceGroup` | shared base detail for the above (Id, Name, SubscriptionId, Location, Tags) |

### Query projections over the snapshot (headline questions, FR-007/008, SC-004)

| Question | Projection |
|---|---|
| "What environments do I have deployed?" | `Environments.Select(e => e.Name)` |
| "Which spokes exist, in which subscription and region?" | `Spokes` (each has SubscriptionId + Region) |
| "What is in environment X?" | `Environments.First(e => e.Name == X).Workloads` (empty result if none — clean, not error) |

---

## 6. Relationships & invariants

- A managed RG yields **at most one** taxonomy item (fabric XOR spoke XOR workload) and **zero or
  more** drift findings.
- An RG that is orphan/ambiguous yields **no** taxonomy item but **one** drift finding.
- Every `WorkloadItem` with a non-null `Environment` appears in exactly one `EnvironmentView`;
  workloads missing `pdp-env` appear in `Workloads` and raise a conformance finding but join no
  `EnvironmentView`.
- `Coverage` has exactly one entry per discovered subscription; the union of `Queried` subscriptions
  is the scope all taxonomy/drift was computed over (SC-008).
- No state transitions: a snapshot is a pure, point-in-time projection of ARG; re-running recomputes
  from scratch (Article III). No identity/sequence is persisted.
