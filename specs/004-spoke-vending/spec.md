# Feature Specification: Spoke Vending

**Feature Branch**: `004-spoke-vending`

**Created**: 2026-06-16

**Status**: Draft

**Input**: User description: "Spoke vending: atomically create a spoke — a workload-ready VNet peered to its region's hub — into any writable target subscription, and tear it down cleanly … all subnets (platform, hubs, spokes) gotta have default NSG."

## Clarifications

### Session 2026-06-16

- **Q: How does a spoke obtain its CIDR before the spec-006 allocator runtime exists?** →
  **A (superseded by Plan Gate G1, 2026-06-16):** initially "pull a minimal allocation capability
  forward" (vend writes/releases the ledger row). The `/speckit-plan` Constitution Check confirmed
  the feasibility blocker flagged here — the ledger is private/Entra-only and a GitHub-hosted CI
  runner cannot reach it, and allocation is a control-plane function. **Revised decision (Gate G1,
  Option A): no ledger write in this spec; the spoke CIDR is a typed input fitted to the region
  `/16` (FR-016), and live by-size allocation + ledger write/release are deferred to the spec-006
  control plane.**
- **Q: Spoke subnet layout and block size?** → **A: Fully configurable per vend.** The spoke VNet
  size, the number of subnets, each subnet's size, and per-subnet delegations are all parameterized
  inputs with sensible defaults.
- **Q: Default NSG posture for spoke subnets?** → **A: NSG attached with Azure default rules
  only.** Every spoke subnet gets an NSG (satisfying the "every subnet has an NSG" rule); this spec
  adds no custom rules — specific allow/deny rules come from archetypes/later specs.
- **Q: How is a spoke's CIDR requested from the ledger?** → **A: By size** *(superseded by Plan
  Gate G1, 2026-06-16)*. Initially chosen as live by-size allocation; the `/speckit-plan`
  Constitution Check found that live allocation is a control-plane function against the private
  ledger that OpenTofu cannot reach. **Revised decision: the spoke CIDR is a typed input fitted to
  the region `/16` (FR-016); live by-size allocation is deferred to the spec-006 control plane.**
- **Q: What teardown protection do spokes carry?** → **A: Freely destroyable, confirm-gated
  only.** Spokes carry **no** `prevent_destroy` and **no** management lock (unlike the hub and
  control-plane); they are the disposable, high-churn unit. Teardown is gated by explicit
  confirmation alone (FR-014).
- **Q: How is the cross-subscription peering created?** → **A: One vend creates both sides
  atomically.** The deploy identity therefore must hold rights in **both** the target subscription
  (spoke side) and the platform subscription (hub side — Network Contributor on the hub resource
  group).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Vend a spoke into a target subscription (Priority: P1) 🎯 MVP

The owner asks the platform to create a named spoke in a chosen registered region and a chosen
writable subscription. In one operation the platform takes a spoke address block from the region's
ledger space, creates the spoke network, peers it to that region's hub, routes all its egress
through the hub firewall, secures every subnet, and links it to the shared private DNS — producing
an isolated, workload-ready network environment with no hand-plumbing.

**Why this priority**: This is the critical-path payoff of specs 1–4 ("deploy me a spoke"). It
is the first capability that produces a place for workloads to live, and every later spec
(archetypes, workloads) depends on it. On its own it delivers a usable, demoable outcome.

**Independent Test**: Vend a single spoke into one subscription/region and confirm it is peered,
egresses through the hub firewall, resolves private DNS, and is discoverable in inventory —
without touching any other story.

**Acceptance Scenarios**:

1. **Given** a registered region with a deployed fabric and a writable target subscription,
   **When** the owner vends spoke `app1` into that subscription/region, **Then** a spoke network
   exists with address space drawn from the IPAM ledger, peered to the region's hub (both sides),
   with a default route to the hub firewall and DNS links to the shared zones — created by a
   single operation.
2. **Given** the spoke from scenario 1, **When** its egress is inspected, **Then** the only path
   off the spoke is via the hub firewall's private IP and there is no alternative default route or
   independent egress.
3. **Given** the spoke from scenario 1, **When** inventory is queried, **Then** the spoke's
   resource group is discoverable by its `pdp-*` tags and identifies its region and subscription.

---

### User Story 2 - Many independent spokes per subscription (Priority: P2)

The owner vends several spokes (e.g., `app1`, `app2`, `staging`) into the **same** subscription.
Each is a fully independent network environment with its own address allocation, network,
peering, routing, security, DNS, and lifecycle; they never collide and never depend on one
another.

**Why this priority**: A single subscription is a common home for multiple workloads/environments;
without this the platform forces one-spoke-per-subscription, which is impractical. Builds directly
on US1.

