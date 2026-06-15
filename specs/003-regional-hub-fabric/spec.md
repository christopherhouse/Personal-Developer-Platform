# Feature Specification: Regional Hub Fabric

**Feature Branch**: `003-regional-hub-fabric`

**Created**: 2026-06-15

**Status**: Draft

**Input**: User description: "regional-hub-fabric: As the platform owner, I want to stand up and tear down a complete regional network hub for a given Azure region (starting with East US 2) with a single platform verb, so that spokes have a regional landing zone to peer into and one controlled egress path — without me hand-building network plumbing or inventing address space."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Stand up a regional hub fabric (Priority: P1)

As the platform owner, I deploy a complete regional network hub for an already-registered
Azure region (East US 2 first) with a single platform verb. The result is one resource
group in the platform subscription containing a hub VNet (addressed from the region's
ledger-reserved hub block), a single controlled egress path, centralized private DNS, and
private management access — with no hand-built network plumbing and no invented address
space. Spokes (spec 004) now have a regional landing zone to peer into and an egress path
to point their default route at.

**Why this priority**: This is the feature's core value and the unblocking dependency for
spoke vending (spec 004). Without a hub, there is nothing for spokes to peer to and no
controlled egress — the entire hub-and-spoke topology starts here. It is the MVP: a
deployed, inventory-visible, spoke-ready hub.

**Independent Test**: Register a region in the IPAM ledger (spec 002 `register_region`),
invoke the fabric deploy verb for that region, and confirm the hub fabric comes up with its
VNet addressed inside the region's recorded hub carve-out, a single egress next-hop, private
DNS owned by the fabric, private-only management access, and a fabric RG discoverable in
Resource Graph inventory — all without any manual networking or address entry.

**Acceptance Scenarios**:

1. **Given** a region registered in the IPAM ledger with a recorded hub carve-out
   (`10.R.252.0/22`), **When** the owner invokes the fabric deploy verb for that region,
   **Then** a single resource group is created in the platform subscription containing a hub
   VNet whose address space is exactly the region's recorded hub carve-out, and no CIDR
   outside the ledger is used.
2. **Given** a deployed hub fabric, **When** inventory is queried via Resource Graph,
   **Then** the fabric RG is returned by the `pdp-managed == 'true'` filter and carries
   `pdp-fabric = <region>`.
3. **Given** a deployed hub fabric, **When** its egress configuration is inspected, **Then**
   exactly one controlled egress path exists for the region and a single default-route
   next-hop is exposed for spokes to later point at — no alternative egress exists.
4. **Given** a deployed hub fabric, **When** management access is inspected, **Then** the
   owner can reach the region's private network through a private, bastion-style path and no
   public management endpoint is present.
5. **Given** a region that is **not** registered in the IPAM ledger (no recorded hub
   carve-out), **When** the owner invokes the fabric deploy verb, **Then** deployment fails
   with a clear error and creates no resources and no address space.

---

### User Story 2 - Tear down a regional hub fabric cleanly (Priority: P2)

As the platform owner, I tear down a region's hub fabric with a single verb, after an
explicit confirmation, leaving no orphaned resources, no leaked address allocations, and no
dangling peerings. The region's reserved hub address block remains a standing reservation in
the ledger (it was never an allocation), so the region can be re-stood-up later without
address drift.

**Why this priority**: Destroyable-by-design is a constitutional acceptance criterion
(Article IV) and the cost-control backbone of a personal platform, but it depends on a hub
existing first (US1). It completes the stand-up/tear-down loop the owner asked for.

**Independent Test**: From a deployed fabric, invoke the destroy verb, provide the required
confirmation, and confirm the fabric RG and all its resources are gone from inventory, no
public IPs or peerings remain, and the region's ledger hub carve-out is still recorded
(unchanged) — so a subsequent re-deploy reuses the same block.

**Acceptance Scenarios**:

