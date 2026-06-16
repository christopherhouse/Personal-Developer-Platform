# Implementation Plan: Spoke Vending

**Branch**: `004-spoke-vending` | **Date**: 2026-06-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/004-spoke-vending/spec.md`

## Summary

Vend a **spoke** — a configurable, workload-ready VNet in any writable **target subscription**,
peered to its region's hub, egressing only through the hub firewall, NSG-protected on every
subnet, and linked to the platform-shared private DNS — as one atomic operation, with clean,
confirm-gated teardown. It consumes the spec-003 fabric's published outputs (`hub_vnet_id`,
`hub_resource_group_name`, `firewall_private_ip`, `shared_dns_zone_ids`) and addresses the spoke
from the spec-002 region `/16`. Each spoke is its own deployable unit (`spokes/<sub-id>/<spoke-name>`),
multiple per subscription, freely destroyable (no lock).

**Allocation (Gate G1 resolved — Option A)**: the spoke CIDR is a **typed input** fitted to the
region `/16`; this spec performs **no live ledger allocation** (a control-plane function against
the private ledger that OpenTofu can't reach). Live by-size allocation + the ledger write/release
land with the spec-006 control plane. This mirrors spec 003's typed-`region_index` staging — a
documented Article-VI refinement, not a violation.

## Technical Context

**Language/Version**: OpenTofu 1.11.x (HCL only). **No .NET in this spec** — allocation runtime is
spec 006 (Gate G1).

**Primary Dependencies**: `azurerm ~> 4.77.0` (+ transitive `random`/`time`/`modtm`); pinned AVM
modules — `avm-res-network-virtualnetwork` (spoke VNet + subnets + NSGs + route table association),
plus `azurerm_virtual_network_peering` (both sides) and
`azurerm_private_dns_zone_virtual_network_link`. Consumes spec-003 fabric outputs via
`terraform_remote_state` against `fabrics/<region>`.

**Storage**: No new store. OpenTofu state per spoke at `spokes/<sub-id>/<spoke-name>` in the
spec-001 backend (`stpdpwus3statejqyq`, `use_azuread_auth`). Address allocation rows in the
spec-002 ledger are written by the control plane (spec 006), not here.

**Testing**: `tofu fmt -check`/`validate`/`plan` in CI; AVM smoke-validation under OpenTofu 1.11.x
recorded in the stack README (Article V); quickstart scenarios mapped to SCs. No xUnit (no .NET).

**Target Platform**: Azure — spokes deploy into **arbitrary writable target subscriptions** (the
platform's first cross-subscription deploys); the hub/ledger live in the platform subscription.

**Project Type**: One parameterized OpenTofu spoke stack + two dispatch workflows (vend, destroy);
additive on the spec-001 CI rails.

**Performance Goals**: N/A (infra). Functional targets are the SCs.

**Constraints**: address only from the region `/16` typed block (Article VI, staged); single hub
egress (Article VII); private + **NSG on every subnet** + smallest viable SKUs (Article IX);
destroyable, no lock, confirm-gated destroy (Articles IV/VIII); OpenTofu only in dispatched CI with
OIDC into **both** the target and platform subscriptions (Articles I/II).

**Scale/Scope**: many spokes per subscription, many subscriptions, many regions; each spoke a
configurable VNet (size, subnet count/sizes, delegations) within the region `/16`.

## Constitution Check

*GATE: evaluated before Phase 0; re-checked after Phase 1. Result: **PASS** — Gate G1 resolved via
Option A (typed-CIDR staging); Complexity Tracking records the one refinement.*

| Article | Gate | Status |
|---|---|---|
| I — Infra is Code | Spoke VNet/subnets/NSGs/route/peering/DNS-links via OpenTofu + AVM. | ✅ |
| II — AI calls verbs / plane split | Allocation stays a control-plane concern (deferred to 006); this spec is pure execution-plane IaC in dispatched CI. | ✅ (G1 resolved) |
| III — Tagged/tracked | Spoke RG carries `pdp-managed`/`pdp-spoke`/`pdp-env`/`pdp-deployed-by`; Resource-Graph discoverable. | ✅ |
| IV — Destroyable | Confirm-gated teardown removes the spoke cleanly; siblings/hub/zones untouched; no leaked peerings. | ✅ |
| V — AVM-first | Spoke network from pinned AVM modules, smoke-validated; peering/DNS-link primitives get a README reason. | ✅ |
| VI — No address without allocation | Spoke draws from the ledger's region `/16`; typed block now, live allocation + recording by spec 006 (staging, mirrors spec 002/003). | ✅ (refinement, see below) |
| VII — Hub owns egress | `0.0.0.0/0 → firewall_private_ip`; no alternative egress; no spoke-to-spoke peering. | ✅ |
| VIII — Plan before apply / confirm destroy | Plan-on-PR / apply-on-merge inherited; vend + destroy are dispatched, destroy typed-confirm. | ✅ |
| IX — Secure & cheap | Private by default; **NSG on every spoke subnet**; smallest SKUs; no public ingress. | ✅ |
| X — Specs before code | specify → clarify → plan → tasks → implement. | ✅ |

**Additional constraints**: OpenTofu-only ✅; .NET N/A this spec (Gate G1) ✅; control vs execution
plane preserved (allocation = control plane/006; this spec only applies) ✅; CAF naming — needs
`pdp-spoke`/`pdp-env` tags (already in the constitution's tag schema) and spoke CAF abbreviations
(verify in `docs/conventions.md` during research; add rows if missing — see research §6).

## Project Structure

### Documentation (this feature)

```text
specs/004-spoke-vending/
├── plan.md              # This file
├── research.md          # Phase 0 — allocation staging, cross-sub peering, AVM surfaces, NSG model
├── data-model.md        # Phase 1 — inputs, resources, outputs, the typed-CIDR/region fit
├── quickstart.md        # Phase 1 — validation scenarios mapped to SCs
├── contracts/
│   └── spoke-interfaces.md   # upstream (fabric + ledger) and downstream (workload) boundaries
├── checklists/requirements.md
└── tasks.md             # Phase 2 — /speckit-tasks (NOT here)
```

### Source Code (repository root)

```text
infra/spoke/                         # NEW parameterized stack — state key spokes/<sub-id>/<spoke-name>
├── versions.tf  backend.tf          # backend key + target-sub provider set at init/dispatch
├── variables.tf                     # region, region_index, target_subscription_id, spoke_name,
│                                     #   spoke_cidr (typed), subnet/shape map, platform_subscription_id
├── main.tf  outputs.tf  locals.tf
├── README.md                        # AVM smoke results; cross-sub identity + typed-CIDR staging note
└── .terraform.lock.hcl