**Independent Test**: Vend two differently-named spokes into one subscription and confirm both
exist with non-overlapping address space and independent peerings, then operate on one without
affecting the other.

**Acceptance Scenarios**:

1. **Given** spoke `app1` already vended in a subscription, **When** the owner vends `app2` into
   the same subscription, **Then** both spokes exist with non-overlapping address space and each
   peers to the hub independently.
2. **Given** two spokes in one subscription, **When** the owner re-vends `app1` with the same
   name, **Then** `app1` converges with no duplicate created and `app2` is untouched.
3. **Given** two spokes in one subscription, **When** the owner attempts to vend a third spoke
   reusing the name `app1`, **Then** the operation is rejected (names are unique within a
   subscription) rather than mutating the existing `app1`.

---

### User Story 3 - Clean, confirm-gated teardown (Priority: P2)

The owner tears down a single spoke. All of that spoke's resources — **including both peering
sides** — are removed and its address block becomes reusable, while sibling spokes, the regional
hub, and the shared DNS zones are left completely untouched. (Releasing the *ledger allocation row*
is the spec-006 control plane's job; this stack writes and holds no such row — Plan Gate G1.)

**Why this priority**: Destroyable-by-design is constitutional (Article IV); a spoke that can be
created but not cleanly removed — or that leaves a dangling hub-side peering — is a liability.
Ledger-allocation release is handled by spec 006 alongside allocation.

**Independent Test**: Tear down one spoke among several and confirm zero residual resources (no
dangling hub-side peering), the freed block is reusable, and the other spokes plus hub/zones still
function.

**Acceptance Scenarios**:

1. **Given** a subscription with spokes `app1` and `app2`, **When** the owner tears down `app1`
   (with explicit confirmation), **Then** all of `app1`'s resources are gone — including the
   hub-side peering — its address block is free for reuse, and `app2`, the hub, and the shared
   zones are unchanged.
2. **Given** `app1` has been torn down, **When** a new spoke is later vended in the same region,
   **Then** the released address space is available for reuse.
3. **Given** a destroy request, **When** it is initiated, **Then** it does not proceed without an
   explicit confirmation step (Article VIII).

---

### User Story 4 - Generalizes across subscriptions and regions (Priority: P3)

The same vending capability targets any registered region and any writable subscription by
changing only parameters — no per-subscription or per-region code.

**Why this priority**: Multi-subscription, multi-region reach is the platform's reason to exist,
but it is an extension of the core vend (US1) rather than a precondition for value.

**Independent Test**: Vend a spoke into a second subscription and/or a second region by changing
only inputs, and confirm correct names, address space, hub, and DNS with no source edits.

**Acceptance Scenarios**:

1. **Given** two registered regions each with a deployed fabric, **When** the owner vends spokes
   into each by changing only region/subscription/name inputs, **Then** each spoke draws from the
   correct region's address space and peers to the correct regional hub.

---

### Edge Cases

- **Re-vend (idempotency)**: re-vending the same `(subscription, spoke-name)` converges to the
  existing spoke and creates no duplicate; a new name in the same subscription creates an
  additional spoke.
- **No fabric for the region**: vending into a region with no deployed fabric (or unregistered
  region) fails fast with a clear error — there is no hub to peer to.
- **Address exhaustion**: when the region's `/16` cannot satisfy a new spoke block, vending fails
  cleanly with no partial spoke and no orphaned allocation.
- **Non-writable target subscription**: vending into a subscription the platform identity cannot
  write to fails before any resource is created.
- **Teardown while in use**: tearing down a spoke that still hosts workloads — workload lifecycle
  is spec 008; this spec's teardown removes the spoke network only and assumes the spoke is empty
  (behavior on a non-empty spoke is called out in Assumptions).
- **Partial-failure recovery**: a vend interrupted mid-way can be safely re-run to completion
  (convergent), never leaving a half-allocated or unpeered spoke.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The platform MUST vend a spoke as a single logical operation that produces all of:
  the spoke network (from its address block), hub peering, egress routing, subnet security, and DNS
  links — with no manual networking steps.
- **FR-002**: Every spoke's address space MUST come from the IPAM ledger's region `/16` (the sole
  authority for ranges, Article VI), supplied as a typed block fitting that `/16` and never
  invented or hardcoded outside it. (The block is *recorded* as a ledger allocation by the spec-006
  control plane — Gate G1; this stack does not write the row.)
- **FR-003**: The spoke MUST be peered to its region's hub on **both sides, created within the
  single vend operation**, consuming the fabric's published interface (`hub_vnet_id`,
  `hub_resource_group_name`); the spoke MUST NOT peer to any other spoke. Because the hub side lives
  in the platform subscription, this requires the cross-subscription identity described in FR-010.