1. **Given** a deployed hub fabric and an explicit destroy confirmation, **When** the owner
   invokes the destroy verb, **Then** the fabric RG and every resource it contains are
   removed and the region no longer appears in inventory.
2. **Given** a deployed hub fabric, **When** a destroy is requested **without** explicit
   confirmation, **Then** no resources are deleted (destruction is gated on confirmation).
3. **Given** a destroyed hub fabric, **When** the IPAM ledger is queried for the region,
   **Then** the region's hub carve-out is still recorded as reserved (teardown leaks no
   allocation and releases no address space, because the carve-out is a standing reservation,
   not an allocation).
4. **Given** a hub fabric that still has live spoke peerings on its hub side, **When** a
   destroy is requested, **Then** the operation surfaces the dangling peerings and does not
   silently orphan them. *(This scenario only becomes reachable once spokes/peerings exist —
   spec 004; spokes own the peering, so cross-stack peering-aware teardown is enforced by spec
   004/006. Within this spec, no spokes exist, so the destroy plan has no peerings to surface.)*

---

### User Story 3 - Generalize to additional regions without rework (Priority: P3)

As the platform owner, I onboard a second Azure region by registering it (by index) in the
ledger and invoking the same fabric verb with a different region parameter — no module
changes, no new code, no hardcoded East US 2 assumptions.

**Why this priority**: Multi-region rollout ergonomics are a later spec (009), but the
fabric design must not bake in single-region assumptions today, or every future region pays
a rework tax. Validating region-parameterization now is cheap insurance.

**Independent Test**: Register a second test region at a different index, run the fabric
deploy (or plan) for it, and confirm it produces a correctly-named, correctly-tagged,
correctly-addressed hub for that region using only the region parameter — then tear it down.

**Acceptance Scenarios**:

1. **Given** two regions registered at distinct indices, **When** the fabric verb is invoked
   for each, **Then** each produces a hub addressed from its own region's hub carve-out, with
   region-conformant names and tags, with no code or module change between them.
2. **Given** the East US 2 fabric exists, **When** a second region's fabric is deployed,
   **Then** the two fabrics are independent deployable units (separate state, separate RGs)
   and neither's teardown affects the other.

---

### Edge Cases

- **Region not registered**: deploy verb invoked for a region with no ledger hub carve-out →
  fail clearly, create nothing (US1 scenario 5).
- **Fabric already exists for the region**: re-invoking deploy for an already-deployed region
  → idempotent (no duplicate RG/VNet) or a clear "already deployed" outcome; never a second
  conflicting hub.
- **Carve-out too small for required subnets**: the chosen egress tier plus management and
  DNS-resolution subnets must fit within the `/22` hub carve-out → if the subnet plan cannot
  fit, fail at plan time, not after partial apply.
- **Destroy with live peerings**: hub side still peered to one or more spokes → surface and
  refuse to silently orphan (US2 scenario 4). Reachable only once spokes exist (spec 004); the
  peering lives in spoke state, so peering-aware teardown is a spec-004/006 concern — within
  this spec there are no spokes, hence no peerings to orphan.
- **Concurrent deploy of the same region**: two simultaneous deploys for one region → state
  locking prevents a split-brain hub.
- **Only public surface is egress/management**: any public IP that exists is demanded by the
  egress or management requirement; no other public endpoint is present (Article IX).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST deploy a complete regional hub fabric for a specified, already-
  registered Azure region through a single platform verb, creating exactly one resource group
  per region in the platform subscription.
- **FR-002**: The hub VNet address space MUST be exactly the region's hub carve-out as
  recorded in the IPAM ledger (the reserved `/22`, `10.R.252.0/22`). The fabric MUST NOT
  invent CIDR, allocate outside the ledger, or use any address space the ledger has not
  recorded for that region.
