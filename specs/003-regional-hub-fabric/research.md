# Research: Regional Hub Fabric

Phase 0 decisions for spec 003. Azure/AVM facts verified live via the `microsoft-learn`
MCP and the Terraform Registry (AVM module versions/inputs) on 2026-06-15 — never from
memory (CLAUDE.md rule). Each decision is **Decision / Rationale / Alternatives**.

The two cost/security choices (egress tier, bastion mechanism) and the DNS ownership model
were settled in `/speckit-clarify` (spec.md §Clarifications, Session 2026-06-15); this file
records the *technical* consequences and the verified module surfaces that implement them.

---

## §1 Egress tier — Azure Firewall **Basic** with a mandatory Management NIC

**Decision**: The region's single egress path is an **Azure Firewall, Basic SKU**
(`firewall_sku_tier = "Basic"`, `firewall_sku_name = "AZFW_VNet"`) with an attached
**Basic-tier firewall policy**. Basic SKU **mandates a Management NIC**, so the hub VNet
carries **both** `AzureFirewallSubnet` and `AzureFirewallManagementSubnet` (each `/26`), and
the firewall gets **two Standard static public IPs** (data + management). The firewall's
**private IP** is the single default-route next-hop spokes point at.

**Rationale**: Owner chose Azure Firewall (clarify Q1) for L3–L7/FQDN egress control; Basic
is the cost-reduced tier for personal/SMB scale (≤250 Mbps). MS Learn confirms the hard
requirements:
- *"Firewall Basic has a mandatory requirement to be configured with a management NIC"* and
  *"all Basic Firewall versions … always have a Management NIC enabled."* The
  `AzureFirewallManagementSubnet` minimum size is `/26`
  ([management-nic](https://learn.microsoft.com/azure/firewall/management-nic)).
- *"Azure Firewall supports Standard SKU public IP addresses. Basic SKU public IP address …
  aren't supported."* → both firewall PIPs are **Standard / Static**
  ([configure-public-ip-firewall](https://learn.microsoft.com/azure/virtual-network/ip-services/configure-public-ip-firewall)).
- Spoke egress route = `0.0.0.0/0` → next-hop **Virtual appliance** → firewall **private IP**
  ([deploy-firewall-basic-portal-policy](https://learn.microsoft.com/azure/firewall/deploy-firewall-basic-portal-policy)).
  The hub **exposes** the private IP; the route table that consumes it is **spoke vending
  (spec 004)** — not built here.

**Alternatives considered**: NAT Gateway + NSGs (cheaper, but no L7/FQDN — owner declined);
NVA (most burden, least AVM-first — declined). Standard/Premium firewall (over-budget for a
personal platform). Forced-tunneling mode is **not** used (Basic doesn't support it, and the
hub egresses directly to the internet via the data PIP); the Management NIC is required
regardless, purely for Microsoft management traffic.

## §2 Management access — Azure Bastion **Basic**, always-on, in the hub

**Decision**: An **Azure Bastion, Basic SKU** host in the hub, in a dedicated
`AzureBastionSubnet` (`/26`), with a **Standard static** public IP. Always-on, part of the
standing fabric.

**Rationale**: Owner chose Bastion (clarify Q2). **Basic is the floor** that supports VNet
peering, so the single hub bastion reaches VMs in *peered spokes* — the management model this
fabric exists for. The **Developer SKU was rejected**: it *"doesn't support virtual network
peering"* and connects to *"one VM at a time"*
([bastion-sku-comparison](https://learn.microsoft.com/azure/bastion/bastion-sku-comparison)),
so it could only reach the workload-less hub VNet. No public management endpoint exists
otherwise (Article IX); the bastion's PIP is platform-required, not a workload endpoint.

**Alternatives considered**: Developer SKU (free, no peering — fails the requirement);
on-demand/JIT bastion (cheapest idle, but adds a spin-up lifecycle/verb → spec 006); P2S VPN
(gateway cost + client setup). All declined in clarify.

## §3 Private DNS ownership — platform-shared global zones, hub-linked

**Decision**: Azure Private DNS zones are **global** resources. A **separate, region-agnostic
deployable unit** (`infra/platform-dns`, state key `platform/dns`) owns a platform-shared set
of `privatelink.*` zones. The **fabric stack** does **not** create zones; it creates the
**hub VNet → zone links** only (looking the zones up by name via `data` sources). Fabric
teardown removes only the links; the shared zones survive (mirroring how the ledger hub
carve-out survives — spec.md FR-014).

Initial shared zone set (grow by PR as services arrive, per `docs/conventions.md` §1.3
private-DNS exception):
- `privatelink.postgres.database.azure.com` (Postgres Flexible Server **private endpoints** —
  distinct from the control-plane's VNet-integrated `pdp-controlplane.private.postgres.\
database.azure.com` created by spec 002, which is a different zone form and does **not**
  conflict).
- `privatelink.blob.core.windows.net` (Storage / blob).
- `privatelink.vaultcore.azure.net` (Key Vault).
- *Deferred*: ACA's `privatelink.<region>.azurecontainerapps.io` is **region-qualified**;
  added with the first container-app archetype (spec 008), not guessed now.

**Rationale**: One global zone set avoids per-region duplicate/split-horizon zones, satisfies
the clarify-Q3 decision and `architecture.md` ("Private DNS is centralized in the platform
subscription"), and keeps fabric teardown clean. `data`-source lookup (not remote state)
keeps the fabric loosely coupled to the DNS unit.

**Alternatives considered**: Fabric owns per-region zones (breaks FR-016 generalization — a
second region's fabric would collide on the same global zone names); a single mega-stack
(violates one-state-per-unit). "Create-if-absent" inside the fabric stack is not idiomatic in
OpenTofu (it owns or it doesn't) — the separate-unit + data-source split is the clean
equivalent.

## §4 Hub address space — the ledger's deterministic `/22` carve-out (Article VI)

**Decision**: The hub VNet address space is the region's **hub carve-out**
`10.<region_index>.252.0/22` — the **top `/22`** of the region's `/16`, recorded as a
standing reservation in `region_pool.hub_carveout` (spec 002 data-model §1/§2). The fabric
takes `region` + `region_index` as **typed inputs** and derives the space; it creates **no**
`allocation` row (the carve-out is reserved, not allocated — so nothing leaks on teardown,
spec.md FR-003). For East US 2: `region_index = 1` → supernet `10.1.0.0/16`, hub
`10.1.252.0/22`.

**Rationale / Article VI handling** (the one subtle gate — see plan Constitution Check):
Article VI says ranges come *only* from the ledger. The carve-out **is** the ledger's ratified
deterministic reservation; the fabric mirrors it, it does not invent it. The **live** check
"is this region registered?" (FR-004) belongs to the **control plane (spec 006)**, which can
reach the private, Entra-only ledger DB and queries it *before dispatching* the fabric
workflow (architecture.md: "control plane allocates from IPAM … dispatches the workflow"). At
the raw-IaC layer (this spec) the registered region's `region_index` is the typed input and
the derivation is deterministic — exactly the staging spec 002 established (schema/allocation
enforcement lands with the spec-006 runtime; the deterministic platform reservation was seeded
without a live verb). This is a **documented refinement, not a violation**.

**Alternatives considered**: A live DB query from the fabric's OpenTofu — impossible and
undesirable (the ledger is private + Entra-only, OpenTofu runs in CI with no DB path, and
Article I/II keep IaC out of the data plane). Hardcoding the CIDR — rejected; `region_index`
is the single knob (FR-016).

## §5 Hub subnet plan (within the `/22`)

**Decision**: Carve the `/22` (16 × `/26`) deterministically with `cidrsubnet(hub_cidr, 4, n)`:

| Subnet (reserved Azure name) | Index `n` | East US 2 prefix | Notes |
|---|---|---|---|
| `AzureFirewallSubnet` | 0 | `10.1.252.0/26` | No NSG, no custom route table (Azure rule). |
| `AzureFirewallManagementSubnet` | 1 | `10.1.252.64/26` | System route table only; no NSG/UDR. |
| `AzureBastionSubnet` | 2 | `10.1.252.128/26` | No route table; NSG omitted (safest for Basic). |
| *(reserved headroom)* | 3–15 | `10.1.252.192/26` … `10.1.255.0/24` | Future hub services (GatewaySubnet, DNS Private Resolver, etc.). |

**Rationale**: Three `/26`s fit with 13 `/26`s of headroom. The subnet **names are fixed by
Azure** and pass through the AVM VNet module's `subnets` map `name` field unchanged (verified
— no name validation). Per AVM/Azure rules, **no NSG or route table** is attached to the
firewall/bastion/management subnets (the module won't stop you — the stack must omit them).
Derivation is region-agnostic (FR-016): any `region_index` yields the same layout in its `/16`.

**Alternatives considered**: Smaller subnets — `/26` is the **enforced minimum** for all three
reserved subnets. A flat single subnet — impossible (reserved names are mandatory).

## §6 Verified AVM module surfaces (Terraform flavor, pinned)

All Terraform-flavor `Azure/avm-res-*/azurerm`; versions/inputs verified against the Terraform
Registry + tagged source 2026-06-15. **Smoke-validate each under OpenTofu 1.11.x before
reliance** (Article V; AVM is Terraform-first — record results in the stack README).

| Module | Version | Key inputs for this stack |
|---|---|---|
| `avm-res-network-virtualnetwork` | **0.18.0** (repo-pinned) | `subnets` map with reserved `name`s; `address_space = ["10.R.252.0/22"]`. |
| `avm-res-network-azurefirewall` | **0.4.0** | `firewall_sku_tier="Basic"`, `firewall_sku_name="AZFW_VNet"`, `firewall_policy_id`, `ip_configurations` (map; one carries `subnet_id`+`public_ip_address_id`), `firewall_management_ip_configuration` (object: `name`+`subnet_id`+`public_ip_address_id`, all required). Private IP is a module **output**. |
| `avm-res-network-firewallpolicy` | **0.3.4** | `firewall_policy_sku = "Basic"` (default is `null` — must be set). |
| `avm-res-network-bastionhost` | **0.9.0** | `sku="Basic"` (default), `ip_configuration{ subnet_id=<AzureBastionSubnet>, create_public_ip=false, public_ip_address_id=<…> }`. |
| `avm-res-network-publicipaddress` | **0.2.1** | `allocation_method="Static"`, `sku="Standard"`. ×3: fw-data, fw-mgmt, bastion. |
| `avm-res-network-privatednszone` | **0.5.0** (repo-pinned) | Used in **`platform-dns`** for the shared zones; in the **fabric** only its `virtual_network_links` surface is exercised (or `azurerm_private_dns_zone_virtual_network_link` against data-source zone IDs). |

**Gotchas captured**: firewall PIPs **must** be Standard (Basic firewall ≠ Basic PIP); the
deprecated `firewall_ip_configuration` (list) must **not** be combined with `ip_configurations`
(map); Basic firewall **requires** the Basic-tier policy + the management IP config; bastion
needs a Standard/Static PIP. **Route table module is NOT used in this spec** — the
`0.0.0.0/0 → firewall private IP` UDR is a **spec-004** concern; the fabric only outputs the
private IP.

## §7 Article IV/VIII teardown gating — protection + dedicated destroy workflow

**Decision**: Mirror the control-plane stack's posture (spec.md FR-015): `prevent_destroy =
true` on the fabric RG (blocks accidental `tofu destroy`) **plus** a `CanNotDelete`
`azurerm_management_lock` on the RG. Deliberate teardown runs a new **`fabric-destroy`**
`workflow_dispatch` workflow requiring a **typed confirmation** equal to the region (e.g.
`eastus2`), modeled on `controlplane-destroy.yml` / `foundations-destroy.yml`; protection
removal is a reviewed PR step documented in the stack README.

**Rationale**: A hub destroy severs egress + management for an entire region and would orphan
spoke peerings — high blast radius warrants confirm-before-destroy (Article VIII) and an
accidental-deletion guard, while still being **cleanly destroyable** (Article IV) via the
deliberate workflow. AVM provisions the firewall/bastion, so (as the control-plane README
notes for the server) a per-resource `prevent_destroy` can't be injected into module
internals — the **RG-level** guard + lock is the equivalent carve-out.

**Alternatives considered**: No protection (fails FR-015 / Article VIII for a high-blast-radius
stack); per-resource locks (not reachable through AVM module internals).

## §8 State, providers, CI wiring — additive on spec-001 rails

**Decision**: Two new stacks, each its own state in the spec-001 backend (account
`stpdpeus2stateokoq`, RG `rg-pdp-eastus2-foundations`, container `tfstate`,
`use_azuread_auth = true`):
- `infra/fabric` → key **`fabrics/eastus2`** (region a variable; the CI/dispatch sets the key
  per region for additional regions — full multi-region ergonomics = spec 009).
- `infra/platform-dns` → key **`platform/dns`** (region-agnostic, created once).

Providers match the control-plane stack: `azurerm ~> 4.77.0` (+ transitive `random`/`time`/
`modtm`), `required_version = "~> 1.11.0"`, `storage_use_azuread = true`. CI is additive:
add `fabric` and `platform-dns` to the **existing** `iac-plan.yml` / `iac-apply.yml` matrix
(`stack: [foundations, control-plane, platform-dns, fabric]`, `working-directory:
infra/${{ matrix.stack }}`), and add `fabric-destroy.yml`. No rail/convention changes.

**Rationale**: One state per deployable unit (architecture.md); the matrix workflows were
built "matrix-ready; later stacks add entries here" (iac-plan.yml comment). Plan-on-PR /
apply-on-merge (Article VIII) is inherited unchanged.

**Alternatives considered**: One combined stack (violates one-state-per-unit, couples DNS to
region); OpenTofu workspaces for regions now (premature — spec 009).

## §9 Naming & tags — all pins already exist

**Decision**: Use the pinned CAF abbreviations from `docs/conventions.md` §1.1 — `rg`, `vnet`,
`snet` (non-reserved hub subnets only), `afw`, `afwp`, `pip`, `bas`. Private DNS zones are
named by **domain** (conventions §1.3 exception). Tags: fabric RG carries `pdp-managed=true`,
`pdp-deployed-by`, **`pdp-fabric=<region>`**; the `platform-dns` RG carries the universal tags
only (platform scope — no `pdp-fabric`).

**Rationale**: `vnet`/`snet`/`afw`/`afwp`/`pip`/`bas` were **pre-pinned for "003 fabric"** in
conventions.md — no new abbreviations needed. Reserved Azure subnet names
(`AzureFirewallSubnet`, etc.) are Azure-mandated and bypass the `<type>-pdp-…` pattern by
design.

**Alternatives considered**: New abbreviations (unnecessary — all present). `rt`/`ng` pins are
unused here (route table → spec 004; no NAT gateway).
