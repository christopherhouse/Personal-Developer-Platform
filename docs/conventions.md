# Conventions — Naming & Tags (PDP)

The binding naming convention and tag schema for every PDP-managed resource. Finalized
by **001-platform-foundations** (FR-007, FR-008) and consumed by every later
resource-creating spec. Authoritative source tables live in
[`specs/001-platform-foundations/data-model.md`](../specs/001-platform-foundations/data-model.md)
§1–2; the rules contract is
[`contracts/tagging-and-naming.md`](../specs/001-platform-foundations/contracts/tagging-and-naming.md).
This document is the published, growable version those reference.

> **Scope.** These conventions apply to **PDP-managed** resources only. The owner's
> seed backend (`cmhtfstatesa` / `RG-TF`) is owner-managed and explicitly **out of
> scope** for PDP naming, tagging, and inventory (data-model.md §2).
>
> **Enforcement.** Tags and names are set by IaC only. A hand-edited tag or a portal
> rename is drift (constitution Article I) and the next `tofu plan` reverts it. Names
> are **immutable post-creation** — a rename is a destroy/recreate decision. Automated
> Azure Policy enforcement arrives in spec 10; until then conformance is by code review
> and plan output (SC-004 targets 100%).

---

## 1. Naming convention

### Default pattern

```
<type>-pdp-<region>-<name>
```

- Lowercase, hyphen-separated.
- **`<type>`** — the CAF abbreviation for the resource type (§1.1).
- **`pdp`** — fixed platform discriminator (every PDP-managed resource carries it).
- **`<region>`** — the **full** Azure region name (`eastus2`), not the short form. The
  short form is reserved for constrained names (§1.3).
- **`<name>`** — purpose label, `[a-z0-9-]`.