- **FR-003**: The fabric MUST read the region's hub carve-out from the IPAM ledger as a
  standing reservation and MUST NOT create or release an `allocation` row for it (the
  carve-out is reserved at region-registration time and can never be released or double-
  issued). Consequently, fabric teardown MUST leak no address allocation.
- **FR-004**: If the target region is not registered in the IPAM ledger (no recorded hub
  carve-out), the deploy verb MUST fail with a clear error and create no resources. This
  registration check is enforced by the control plane (spec 006), which queries the live,
  private ledger and only then dispatches the fabric workflow with the registered region's
  `region_index`; the fabric IaC itself trusts that typed input (it has no data-plane path to
  the ledger). Until the spec-006 runtime exists, "region is registered" is a documented
  deploy-time prerequisite — see Assumptions and `plan.md` Constitution Check (Article VI).
- **FR-005**: The fabric MUST provide a single, controlled egress path for the entire region
  through which all spoke internet egress will flow, and MUST expose a single default-route
  next-hop that spokes will later point their default route at. Spokes MUST NOT have any way
  to egress except through this hub path (Article VII). The egress mechanism MUST be **Azure
  Firewall (Basic SKU)** with an attached firewall policy; the firewall's private IP is the
  default-route next-hop spokes point at. The smallest viable firewall tier (Basic) is used
  per Article IX.
- **FR-006**: The fabric MUST ensure the region can resolve Azure private-link FQDNs via
  **platform-shared** Azure Private DNS zones (a single global zone set in the platform
  subscription, created if absent), and MUST link the hub VNet to those zones. Because
  Private DNS zones are global, the fabric MUST NOT create per-region duplicate zones. The
  fabric owns the hub→zone VNet links (not the shared zones); linking spokes to the zones at
  vending time is out of scope (spec 004).
- **FR-007**: The fabric MUST provide secure, private management/administrative access into
  the region's network with **no public management endpoint on any managed VM or workload by
  default** (no direct RDP/SSH exposure). The mechanism MUST be **Azure Bastion (Basic SKU)**
  deployed in the hub, always-on. Basic is required because it is the smallest SKU that
  supports VNet peering, so the hub bastion can reach VMs in any peered spoke; the Developer
  SKU is explicitly rejected (no peering support, one VM at a time). The bastion MUST occupy a
  dedicated `AzureBastionSubnet` (≥ `/26`) within the hub carve-out. The Bastion broker's own
  public IP — required by the Basic SKU (only Standard/Premium support private-only
  deployment) — is the sanctioned private-access entry, not a public management endpoint on a
  workload.
- **FR-008**: The hub MUST be ready to accept cross-subscription spoke peering (the hub side
  of the peering relationship) as a normal case, since spokes can live in any writable
  subscription. (Creating the spoke side and completing peering is spec 004.)
- **FR-009**: All fabric infrastructure MUST be created, changed, and destroyed only through
  OpenTofu executed in dispatched GitHub Actions workflows authenticated via OIDC (no stored
  cloud secrets), following plan-on-PR / apply-on-merge. No raw `tofu`/`az` mutations and no
  local applies (Articles I, II; spec 001 rails).
- **FR-010**: Every fabric resource MUST follow the `<type>-pdp-<region>-<name>` naming
  convention, and the fabric resource group MUST carry the mandatory tag schema —
  `pdp-managed = true`, `pdp-deployed-by`, and the scope tag `pdp-fabric = <region>` — so the
  RG is discoverable via Resource Graph inventory (Article III).
- **FR-011**: The fabric MUST prefer Azure Verified Modules where viable; any hand-rolled
  `azurerm`/`azapi` module MUST carry a recorded justification in its README (Article V).
- **FR-012**: The fabric MUST be private and cheap by default — no public endpoints except
  those an egress or management requirement explicitly demands, and the smallest viable SKUs
  (Article IX).
- **FR-013**: The fabric MUST have its own OpenTofu state as a single deployable unit, keyed
  `fabrics/<region>` per the spec-001 state-key convention.
