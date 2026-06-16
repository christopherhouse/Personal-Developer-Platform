# Implementation Plan: Regional Hub Fabric

**Branch**: `003-regional-hub-fabric` | **Date**: 2026-06-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/003-regional-hub-fabric/spec.md`

## Summary

Stand up (and cleanly tear down) a complete regional network hub for a registered Azure region
with a single deployable unit. The hub VNet is addressed **only** from the region's
ledger-reserved `/22` carve-out (`10.<index>.252.0/22`); a single **Azure Firewall (Basic
SKU)** is the region's controlled egress and exposes one private-IP next-hop for spokes; an
**Azure Bastion (Basic SKU)** gives private, no-public-endpoint management that reaches peered
spokes; and the hub VNet links to **platform-shared global Private DNS zones** owned by a
separate region-agnostic unit. Everything is AVM-first OpenTofu on the spec-001 rails
(plan-on-PR / apply-on-merge, OIDC, per-unit state), tagged `pdp-fabric=<region>` and
discoverable via Resource Graph, region-parameterized for additional regions without rework,
and protected for confirm-before-destroy with a dedicated `fabric-destroy` workflow. No verbs/
CLI/MCP and no spoke peering/routes/NSGs here — those are specs 006/007 and 004.

## Technical Context

**Language/Version**: OpenTofu 1.11.x (pinned `.opentofu-version`); HCL only — **no .NET in
this spec** (no verb/control-plane code; that's spec 006).

**Primary Dependencies**: `azurerm ~> 4.77.0` (+ transitive `random`/`time`/`modtm`); pinned
AVM Terraform modules — `avm-res-network-virtualnetwork` 0.18.0,
`avm-res-network-azurefirewall` 0.4.0, `avm-res-network-firewallpolicy` 0.3.4,
`avm-res-network-bastionhost` 0.9.0, `avm-res-network-publicipaddress` 0.2.1,
`avm-res-network-privatednszone` 0.5.0 (research §6). **No** prohibited deps (N/A — IaC only).

**Storage**: No database. OpenTofu state in the spec-001 backend (`stpdpeus2stateokoq`,
`use_azuread_auth`): `infra/fabric` → `fabrics/eastus2`; `infra/platform-dns` → `platform/dns`.

**Testing**: `tofu fmt -check` / `validate` / `plan` in CI (`iac-plan` matrix); AVM
smoke-validation under OpenTofu 1.11.x recorded in each stack README (Article V); quickstart.md
provides 9 apply/destroy validation scenarios mapped to SCs. No xUnit (no .NET surface).

**Target Platform**: Azure platform subscription, East US 2 first; design generalizes to any
registered region by `region_index`.

**Project Type**: Two OpenTofu infrastructure stacks + CI wiring (additive on spec-001 rails).

**Performance Goals**: N/A (infrastructure). Functional targets are the SCs (single-verb
stand-up; 100% address trace to ledger; single egress next-hop; clean teardown).

**Constraints**: Address space only from the ledger carve-out (Article VI); single hub egress
(Article VII); private + cheap by default — Firewall **Basic**, Bastion **Basic**, Standard/
Static PIPs (the platform-mandated minimum), no public management endpoint (Article IX);
destroyable with confirmation + accidental-deletion protection (Article IV/VIII).

**Scale/Scope**: Personal platform; ~255 possible regions (`/16` each), one hub per region;
single owner; the `/22` hub holds 3 reserved `/26`s + 13 `/26`s headroom.

## Constitution Check

*GATE: evaluated before Phase 0 and re-checked after Phase 1 design. Result: **PASS** — no
violations; Complexity Tracking empty. One subtle gate (Article VI) is a documented refinement,
mirroring spec 002.*

| Article | Gate | Status |
|---|---|---|
| I — Infra is Code | Hub VNet/firewall/bastion/DNS-links via OpenTofu + AVM; no portal mutations. | ✅ |
| II — AI calls verbs | No AI runtime / no runtime IaC generation in this spec. | ✅ N/A |
| III — Tagged/tracked | Fabric RG carries `pdp-managed`/`pdp-deployed-by`/`pdp-fabric=<region>`; DNS RG carries universal tags; both discoverable via Resource Graph. | ✅ |
| IV — Destroyable | `fabric-destroy` workflow (typed confirm) tears the RG down cleanly; carve-out + shared zones correctly survive; `prevent_destroy`+lock guard accidents. | ✅ |
| V — AVM-first | All compute/network from pinned AVM modules, smoke-tested under OpenTofu 1.11.x; primitives (lock, DNS links) get a README reason. | ✅ |
| VI — No address without allocation | Hub uses the ledger's ratified deterministic carve-out `10.<index>.252.0/22`; creates **no** allocation; live "registered?" check is spec-006 pre-dispatch. | ✅ (see note) |
| VII — Hub owns egress | The fabric **is** the egress (single Basic firewall); exposes one `firewall_private_ip` next-hop; no spoke route/alt-egress here. | ✅ |
| VIII — Plan before apply, confirm destroy | Plan-on-PR / apply-on-merge (inherited); destroy is `workflow_dispatch` + typed confirmation. | ✅ |
| IX — Secure & cheap | Private by default; only platform-mandated PIPs; Firewall Basic + Bastion Basic (smallest viable); no public mgmt endpoint. | ✅ |
| X — Specs before code | specify → clarify → plan → tasks → implement. | ✅ |

**Additional constraints**: OpenTofu-only ✅; .NET N/A this spec; control vs execution plane
(OpenTofu runs only in dispatched CI) ✅; no prohibited deps (N/A) ✅; CAF naming —
`vnet`/`snet`/`afw`/`afwp`/`pip`/`bas` already pinned "for 003 fabric" in `docs/conventions.md`
✅ (no new abbreviations; `rt`/`ng` pins go unused — route table is spec 004, no NAT gateway).

**Note (Article VI)** — the one subtle gate. The hub needs a CIDR; Article VI says it must come
from the ledger. It does: the carve-out `10.<index>.252.0/22` is the ledger's ratified standing
reservation (`region_pool.hub_carveout`, spec 002 data-model §1/§2), and the fabric **mirrors**
it deterministically from `region_index` — it does not invent it and creates no allocation row
(FR-003). The **live** "is this region registered?" enforcement (FR-004) is the **control
plane's** (spec 006), which queries the private Entra-only ledger and only then dispatches the
fabric workflow — the same staging spec 002 used (enforcement lands with the spec-006 runtime;
the deterministic reservation needs no live verb). Recorded in [research.md](research.md) §4 and
[contracts/fabric-interfaces.md](contracts/fabric-interfaces.md) §I1. Refinement, not violation.

## Project Structure

### Documentation (this feature)

```text
specs/003-regional-hub-fabric/
├── plan.md              # This file
├── research.md          # Phase 0 — egress/bastion/DNS/Article-VI decisions + verified AVM surfaces
├── data-model.md        # Phase 1 — stacks, inputs, resources, outputs, subnet plan
├── quickstart.md        # Phase 1 — 9 validation scenarios mapped to SCs
├── contracts/
│   ├── fabric-stack.md       # IaC contract for both stacks (invariants, teardown, CI)
│   └── fabric-interfaces.md  # upstream IPAM + downstream spoke/control-plane boundary
├── checklists/
│   └── requirements.md  # spec quality checklist (16/16 after clarify)
└── tasks.md             # Phase 2 — created by /speckit-tasks (NOT here)
```

### Source Code (repository root)

```text
infra/platform-dns/                  # NEW stack — state key platform/dns (region-agnostic, created once)
├── versions.tf  backend.tf  variables.tf  outputs.tf  main.tf
├── README.md                        # AVM smoke note; "shared zones, not links" ownership
└── .terraform.lock.hcl              # privatelink.{postgres,blob,vaultcore} zones