.github/workflows/
├── spoke-vend.yml                   # NEW — workflow_dispatch (region, sub, name, cidr, shape);
│                                     #   OIDC into target + platform subs; per-spoke backend key
└── spoke-destroy.yml                # NEW — workflow_dispatch, typed-confirm = spoke name
```

**Structure Decision**: One **parameterized** spoke stack (vs. the platform's fixed singleton
stacks) because spokes are many and per-target-subscription. The vend/destroy workflows set the
per-spoke backend key and the target-subscription provider at dispatch, and authenticate via OIDC
into both the target subscription (spoke side) and the platform subscription (hub-side peering,
fabric remote state). Fabric outputs are read via `terraform_remote_state` (the documented spec-003
interface), not hardcoded.

## Complexity Tracking

> One documented refinement; no unjustified violations.

| Item | Why | Why the simpler/strict reading is not used |
|---|---|---|
| Article VI — typed spoke CIDR instead of live allocation | The live by-size allocator is a control-plane function against a private ledger OpenTofu can't reach (Gate G1); blocking spoke vending on the entire spec-006 runtime would stall the critical path. | A typed block fitted to the region `/16` and recorded by spec 006 preserves "address only from the ledger's space" while keeping this spec pure execution-plane IaC — the same staging spec 002/003 used. Not a deviation; the live allocator still lands in 006. |