- **FR-014**: The fabric MUST tear down cleanly via a single verb — no orphaned resources, no
  leaked address allocations, and no dangling peerings (Article IV). Teardown MUST remove the
  hub→shared-zone VNet links but MUST NOT delete the platform-shared Private DNS zones (they
  are not fabric-scoped and may serve other regions), mirroring how the ledger hub carve-out
  survives teardown.
- **FR-015**: A fabric destroy MUST require explicit human confirmation and the running
  fabric MUST carry deletion protection equivalent to the control-plane stack's gating
  posture, removed only as part of a confirmed destroy (Article VIII).
- **FR-016**: The fabric design MUST be region-parameterized — region is an input, not a
  hardcoded value — so additional registered regions deploy with no module or code change
  (East US 2 is the first region).
- **FR-017**: Clean teardown MUST be demonstrated as an acceptance criterion, as measured by
  SC-006 (fabric absent from inventory, no residual public IPs/peerings) and SC-007 (ledger
  hub carve-out still recorded unchanged, so a re-deploy reuses the identical block).

### Key Entities

- **Regional Hub Fabric**: the per-region deployable unit — one resource group in the
  platform subscription holding the hub and its shared services for exactly one region.
  Identified by region; carries `pdp-fabric = <region>`; has its own OpenTofu state
  (`fabrics/<region>`).
- **Hub VNet**: the central VNet of the fabric, addressed from the region's recorded hub
  carve-out (`10.R.252.0/22`). Internally subnetted for egress (firewall + its management NIC)
  and management access (bastion); private-link DNS is provided by VNet-linked Private DNS
  zones, not a subnet. The peering target and egress source for the region's spokes.
- **Egress path**: an **Azure Firewall (Basic SKU)** with firewall policy — the single
  controlled mechanism through which all regional spoke egress flows; its private IP is the
  one default-route next-hop spokes point at. Requires a dedicated `AzureFirewallSubnet`, a
  mandatory `AzureFirewallManagementSubnet` (required by Firewall Basic's management NIC —
  Basic does not support forced tunneling), and **two Standard public IPs** (data +
  management).
- **Private DNS (platform-shared, hub-linked)**: a single global set of Azure Private DNS
  zones in the platform subscription (created if absent) for private-link resolution; the
  fabric owns the **hub VNet links** to them, not the zones. Spokes link to the same shared
  zones at vending (spec 004).
- **Management access**: an **Azure Bastion (Basic SKU)** host in the hub providing private,
  browser-based RDP/SSH into the region's network — including VMs in peered spokes — with no
  public management endpoint by default. Occupies a dedicated `AzureBastionSubnet` (≥ `/26`).
- **Hub-side peering readiness**: the hub-side configuration that lets a spoke in any writable
  subscription peer into the hub as a normal cross-subscription case.
- **Region hub carve-out (consumed, not owned)**: the reserved `/22` recorded in the IPAM
  ledger's `region_pool` for the region; the fabric reads it but never allocates or releases
  it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A registered region's hub fabric is deployed end-to-end with a single verb
  invocation, with zero manual network plumbing steps and zero hand-entered address space.
- **SC-002**: 100% of the hub's address space traces to the region's ledger-recorded hub
  carve-out; zero invented or out-of-ledger CIDR is used (verifiable from the deployed VNet
  address space vs. the ledger record).
- **SC-003**: The fabric resource group is returned by an inventory query filtered on
  `pdp-managed == 'true'` and carries `pdp-fabric = <region>`, in a single Resource Graph
  query.
- **SC-004**: The region has exactly one controlled egress path and exactly one default-route
  next-hop for spokes — verifiable as a single egress target with no alternative egress.
- **SC-005**: No managed VM or workload exposes a public management endpoint (direct RDP/SSH)
  by default — all administrative reachability is through the private Azure Bastion broker.
  Verifiable by inspection: the only public IPs are the firewall data + management IPs and the
  Bastion broker IP; no managed resource carries a public RDP/SSH endpoint.
