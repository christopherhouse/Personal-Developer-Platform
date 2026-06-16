# Research: Spoke Vending

Phase 0 decisions for spec 004. Each is **Decision / Rationale / Alternatives**. Azure/AVM
surfaces are smoke-validated under OpenTofu 1.11.x at implement time and recorded in the stack
README (Article V), as spec 003 did.

## §1 Address allocation — typed CIDR now, live allocation deferred to spec 006 (Gate G1)

**Decision**: The spoke's CIDR is a **typed input** (`spoke_cidr`) that MUST fall within the
region's `/16` (validated deterministically at plan time — `cidrsubnet`/`cidrhost` range checks).
This spec performs **no** live ledger allocation and writes **no** allocation row. The live
**by-size** allocation and the ledger write/release are the **spec-006 control plane's** job.

**Rationale**: The IPAM ledger is private/Entra-only Postgres injected in the control-plane VNet;
OpenTofu runs on GitHub-hosted runners that cannot reach it, and the constitution explicitly places
allocation in the control plane. Trusting a typed block now (recorded out-of-band until 006) is the
same staging spec 002 (seeded reservations) and spec 003 (typed `region_index`) used — a documented
Article-VI refinement. The region `/16` itself still comes from the ledger's scheme.

**Alternatives considered**: live by-size allocation from CI (impossible — private DB); a minimal
in-VNet allocator pulled into this spec (first runtime + control-plane scope creep — rejected as
Gate-G1 Option B); sequencing spec 006 first (delays the critical path — Option C). Owner chose
Option A.

## §2 Cross-subscription peering — both sides in one vend, dual-subscription identity

**Decision**: The vend creates **both** peering directions: spoke→hub (in the target subscription)
and hub→spoke (in the platform subscription, on the hub VNet). The OpenTofu run uses **two
provider configurations** — a default `azurerm` provider for the target subscription and an aliased
provider for the platform subscription — and the deploy identity holds rights in both (Network
Contributor on the hub RG in the platform sub; Contributor in the target sub). The hub-side peering
sets `allow_forwarded_traffic = true` so spoke egress can transit the hub firewall.

**Rationale**: FR-003/FR-010 require a single atomic vend that yields a *connected* spoke; the
hub-side peering must be created somewhere, and doing it in the same run (aliased provider) keeps
the operation atomic. `allow_forwarded_traffic` on the hub side is required for the firewall to
forward spoke traffic (Article VII egress).

**Alternatives considered**: two-step (spoke now, hub later) — breaks atomicity; a platform-side
helper that completes the hub peering — more orchestration, deferred to spec 006's verb layer.

## §3 Consuming the fabric — `terraform_remote_state`

**Decision**: Read the fabric outputs (`hub_vnet_id`, `hub_resource_group_name`,
`firewall_private_ip`, `shared_dns_zone_ids`) via `terraform_remote_state` against the
`fabrics/<region>` state (the spec-003 §I2 contract), not by hand-entered values or ad-hoc data
sources.

**Rationale**: spec 003 publishes exactly these as stable outputs for this consumer; remote state
is the loosest stable coupling and survives hub re-allocation (consumers read live).

**Alternatives considered**: `data` lookups by name (more brittle, must know every resource name);
hardcoding (forbidden — values change).

## §4 Spoke network shape — configurable via a subnets map

**Decision**: The spoke VNet size, subnet count, each subnet's prefix, and per-subnet delegations
are driven by typed inputs (a `subnets` map), with sensible defaults (a single workload subnet
filling the spoke block). Built with `avm-res-network-virtualnetwork` (same module family as the
hub), whose `subnets` map carries `address_prefixes`, optional `delegations`, the NSG association,
and the route-table association per subnet.

**Rationale**: FR-017 requires full configurability; the AVM VNet module already models subnets
with NSG + route-table associations, so one module covers VNet+subnets+NSG-attach+route-attach.

**Alternatives considered**: fixed layout (rejected — FR-017); hand-rolled `azurerm_subnet`
(loses AVM benefits).

## §5 NSG model — one NSG per subnet, Azure default rules only

**Decision**: Every spoke subnet gets its own NSG (or a shared spoke NSG associated to all subnets)
with **no custom rules** — Azure's default rule set only (FR-005/FR-018). The egress UDR (not the
NSG) is what forces traffic through the hub.

**Rationale**: Satisfies "every subnet has an NSG" cheaply; archetypes (spec 008) add specific
rules later. Default rules already deny inbound-from-internet and allow intra-VNet + (UDR-routed)
outbound.

**Alternatives considered**: deny-all baseline (more secure but blocks basic workloads until rules
are added — owner chose defaults); no NSG (violates the requirement).

## §6 Egress route — route table → firewall private IP

**Decision**: A route table with `0.0.0.0/0` → `VirtualAppliance` next-hop = `firewall_private_ip`
(from fabric remote state), associated to every spoke workload subnet. No other default route; no
NAT/independent egress (Article VII).

**Rationale**: This is the structural backing of SC-004; the fabric exposes the next-hop precisely
for this.

## §7 Teardown — no lock, confirm-gated destroy

**Decision**: Spokes carry **no** `prevent_destroy` and **no** management lock (clarify decision).
A `spoke-destroy` `workflow_dispatch` with a typed confirmation equal to the spoke name destroys
the spoke; the run removes both peering sides (the hub-side peering too, via the aliased platform
provider) so no dangling peering remains.

**Rationale**: Spokes are the disposable, high-churn unit; locks caused the migration's
lock-vs-operation traps. Confirm-gating satisfies Article VIII; clean both-side removal satisfies
Article IV.

## §8 Naming & tags

**Decision**: Spoke RG `rg-pdp-<region>-spoke-<name>` (verify the exact spoke pattern + any new CAF
abbreviation against `docs/conventions.md`; add rows there before use if missing — constitution
Development Workflow). Tags: `pdp-managed`, `pdp-deployed-by`, `pdp-spoke=<name>`, and `pdp-env`
where applicable (the constitution's tag schema already lists `pdp-spoke`/`pdp-env`).

**Rationale**: Article III discoverability; the tag keys already exist in the constitution.

**Alternatives considered**: reusing `pdp-fabric` (wrong scope).

## §9 State & CI wiring

**Decision**: Per-spoke backend key `spokes/<sub-id>/<spoke-name>` (set at init/dispatch, like the
fabric region key). New `spoke-vend.yml` (`workflow_dispatch` inputs: region, region_index,
target_subscription_id, spoke_name, spoke_cidr, optional shape) and `spoke-destroy.yml`
(typed-confirm). Both authenticate via OIDC into the **target** subscription (and the platform sub
for the hub side / fabric remote state). Additive on the spec-001 rails; the federated credential
must cover these dispatch refs (note the branch-dispatch fed-cred gap surfaced during the westus3
migration — ensure the CI identity's creds cover the vend/destroy dispatch context).

**Rationale**: Mirrors the proven fabric per-region key + dispatch-workflow pattern; spokes add the
target-subscription OIDC dimension.

**Alternatives considered**: one combined spokes state (violates one-state-per-unit); reusing
`iac-apply` (it's for fixed singleton stacks, not parameterized per-sub spokes).