infra/fabric/                        # NEW stack — state key fabrics/eastus2 (region-parameterized)
├── versions.tf  backend.tf  variables.tf  outputs.tf  main.tf
├── README.md                        # AVM smoke results; protected-resource + fabric-destroy handoff
└── .terraform.lock.hcl              # RG(+lock), hub VNet+subnets, 3 PIPs, fw policy(Basic),
                                     #   firewall(Basic+mgmt NIC), bastion(Basic), hub→zone links

.github/workflows/
├── iac-plan.yml                     # EDIT — add platform-dns, fabric to the stack matrix
├── iac-apply.yml                    # EDIT — add platform-dns, fabric to the stack matrix
└── fabric-destroy.yml               # NEW — workflow_dispatch, typed-confirm=region, modeled on
                                     #   controlplane-destroy.yml (removes lock, destroys RG)
```

**Structure Decision**: Two additive OpenTofu stacks, each its own state per the
one-state-per-deployable-unit rule. **`platform-dns` is split out** because Azure Private DNS
zones are **global** — a single shared unit prevents the per-region duplication that owning
zones in the fabric stack would cause, and it makes fabric teardown remove only the hub *links*
(clarify Q3; FR-006/FR-016). The fabric reads the zones by `data` source (loose coupling). CI
is additive: the `iac-plan`/`iac-apply` matrices were built "matrix-ready; later stacks add
entries here," and the destroy workflow mirrors the proven `controlplane-destroy` pattern. No
rail, convention, or layout changes — every needed CAF abbreviation and tag is already pinned.

## Complexity Tracking

> No constitution violations — table intentionally empty. The second stack (`platform-dns`) is
> not added complexity but the *correct* decomposition for a global resource (one shared zone
> set vs. N duplicated regional sets); it reduces long-run complexity. The Article VI handling
> is a documented refinement (deterministic ledger carve-out + spec-006 pre-dispatch check),
> consistent with the spec-002 precedent — not a deviation.