- **SC-006**: A single destroy verb (after confirmation) removes 100% of the fabric's
  resources — the fabric RG and all contents are absent from inventory, with no residual
  public IPs or peerings.
- **SC-007**: After teardown, the region's ledger hub carve-out is still recorded and
  unchanged, so a re-deploy reuses the identical block (no address drift across a
  destroy/recreate cycle).
- **SC-008**: A second registered region's fabric is produced by changing only the region
  parameter — no module or code change — demonstrated by a successful deploy or plan for a
  second region.
- **SC-009**: 100% of fabric applies and destroys execute via dispatched GitHub Actions
  workflows (none run locally), evidenced by the workflow run records.

## Clarifications

### Session 2026-06-15

- Q: Egress tier — which mechanism provides the region's single controlled egress path? → A: Azure Firewall, **Basic SKU** (+ firewall policy). Basic is the cost-reduced tier suited to personal scale; it mandates both `AzureFirewallSubnet` and `AzureFirewallManagementSubnet` (each `/26`) inside the `/22`, plus two Standard public IPs (data + management). (The management subnet/NIC is required for Basic regardless — Basic does not support forced tunneling.)
- Q: Management-access mechanism & cost posture? → A: Azure Bastion **Basic SKU**, always-on, in the hub. Basic is the smallest SKU that supports VNet peering, so the hub bastion can reach VMs in peered spokes (Developer SKU was rejected — it doesn't support peering and connects to one VM at a time, so it couldn't manage spoke workloads). Needs `AzureBastionSubnet` (≥ `/26`) in the `/22`.
- Q: Private DNS ownership model — does the fabric own per-region zones or link to shared zones? → A: **Platform-shared** global Private DNS zones (one set in the platform subscription, fabric create-if-absent); the fabric owns and destroys only the **hub VNet links** to those zones, not the zones themselves. Private DNS zones are global resources, so per-region duplication is avoided; shared zones survive fabric teardown (like the ledger hub carve-out).

The decision-context tables below are retained for traceability. Resolved options are marked
**[CHOSEN]**.

### Q1 — Egress tier *(resolved)*

**What we need to know**: Which mechanism provides the region's single controlled egress
path? Architecture (`docs/architecture.md` §Open questions) flags this as unresolved.