Worked examples (this feature's resources):

| Resource | Type | Name |
|---|---|---|
| Resource group (state) | `rg` | `rg-pdp-eastus2-foundations` |
| Managed identity (CI) | `id` | `id-pdp-eastus2-github-ci` |
| Management lock | `lock` | `lock-pdp-eastus2-foundations` |

### 1.1 Pinned CAF abbreviation subset

`<type>` values come from the **[CAF abbreviation
recommendations](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations)**
(the authoritative list). PDP pins only the subset it uses; **new types are added to
this table by PR before first use**.

**In use now (001-platform-foundations):**

| Abbrev | Resource type | Provider namespace |
|---|---|---|
| `rg` | Resource group | `Microsoft.Resources/resourceGroups` |
| `st` | Storage account | `Microsoft.Storage/storageAccounts` |
| `id` | Managed identity (user-assigned) | `Microsoft.ManagedIdentity/userAssignedIdentities` |
| `lock` † | Management lock | `Microsoft.Authorization/locks` |

**Pinned for upcoming specs** (verified against the CAF page 2026-06-15; grow by PR as
each lands):

| Abbrev | Resource type | First needed by |
|---|---|---|
| `vnet` | Virtual network | 003 fabric |
| `snet` | Virtual network subnet | 003 fabric |
| `nsg` | Network security group | 003 fabric |
| `rt` | Route table | 003 fabric |
| `afw` | Azure Firewall | 003 fabric |
| `afwp` | Azure Firewall policy | 003 fabric |
| `pip` | Public IP address | 003 fabric |
| `ng` | NAT gateway | 003 fabric |
| `bas` | Azure Bastion | 003 fabric |
| `pep` | Private endpoint | 003+ |
| `ca` | Container app | 006 control plane |
| `cae` | Container apps environment | 006 control plane |
| `cr` | Container registry | 006 control plane |
| `psql` | PostgreSQL (Flexible Server) | 002 control-plane |
| `kv` | Key vault | 006+ |
| `log` ‡ | Log Analytics workspace | 006+ |
| `appi` | Application Insights | 006+ |

† **`lock`** has no CAF abbreviation (CAF does not list `Microsoft.Authorization/locks`).
It is a **PDP-specific** pin.
‡ CAF's **current** abbreviation for Log Analytics workspace is **`log`** — *not* the
retired `law`. PDP uses `log`. (data-model.md §2's draft listed `law`; corrected here as
the authoritative pin.)

### 1.1a Spoke resource names (spec 004)

Spokes follow the default `<type>-pdp-<region>-<name>` pattern; the spoke's `<name>`
segment is `spoke-<spoke_name>` for its resource group (so a Resource-Graph sweep on
`rg-pdp-*-spoke-*` enumerates spokes) and `<spoke_name>[-<subnet>]` for the network
resources it owns. All abbreviations used (`rg`, `vnet`, `snet`, `nsg`, `rt`) are already
pinned in §1.1 (carried over from 003 fabric); spec 004 adds **no** new abbreviations.

Worked examples (spoke `app1` in `westus3`):

| Resource | Type | Name |
|---|---|---|
| Resource group (target sub) | `rg` | `rg-pdp-westus3-spoke-app1` |
| Spoke VNet | `vnet` | `vnet-pdp-westus3-app1` |
| Workload subnet | `snet` | `snet-pdp-westus3-app1-workload` |
| Network security group | `nsg` | `nsg-pdp-westus3-app1-workload` |
| Route table (hub egress) | `rt` | `rt-pdp-westus3-app1` |
| Peering (spoke→hub, target sub) | — | `peer-app1-to-hub` |
| Peering (hub→spoke, platform sub) | — | `peer-hub-to-app1` |
| DNS zone link (per shared zone) | — | `vnetlink-pdp-westus3-app1-<zone>` |

> Virtual-network peerings and Private-DNS zone links are child resources whose names are
> scoped to their parent VNet/zone; they take the descriptive `peer-…` / `vnetlink-pdp-…`
> forms above (the fabric already uses `vnetlink-pdp-<region>-hub-<zone>`) rather than a
> `<type>-pdp-…` prefix.

### 1.2 Region-short table

For constrained names only (§1.3). Derived by dropping the direction vowels and
abbreviating the ordinal; pinned per region so it never varies:

| Azure region | Short form |
|---|---|
| `eastus2` | `eus2` |

Add a row by PR when a new region is onboarded (spec 009).

### 1.3 Constrained-name exception

Some resource types disallow hyphens, cap length, and/or require **global** uniqueness
(storage accounts; future ACR-like resources). For these the default pattern is
replaced by:

```
<type>pdp<region-short><name><4-char-suffix>
```

- No hyphens; all lowercase alphanumeric.
- **`<region-short>`** from the §1.2 table (`eastus2` → `eus2`).
- **`<4-char-suffix>`** — a stable random `[a-z0-9]{4}` string generated at create time
  (`random_string` in OpenTofu) and held in state, guaranteeing global uniqueness
  without hand-coordination.

Worked example (this feature's storage account):

```
stpdpeus2state8d2k
└┬┘└┬┘└─┬┘└──┬─┘└┬─┘
 │  │   │    │   └ random suffix (4 chars)
 │  │   │    └ name: "state"
 │  │   └ region-short: "eus2"
 │  └ platform discriminator: "pdp"
 └ type: "st" (storage account)
```

> The literal suffix for the deployed state account is filled in post-bootstrap (spec
> task T032). Until then docs use the placeholder `stpdpeus2state<suffix>`.

**Private DNS zones** (`Microsoft.Network/privateDnsZones`) are a related exception: the
resource **name is the DNS domain itself**, as Azure private-link resolution requires.
The control-plane Postgres (spec **002**) deploys the Flexible-Server zone
`pdp-controlplane.private.postgres.database.azure.com` (Flexible Server uses
`<name>.private.postgres.database.azure.com`, *not* the private-endpoint
`privatelink.*` form). They do **not** take the `<type>-pdp-…` pattern; CAF likewise
names them by domain. The RG that holds them still follows the standard convention and
tag schema.

---

## 2. Tag schema

Mandatory tags on every PDP-managed **resource group** (constitution Article III —
untagged = unmanaged = invisible to inventory). Inventory is Azure Resource Graph
filtered on `tags['pdp-managed'] == 'true'`; nothing else is queried for "what's
managed."

| Tag | Required on | Allowed values | Notes |
|---|---|---|---|
| `pdp-managed` | **Every** PDP-managed RG | `true` | Presence + `true` is the inventory filter; never `false` (unmanaged = untagged). |
| `pdp-deployed-by` | **Every** PDP-managed RG | `github-actions` \| `control-plane` \| `owner` | Enumerated actors. Run-level provenance lives in the provisioning-run audit trail (spec 6), not tags. |
| `pdp-fabric` | RGs belonging to a regional fabric | Azure region name (e.g., `eastus2`) | Omitted on non-fabric scopes. |
| `pdp-spoke` | RGs belonging to a spoke | Spoke name, `[a-z0-9-]{1,24}` | Omitted on non-spoke scopes. |
| `pdp-workload` | RGs holding a workload | Workload name, `[a-z0-9-]{1,24}` | Omitted elsewhere. |
| `pdp-env` | RGs holding a workload | Environment name (`dev`, `demo`…), `[a-z0-9-]{1,16}` | Omitted elsewhere; the grouping key for "what environments do I have?" |

### Rules

1. **Universal tags** (`pdp-managed`, `pdp-deployed-by`) appear on **every** managed RG,
   without exception.
2. **Scope tags** (`pdp-fabric`, `pdp-spoke`, `pdp-workload`, `pdp-env`) are present
   **exactly when the scope applies**. Inapplicable scope tags are **omitted**, never
   set to a sentinel like `n/a` or empty string.
3. Tags are set by **IaC only**. Hand-edited tags are drift and revert on next plan.

### Foundation resources

The foundations stack's RG carries the universal tags and **no** scope tags (platform
scope):

```hcl
pdp-managed     = "true"
pdp-deployed-by = "owner"          # → "github-actions" after the first CI apply (US3)
```

`pdp-deployed-by` starts at `owner` during the local bootstrap and flips to
`github-actions` with the first CI-driven apply — the value tracks whoever last applied
the stack.

### Spoke resources (spec 004)

A spoke's resource group carries the universal tags plus the `pdp-spoke` scope tag (its
spoke name), making it discoverable as a spoke via Resource Graph:

```hcl
pdp-managed     = "true"
pdp-deployed-by = "github-actions"   # spokes only ever vend via dispatched CI
pdp-spoke       = "app1"             # the spoke name
```

`pdp-env` is **omitted** on spoke RGs — it is a **workload** scope tag (set when a workload
lands in a spoke, spec 008), not a property of the spoke itself. A spoke is environment-
agnostic; multiple workloads of different environments can share one spoke.

---

## 3. Where this is enforced

- **IaC**: `infra/foundations/` (and every later stack) sets names and `local.tags` per
  this doc. See `infra/foundations/variables.tf` (`local.region`, `local.region_short`,
  `local.tags`).
- **Inventory**: Resource Graph queries filter on `pdp-managed == 'true'` (quickstart.md
  Scenario 2).
- **Review**: every IaC PR is checked against this doc until Azure Policy enforcement
  (spec 10) automates it.

To extend either convention (new abbreviation, new region-short, new tag), edit this
doc in the same PR that first needs it.
