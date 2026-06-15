# Data Model: Platform Foundations

No database entities in this feature. The "data" are the published conventions every
later spec consumes — finalized here per FR-007/FR-008, plus the state-key registry and
the protected-resource enumeration.

## 1. Tag schema (final — FR-008)

| Tag | Required on | Allowed values | Notes |
|---|---|---|---|
| `pdp-managed` | Every PDP-managed resource group | `true` | Presence + `true` is the inventory filter; never `false` (unmanaged = untagged) |
| `pdp-deployed-by` | Every PDP-managed resource group | `github-actions` \| `control-plane` \| `owner` | Enumerated actors (clarification Q5); run-level provenance lives in the provisioning-run audit trail (spec 2/6), not tags |
| `pdp-fabric` | RGs belonging to a regional fabric | Azure region name (e.g., `eastus2`) | Omitted on non-fabric scopes |
| `pdp-spoke` | RGs belonging to a spoke | Spoke name (lowercase, `[a-z0-9-]{1,24}`) | Omitted on non-spoke scopes |
| `pdp-workload` | RGs holding a workload | Workload name (lowercase, `[a-z0-9-]{1,24}`) | Omitted elsewhere |
| `pdp-env` | RGs holding a workload | Environment name (e.g., `dev`, `demo`; lowercase, `[a-z0-9-]{1,16}`) | Omitted elsewhere; the grouping key for "what environments do I have?" |

**Scope rule**: inapplicable scope tags are **omitted**, not set to a sentinel. Universal
tags (`pdp-managed`, `pdp-deployed-by`) appear on every managed RG without exception.

**Foundation resources carry**: `pdp-managed=true`, `pdp-deployed-by=owner` (bootstrap)
then `github-actions` (once CI manages the stack — tag value updates with first CI apply).

## 2. Naming convention (final — FR-007)

**Pattern**: `<type>-pdp-<region>-<name>` — lowercase, hyphen-separated.

- `<type>`: CAF abbreviation (`rg`, `st`, `id`, `vnet`, `snet`, `pip`, `afw`,
  `ca`, `psql`, `bas`, `nsg`, `rt`, `log`…). The authoritative list is the CAF
  abbreviations page; the conventions doc pins the subset PDP uses and grows it by PR.
  (`log`, not the retired `law`, is current CAF; private DNS zones are named by their
  DNS domain, not a `pdnsz` token — see `docs/conventions.md` §1.1, §1.3.)
- `<region>`: full Azure region name (`eastus2`) in resource names.
- `<name>`: purpose label, lowercase `[a-z0-9-]`.

**Constrained-name exception** (storage accounts, and future ACR-like resources — no
hyphens, length caps, global uniqueness): `<type>pdp<region-short><name><4-char-suffix>`
where `<region-short>` comes from a pinned table (`eastus2` → `eus2`) and the suffix is a
stable random string generated at create time. Example: `stpdpeus2state8d2k`.

**This feature's names** (all new resources — full convention, no exceptions needed):

| Resource | Name |
|---|---|
| PDP state resource group | `rg-pdp-eastus2-foundations` |
| PDP state storage account | `stpdpeus2state<suffix>` (constrained-name pattern) |
| PDP state container | `tfstate` |
| CI managed identity | `id-pdp-eastus2-github-ci` |
| Management lock | `lock-pdp-eastus2-foundations` |

**Out of scope**: the seed backend (`cmhtfstatesa` / `RG-TF`) is owner-managed and
outside PDP's naming, tagging, and inventory scope entirely.

## 3. State key registry (FR-002 + foundations key) — two backends

| Deployable unit | Backend | Key | Introduced by |
|---|---|---|---|
| Platform foundations | **Seed** (`cmhtfstatesa/tfstate`) | `pdp/foundations` | this spec |
| Regional hub fabric | PDP | `fabrics/<region>` | spec 3 |
| Spoke | PDP | `spokes/<sub-id>/<spoke-name>` | spec 4 |
| Workload deployment | PDP | `workloads/<sub-id>/<spoke-name>/<workload-name>` | spec 8 |
| Future platform-plane stacks | PDP | `platform/<stack>` | spec 2+ |

The seed backend holds exactly one PDP key (`pdp/` prefix namespaces it among the
owner's personal states). Everything else lives in the PDP backend's `tfstate`
container; keys are the namespace.

## 4. Version pins (FR-005, FR-006)

| File | Pins | Enforced by |
|---|---|---|
| `.opentofu-version` | OpenTofu 1.11.x (exact) | `opentofu/setup-opentofu` in CI; `tenv`/equivalent locally |
| `global.json` | .NET 10 SDK (rollForward latestFeature) | dotnet CLI (from spec 6) |
| `required_version` per stack | `~> 1.11.0` | tofu init |
| Provider constraints per stack | `azurerm ~> 4.x`, `azapi ~> 2.x` (minor-pinned) | tofu init |
| `.terraform.lock.hcl` per stack (committed) | exact provider builds | tofu init; bumps = lockfile PR diffs |
| AVM module `version =` | exact (`X.Y.Z`) | tofu init |

## 5. Protected resources (FR-004 — the Article IV carve-out, enumerated)

| Resource | Protection | Why |
|---|---|---|
| `rg-pdp-eastus2-foundations` | `CanNotDelete` management lock | Holds the PDP state account |
| `stpdpeus2state<suffix>` | lock (inherited) + `prevent_destroy` | All PDP unit state lives here |
| `tfstate` container (PDP) | `prevent_destroy` | The state objects themselves |
| `lock-pdp-eastus2-foundations` | removable only via reviewed PR | The protection itself |

The seed backend needs no PDP protection — PDP never has delete rights on it (data-plane
container RBAC only). Everything else in the foundations stack (UAMI, federated
credentials, role assignments) tears down cleanly with a confirmed destroy. Removing
protection = PR deleting the lock resource + lifecycle flags, then confirmed destroy
(the "deliberate, reviewable step").

## 6. Lifecycle / state transitions

Foundations stack states: `unbootstrapped` (seed exists, no PDP resources) → (init
against seed + plan/apply) → `bootstrapped` (PDP backend live, foundations state in
seed) → `managed` (CI-applied from then on). Partial failure re-converges by re-running
plan/apply (research §1). Failure domains: seed lost → foundations state only
(re-import small resource set); PDP backend lost → re-run foundations to recreate it
(other units re-bootstrap per clarification Q2).
