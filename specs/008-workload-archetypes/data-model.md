# Data Model — Workload Archetypes (spec 008)

All new objects live in the existing `registry` schema (`RegistryDbContext`,
snake_case via EFCore.NamingConventions), one new EF migration. Postgres remains
intent + catalog projection; Azure Resource Graph remains deployed truth.

## Extended enum

```csharp
public enum EnvironmentKind { Fabric, Spoke, Workload }   // persisted lowercase text
```

`EnvironmentStatus`, `RunPhase`, `RunOutcome`, `TrackingSource` unchanged — workloads
reuse the full status machine (`Requested → Provisioning → Active → Destroying →
Destroyed | Failed`) and run-audit trail.

## New entities

### `registry.archetypes` — Archetype

| Column | Type | Notes |
|---|---|---|
| `name` | text PK | catalog identity, `^[a-z0-9-]{1,32}$` |
| `description` | text | human summary (shown by CLI/MCP when listing) |
| `status` | text | `active` \| `retired` — retired = no new deploys, existing workloads unaffected (FR-004) |
| `created_at` / `updated_at` | timestamptz | sync-maintained |

### `registry.archetype_versions` — ArchetypeVersion (immutable)

| Column | Type | Notes |
|---|---|---|
| `archetype_name` | text FK → archetypes | composite PK part |
| `version` | text | composite PK part; the git tag suffix (`v1.0.0`); full ref = `archetype/<name>/<version>` |
| `module_path` | text | repo-relative OpenTofu module dir (`archetypes/container-app-sql`) |
| `parameter_schema` | jsonb | JSON Schema draft 2020-12 (see contracts/archetype-catalog.md) |
| `content_hash` | text | SHA-256 over `(module_path, canonical parameter_schema)` — **sync refuses a differing hash for an existing version** (R2) |
| `registered_at` | timestamptz | first sync that introduced it |

Ordering: versions compare by SemVer; "newest active version" = max SemVer of an
`active` archetype. No per-version status in v1.

### `registry.catalog_syncs` — CatalogSync (audit, FR-006)

| Column | Type | Notes |
|---|---|---|
| `id` | uuid v7 PK | |
| `content_hash` | text | hash of the whole catalog.json |
| `outcome` | text | `applied` \| `no_change` \| `rejected` |
| `summary` | jsonb | per-entry actions (added/retired/reactivated/rejected + reason) |
| `applied_at` | timestamptz | |

### `registry.workloads` — WorkloadDetails (1:1 extension of a managed unit)

| Column | Type | Notes |
|---|---|---|
| `env_id` | uuid PK, FK → environments.env_id (cascade) | the managed-unit row (`kind='workload'`) |
| `spoke_subscription` | text | target subscription of the containing spoke |
| `spoke_name` | text | containing spoke (state key + FR-021 guard join) |
| `archetype_name` | text | FK → archetypes |
| `archetype_version` | text | **stamped permanently at deploy** (FR-005); with name, FK → archetype_versions |
| `pdp_env` | text | workload environment, `^[a-z0-9-]{1,16}$` (TagSchema.IsValidEnvName) |
| `parameters` | jsonb | the schema-validated caller input, as dispatched |
| `created_at` | timestamptz | |

Index: `(spoke_subscription, spoke_name)` — powers the FR-021 spoke-destroy guard
("name the surviving workloads") and spoke-scoped listings.

## Reused entity: `registry.environments`

A workload = one row with `kind='workload'`, `subscription` = target subscription,
`region` = the spoke's region (copied at deploy), `name` = workload name
(`^[a-z0-9-]{1,24}$`, TagSchema.IsValidResourceName). `spoke_cidr` stays NULL —
workloads carve no address space. Natural key `(kind, subscription, name)` unchanged ⇒
**workload names unique per subscription** (R4, recorded trade-off).

## Relationships

```
archetypes 1 ──── * archetype_versions
archetype_versions 1 ──── * workloads (stamped version; never mutated)
environments (kind=workload) 1 ──── 1 workloads (env_id)
environments (kind=spoke)    1 ──── * workloads (by (spoke_subscription, spoke_name) natural join — intent-level, no FK)
environments 1 ──── * provisioning_runs (existing, unchanged)
```

## Lifecycle / state transitions

- **Catalog**: file merge → image deploy → startup sync. Transitions: (absent → active),
  (active ↔ retired) at archetype level; versions append-only, content-immutable.
- **Workload deploy**: `BeginWorkloadDeploy` → env row `Requested/Provisioning` +
  plan run dispatched → `ConfirmationGiven` → apply run → `Active` (or `Failed`).
  Identical saga to spokes minus IPAM allocation.
- **Workload destroy**: verbatim-restatement gate → `BeginWorkloadDestroy` →
  `Destroying` → destroy run → `Destroyed`. No allocation release step.
- **Spoke destroy** (guard, FR-021): refused while
  `workloads WHERE spoke = target AND environment.status NOT IN (Destroyed)` is
  non-empty; refusal message lists survivor names.

## Validation rules (enforcement points)

| Rule | Where |
|---|---|
| workload name / pdp-env / archetype name formats | FluentValidation (`WorkloadDeployValidator`) — same regexes as `TagSchema` |
| archetype exists, `active`; version resolved = newest active | `WorkloadVerbs` catalog lookup (before intent) |
| parameters ⊨ archetype JSON schema | JsonSchema.Net eval, `OutputFormat.List`; failures name parameter + constraint (FR-003) |
| target spoke exists, `kind=spoke`, status `Active` | registry lookup (before intent) |
| natural-key idempotency / single-flight | existing `BeginCreateAsync` + `PlanGate` (unchanged) |
| destroy verbatim restatement | `ConfirmationGuard.RequireMatch` + workflow `destroy-confirm` input (both layers) |
| version immutability | `content_hash` check in `CatalogSyncService` (sync-fatal) |