- **FR-004**: The spoke MUST route all egress (`0.0.0.0/0`) to the hub firewall's private IP
  (`firewall_private_ip`) and MUST NOT define any alternative egress or default route (Article
  VII — single regional egress).
- **FR-005**: **Every spoke subnet MUST be associated with a network security group** (no subnet
  is left without an NSG); the default security posture of that NSG is per the Clarifications.
- **FR-006**: The spoke VNet MUST be linked to the platform-shared private DNS zones
  (`shared_dns_zone_ids`) so private endpoints resolve; the spoke MUST NOT create its own copies of
  those zones.
- **FR-007**: A single target subscription MUST support multiple independent spokes; a spoke is
  uniquely identified by the `(subscription, spoke-name)` pair, spoke names MUST be unique within a
  subscription, and each spoke MUST be an independently deployable/destroyable unit
  (`spokes/<sub-id>/<spoke-name>`).
- **FR-008**: Spoke blocks MUST be non-overlapping with every other allocation in the region's
  `/16`. Once spec 006's allocator exists this is **ledger-enforced** (GiST). **Until then** the
  typed `spoke_cidr` (FR-016) is operator-trusted and this stack validates only containment within
  the region `/16` and avoidance of the hub carve-out — **cross-spoke non-overlap is the operator's
  responsibility during the staged period** (call it out at vend time; see quickstart).
- **FR-009**: Vending MUST be idempotent: re-vending the same `(subscription, spoke-name)`
  converges with no duplicate; a new name creates an additional spoke; reusing an existing name is
  rejected, not silently mutated.
- **FR-010**: The target subscription MUST be supplied at request time and discovered/validated at
  runtime as writable — never hardcoded; it may differ from the platform subscription, and the
  operation MUST authenticate into the target subscription via OIDC with no stored cloud secrets
  (Articles I/II). Because the vend also creates the **hub-side** peering (FR-003), the operation's
  identity MUST additionally hold the rights to create that peering on the hub in the **platform**
  subscription (Network Contributor on the hub resource group) — a single vend spans both
  subscriptions.
- **FR-011**: Vending MUST require that the target region is registered and has a deployed fabric;
  otherwise it MUST fail before creating resources.
- **FR-012**: Every spoke resource group MUST carry the `pdp-*` tag schema and be discoverable via
  Resource Graph (Article III), identifying at least its region and owning subscription.
- **FR-013**: Tearing down a spoke MUST remove all of that spoke's resources — **including both
  peering sides** (the hub-side peering removed via the platform provider, so none dangles) — and
  leave sibling spokes, the hub, and shared zones untouched; the spoke's address block MUST become
  reusable by a future vend. Releasing the *ledger allocation row* is the spec-006 control plane's
  responsibility (this stack writes/holds no such row — Plan Gate G1).
- **FR-014**: Spoke teardown MUST require explicit confirmation before proceeding (Article VIII)
  and MUST be achievable cleanly (Article IV). Spokes carry **no** `prevent_destroy` and **no**
  management lock (unlike the hub/control-plane) — they are the disposable unit; the confirmation
  step is the sole guard.
- **FR-015**: All infrastructure mutations MUST go through the platform's OpenTofu execution path
  in dispatched CI (no laptop applies, no runtime IaC generation) (Articles I/II).
- **FR-016**: The spoke's CIDR block MUST be a **typed/parameterized input** to the vend, drawn
  from the region's `/16` and fitting within it (validated deterministically at plan time). This
  spec does **not** perform a live ledger allocation — that is a control-plane function against the
  private IPAM ledger, which the execution plane (OpenTofu in CI) cannot reach (Plan Gate G1). The
  **live by-size allocation and the ledger write/release** are deferred to the spec-006 control
  plane; until then the typed block is trusted and recorded out-of-band, mirroring how spec 003
  trusted a typed `region_index`. Teardown still removes the spoke cleanly (the allocation row,
  once 006 exists, is released by the control plane, not by this stack).
- **FR-017**: The spoke network shape MUST be **configurable per vend**: the number of subnets,
  each subnet's size, and per-subnet delegations MUST all be parameterizable inputs; the
  **default is a single workload subnet filling the spoke block**. Subnets are carved within the
  typed `spoke_cidr` (FR-016) and MUST fit within it.
- **FR-018**: Every spoke subnet MUST have an NSG attached (FR-005), but this spec applies **no
  custom rules** — the NSG carries Azure's default rule set only (intra-VNet allowed,
  outbound-to-internet allowed *but routed through the hub by the egress UDR of FR-004*,
  inbound-from-internet denied). The NSG is a mandatory security anchor on every subnet; specific
  allow/deny rules are added by workload archetypes / later specs.

#### Cross-cutting note — "all subnets must have an NSG" (platform + hubs)