| Option | Answer | Implications |
|--------|--------|--------------|
| A **[CHOSEN]** | Azure Firewall — **Basic SKU** (+ policy) | Strongest L3–L7 control and the cleanest "inspect all egress" story. **Basic** is the cost-reduced firewall tier for small/personal scale (~one-third of Standard's standing cost). Requires **both** `AzureFirewallSubnet` and a mandatory `AzureFirewallManagementSubnet` (each `/26`) inside the `/22` — the management NIC is required for Basic regardless (Basic does **not** support forced tunneling) — plus **two Standard public IPs** (data + management). |
| B | NAT Gateway + NSGs | Cheapest always-on egress with deterministic SNAT; NSGs give L3/L4 filtering only (no L7/FQDN inspection). Smallest footprint. |
| C | NVA (third-party appliance) | Most flexible/portable but most operational burden and VM cost; least "AVM-first." Generally over-engineered at personal scale. |

### Q2 — Management-access mechanism & cost posture *(resolved)*

**What we need to know**: How is private, no-public-endpoint management access provided, and
is it always-on or on-demand — given "private and cheap by default"?

| Option | Answer | Implications |
|--------|--------|--------------|
| A **[CHOSEN]** | Azure Bastion — **Basic SKU**, always-on | Smallest SKU that **supports VNet peering**, so the hub bastion reaches VMs in any peered spoke (the management model this fabric needs). Dedicated, browser-based private RDP/SSH; recurring hourly cost while the fabric exists. Needs `AzureBastionSubnet` (≥ `/26`) inside the `/22`. |
| ~~Developer SKU~~ | Rejected | Free, but **does not support VNet peering** and connects to one VM at a time — it could only reach the (workload-less) hub VNet, not peered spokes, so it cannot satisfy region management. |
| B | On-demand / just-in-time bastion (deploy-when-needed) | Near-zero idle cost; access requires a spin-up step (and a verb to do so). Best literal fit for "cheap by default." |
| C | Point-to-site VPN into the hub | Reusable across regions, no per-host bastion; gateway cost and client setup; `GatewaySubnet` reservation. |

## Assumptions

- **Region index for East US 2**: East US 2 is the first geographic region and is expected to
  be registered at `region_index 1` → supernet `10.1.0.0/16`, hub carve-out `10.1.252.0/22`
  (index 0 is the platform-shared supernet per IPAM data-model §1). The exact index is an
  operational choice made at `register_region` time; the fabric reads whatever carve-out the
  ledger recorded for the region and assumes nothing about a specific index.
- **Carve-out sizing**: the `/22` hub carve-out is assumed large enough to hold the required
  hub subnets — `AzureFirewallSubnet` (`/26`) and the mandatory `AzureFirewallManagementSubnet`
  (`/26`) for Azure Firewall Basic, and `AzureBastionSubnet` (`/26`) for Azure Bastion Basic.
  Private-link DNS uses VNet-linked zones (no subnet). These three `/26`s leave ample headroom
  for future hub services (e.g., a DNS Private Resolver or GatewaySubnet) in a `/22` (which
  holds 16 `/26`s). The concrete subnet plan is finalized in `/speckit-plan` and must fit
  within the `/22` (else fail at plan time per the edge case).
- **Private DNS zone set**: the platform-shared zones cover the Azure private-link services
  PDP will actually use (e.g., Postgres Flexible Server, Blob/Storage, Key Vault, Container
  Apps); the exact zone list is finalized in `/speckit-plan`. The control-plane Postgres zone
  already created by spec 002 (`pdp-controlplane.private.postgres.database.azure.com`) is an
  existing shared zone; the fabric reuses/links rather than duplicating it. Spoke-side linking
  is spec 004.
- **Gating posture parity**: "same gating posture as the control-plane stack" means the
  running fabric carries deletion protection (a management lock, as the foundations/control-
  plane stacks do) plus explicit human confirmation before destroy.
- **Single owner**: management access is for the platform owner only (single-owner platform).
- **Carve-out is consumed, not allocated**: per IPAM data-model §2 the hub carve-out lives in
  `region_pool.hub_carveout`, not in `allocation`; the fabric therefore performs no ledger
  allocate/release and leaves no allocation to leak on teardown.

## Dependencies

- **Spec 001 — platform-foundations** (complete, merged): OpenTofu state backend, naming/tag
  conventions, CI plan-on-PR / apply-on-merge rails, branch protection. The fabric stack
  builds directly on these.
- **Spec 002 — ipam-ledger** (active): the target region MUST be registered via
  `register_region` with a recorded hub carve-out before the fabric can deploy. The fabric
  reads the region's hub carve-out from the ledger (`query`); it never allocates.

## Out of Scope

- Spoke creation, cross-subscription peering execution (spoke side), route tables, NSGs, and
  spoke-side DNS links — **spec 004 (spoke-vending)**.
- Multi-region hub-to-hub connectivity and second-region rollout ergonomics — **spec 009
  (multi-region)**. (This spec only ensures the design *generalizes* to more regions.)
- Policy enforcement, diagnostics/log routing, and cost guardrails — **spec 010
  (observability-guardrails)**.
- The verb / `pdp` CLI / `pdp-mcp` front-ends that invoke fabric deploy/destroy — **specs 006
  (action-layer) and 007 (mcp-chatops)**. This spec defines the fabric behavior those verbs
  drive.