The owner's intent that **platform, hub, and spoke subnets all carry a default NSG** is fully
in-scope and required **for spoke subnets** here (FR-005). Extending it to **platform** (control-
plane) and **hub** (fabric) subnets is a change to specs 002/003 and is **out of scope for this
spec** — but it carries a hard Azure constraint that must be honored wherever it lands:
`AzureFirewallSubnet` and `AzureFirewallManagementSubnet` **cannot** have an NSG, while
`AzureBastionSubnet` **can and should** (with the Azure-mandated rule set). This is flagged for a
cross-cutting follow-up (e.g., a constitution amendment or the observability/guardrails spec), not
silently assumed solvable as a literal "every subnet."

### Key Entities

- **Spoke**: an isolated, workload-ready network environment in a target subscription, peered to
  one regional hub. Identified by `(subscription, spoke-name)`; one deployable unit per spoke.
- **Spoke CIDR allocation**: the spoke's block within the region's `/16` — supplied as a typed
  input here (FR-016) and recorded as a ledger row by the spec-006 control plane (this stack writes
  no row). The ledger's region `/16` remains the authority for the address space.
- **Hub peering (pair)**: the spoke-side and hub-side peerings connecting the spoke to its region's
  hub; one pair per spoke.
- **Egress route**: the spoke's `0.0.0.0/0` route to the hub firewall private IP — the single
  egress path.
- **Spoke subnet NSG**: the security group attached to every spoke subnet.
- **Spoke DNS link**: the link from the spoke VNet to each shared private DNS zone.
- **Target subscription**: any subscription the platform identity can write to; the spoke's home,
  possibly different from the platform subscription.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A single vend operation produces a working spoke (peered, egressing via the hub
  firewall, resolving shared private DNS) in a named subscription and region with zero manual
  networking steps.
- **SC-002**: 100% of a spoke's address space falls within the region's `/16` and outside the hub
  carve-out; none is invented outside the region block. (It is recorded as a ledger allocation row
  once spec 006's allocator exists — see FR-016/Gate G1.)
- **SC-003**: Multiple spokes vended into the same subscription coexist with non-overlapping
  address space and independent lifecycles (operating on one never affects another).
- **SC-004**: Each spoke has exactly one egress path — the regional hub firewall — with no
  alternative route or independent egress.
- **SC-005**: 100% of spoke subnets have an associated NSG (no subnet without one).
- **SC-006**: Tearing down one spoke leaves zero residual resources for that spoke (including **no
  dangling hub-side peering**), frees its address block for reuse, and disturbs no sibling spoke,
  the hub, or the shared zones.
- **SC-007**: Every spoke is discoverable in inventory by its `pdp-*` tags, identifying its region
  and owning subscription.
- **SC-008**: The same vending definition targets any registered region and any writable
  subscription by changing only request parameters — no per-region or per-subscription source
  changes.
- **SC-009**: Every create and destroy occurs via dispatched CI with OIDC and no stored cloud
  secrets, including into target subscriptions distinct from the platform subscription.

## Assumptions

- The **IPAM ledger** (spec 002) is the sole authority for address space (the region `/16`); this
  spec consumes a typed block from that space and **does not write or release a ledger row** —
  recording/releasing the allocation is the spec-006 control plane's job (Plan Gate G1 / FR-016).
  This is why the spec is pure execution-plane IaC and avoids the private-ledger reachability
  problem entirely.
- The spoke network **shape is parameterized** (VNet size, subnet count/sizes, delegations); this
  spec ships sensible defaults but no fixed layout (FR-017).
- The target region already has a **deployed fabric** (spec 003) exposing the documented outputs;
  this spec consumes them and does not modify the hub.
- The **platform identity can write** to the named target subscription; subscription discovery and
  the verb/CLI/MCP that *invoke* vending are spec 006 (out of scope here).
- **Workload lifecycle** is spec 008; this spec's teardown removes the spoke network and assumes
  the spoke is otherwise empty. Teardown behavior on a spoke that still contains workloads is
  treated as the operator's responsibility (workload removal precedes spoke teardown) until spec
  008 defines orchestration.
- The literal "**every** subnet everywhere has an NSG" intent is honored for spoke subnets here;
  platform/hub retrofits are a separate cross-cutting effort with the `AzureFirewallSubnet` /
  `AzureFirewallManagementSubnet` Azure exceptions noted above.
- Each spoke is a **separate deployable unit/state** (`spokes/<sub-id>/<spoke-name>`), consistent
  with the platform's one-state-per-unit rule.

### Out of scope (non-goals)

- The IPAM allocator **runtime** and the verb/CLI/MCP that invoke vending (spec 006).
- **Workload archetypes** deployed into spokes (spec 008).
- **Hub-to-hub** connectivity (spec 009).
- **Diagnostics/log routing** and policy guardrails (spec 010).
