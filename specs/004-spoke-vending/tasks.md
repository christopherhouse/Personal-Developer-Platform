# Tasks: Spoke Vending

**Input**: Design documents from `/specs/004-spoke-vending/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: **No automated (xUnit) test tasks** — this spec is pure OpenTofu infrastructure with no
.NET surface (Gate G1 deferred the allocator runtime to spec 006). Acceptance is
`tofu fmt/validate/plan` in CI, **AVM smoke-validation under OpenTofu 1.11.x** (Article V, recorded
in the stack README), and the **9 quickstart.md scenarios** mapped to SC-001…009.

**Organization**: Tasks grouped by user story — US1 vend (MVP), US2 multi-spoke-per-sub, US3 clean
teardown, US4 region/subscription generalization — each an independently demonstrable increment.

## Key facts carried from design (do not re-derive)

| Fact | Value |
|---|---|
| Allocation (Gate G1 / Option A) | `spoke_cidr` is a **typed input** fitted to the region `/16`; **no live ledger allocation** here — deferred to spec 006. Validate within `10.<region_index>.0.0/16`, outside `…252.0/22`. |
| Fabric consumption | `terraform_remote_state` against `fabrics/<region>` → `hub_vnet_id`, `hub_resource_group_name`, `firewall_private_ip`, `shared_dns_zone_ids` (contracts §I1) |
| Cross-sub | default `azurerm` provider = target sub; **aliased** provider = platform sub (hub-side peering, DNS links, fabric remote state). Dual-subscription OIDC (contracts §I3) |
| Egress | route table `0.0.0.0/0` → `VirtualAppliance` = `firewall_private_ip`, associated to spoke subnets (Article VII) |
| NSG | one NSG per subnet (or shared), **Azure default rules only** (FR-018); every subnet associated (FR-005) |
| Peering | spoke→hub (target sub) + hub→spoke (platform sub, aliased), both `allow_forwarded_traffic = true` |
| Shape | configurable `subnets` map (size + delegations); default single workload subnet (FR-017) |
| Protection | **none** — no `prevent_destroy`, no lock; confirm-gated `spoke-destroy` only |
| State key | `spokes/<sub-id>/<spoke-name>` (set at init/dispatch) |
| Worked example | `westus3`, index 2, spoke `app1`, `spoke_cidr = 10.2.16.0/24` |

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelizable (different files, no incomplete-task dependency)
- **[Story]**: US1–US4 per spec.md

---

## Phase 1: Setup (shared scaffolding)

**Purpose**: The parameterized spoke stack skeleton (validate-green) and the dispatch-workflow
shells everything else lands on. Rides spec-001 rails — no rail/convention changes beyond the spoke
naming/tag rows.

- [X] T001 Confirm/extend `docs/conventions.md`: spoke RG/VNet/subnet/NSG/route-table naming
      (`rg-pdp-<region>-spoke-<name>`, `vnet`/`snet`/`nsg`/`rt`) and the `pdp-spoke`/`pdp-env` tags;
      add any missing CAF abbreviation rows **before** use (constitution Development Workflow)
- [X] T002 Scaffold `infra/spoke/versions.tf`: `required_version "~> 1.11.0"`, `azurerm ~> 4.77.0`
      (+ `random`/`time`/`modtm`), `storage_use_azuread = true`; **two provider blocks** — default
      (target sub via `var.target_subscription_id`) and `alias = "platform"` (via
      `var.platform_subscription_id`)
- [X] T003 Scaffold `infra/spoke/backend.tf`: PDP backend, `use_azuread_auth = true`, **key set at
      init** (`spokes/<sub-id>/<spoke-name>`); document the partial-config init
- [X] T004 Scaffold `infra/spoke/variables.tf`: `region`, `region_index` (validation 1–255),
      `target_subscription_id`, `platform_subscription_id`, `spoke_name`, `spoke_cidr`
      (**validation**: inside `10.${region_index}.0.0/16`, outside `…252.0/22`), `subnets` map
      (size + delegations, sensible default), with `outputs.tf` stubs — `tofu validate` green
- [X] T005 [P] Create `.github/workflows/spoke-vend.yml` skeleton (`workflow_dispatch` inputs:
      region, region_index, target_subscription_id, spoke_name, spoke_cidr, optional shape;
      concurrency `tofu-spoke-<name>`; OIDC into target + platform subs; init with the per-spoke
      backend key) — no apply logic yet
- [X] T006 [P] Create `.github/workflows/spoke-destroy.yml` skeleton (`workflow_dispatch`, typed
      `destroy-confirm` = spoke name; same dual-sub OIDC) — no destroy logic yet

**Checkpoint**: Stack validates empty; workflows present as dispatch shells.

---

## Phase 2: Foundational (blocking prerequisite) — upstream wiring

**Purpose**: The fabric remote-state read and the dual-provider plumbing every spoke resource
depends on. ⚠️ Blocks all of US1.

- [X] T007 Add `infra/spoke/locals.tf` + `data.terraform_remote_state.fabric` (platform provider)
      against `fabrics/${var.region}`; surface `hub_vnet_id`, `hub_resource_group_name`,
      `firewall_private_ip`, `shared_dns_zone_ids`; `local.tags = { pdp-managed, pdp-deployed-by,
      pdp-spoke=var.spoke_name }`; fail-fast if the fabric state is absent (FR-011)
- [X] T008 Verify the CI identity's **federated credentials cover the `spoke-vend`/`spoke-destroy`
      dispatch context** and that it holds Contributor (target sub) + Network Contributor (hub RG)
      + Private DNS Zone Contributor (DNS RG) in the platform sub (contracts §I3) — fix the
      branch-dispatch fed-cred gap surfaced in the westus3 migration **before** first vend

**Checkpoint**: A plan can read fabric outputs and authenticate into both subscriptions.

---

## Phase 3: User Story 1 — Vend a spoke (Priority: P1) 🎯 MVP

**Goal**: One `spoke-vend` dispatch produces a peered, hub-egressing, NSG-protected, DNS-linked
spoke in a target subscription from a typed CIDR.

**Independent Test**: quickstart Scenarios **1, 2, 3, 4, 6, 9** against a vended `app1`.

- [X] T009 [US1] Spoke RG `rg-pdp-${region}-spoke-${spoke_name}` (target sub) in
      `infra/spoke/main.tf` with `local.tags` (**no** lock / `prevent_destroy`)
- [X] T010 [US1] Spoke VNet + subnets via `Azure/avm-res-network-virtualnetwork/azurerm`:
      `address_space=[var.spoke_cidr]`, `subnets` from `var.subnets` (prefixes via
      `cidrsubnet(var.spoke_cidr, …)`, delegations passthrough), each subnet associated to its NSG
      (T011) and the route table (T012)
- [X] T011 [P] [US1] `azurerm_network_security_group` (per subnet or one shared) `nsg-pdp-…` with
      **no custom rules** (Azure defaults only, FR-018); ensure **every** subnet is associated
- [X] T012 [P] [US1] `azurerm_route_table` `rt-pdp-${region}-${spoke_name}` with `0.0.0.0/0` →
      `VirtualAppliance` next-hop `firewall_private_ip` (from T007); associate to spoke subnets;
      **no** other default route (Article VII)
- [X] T013 [US1] Spoke→hub peering `azurerm_virtual_network_peering` (target sub):
      `remote_virtual_network_id = hub_vnet_id`, `allow_forwarded_traffic = true` (depends on T010)
- [X] T014 [US1] Hub→spoke peering `azurerm_virtual_network_peering` via the **platform** aliased
      provider, on the hub VNet in `hub_resource_group_name`, `allow_forwarded_traffic = true`
      (depends on T010)
- [X] T015 [US1] Spoke→shared-zone links: `azurerm_private_dns_zone_virtual_network_link` (platform
      provider, in the DNS RG) for each entry in `shared_dns_zone_ids`,
      `vnetlink-pdp-${region}-${spoke_name}-<zone>`, `registration_enabled = false` (depends on T010)
- [X] T016 [US1] Implement `infra/spoke/outputs.tf`: `spoke_vnet_id`, `spoke_resource_group_name`,
      `spoke_subnets` (map), `spoke_cidr` — per contracts §I4
- [X] T017 [US1] Finish `spoke-vend.yml`: `tofu init` (per-spoke key) → `plan` → `apply`; render
      plan; pass the dispatch inputs as `-var`s
- [X] T018 [US1] `infra/spoke/README.md`: AVM smoke-validation results (vnet module under OpenTofu
      1.11.x), the **typed-CIDR staging** note (Gate G1; live allocation = spec 006), the
      **dual-subscription identity** requirement (§I3), and the egress/NSG posture
- [~] T019 [US1] `tofu fmt -check` + `validate` clean ✅ (local gate passed under OpenTofu 1.11.6);
      **remaining (live CI):** vend `app1` via the `spoke-vend` dispatch and run quickstart
      Scenarios 1–4, 6, 9; confirm a **second vend is a no-op** (idempotency, FR-009)

**Checkpoint**: A spoke is vended, peered, egress-through-hub, NSG'd, DNS-linked — MVP demoable.

---

## Phase 4: User Story 2 — Many spokes per subscription (Priority: P2)

**Goal**: Independent spokes coexist in one subscription with non-overlapping space and lifecycles.

**Independent Test**: quickstart Scenario **5**.

- [ ] T020 [US2] Confirm the per-spoke backend key (`spokes/<sub-id>/<spoke-name>`) and naming make
      `(subscription, spoke-name)` the identity; re-vending a name converges, a new name adds a
      spoke (FR-007/009) — document in README
- [ ] T021 [US2] Run quickstart Scenario 5: vend `app2` (`10.2.17.0/24`) into the same subscription
      as `app1`; confirm both exist, non-overlapping, each independently peered; re-vend `app1`
      (no duplicate) and confirm `app2` untouched

**Checkpoint**: Multiple spokes per subscription demonstrated independent.

---

## Phase 5: User Story 3 — Clean, confirm-gated teardown (Priority: P2)

**Goal**: Destroying one spoke removes it and **both** peering sides, leaving siblings/hub/zones
untouched; no lock to remove.

**Independent Test**: quickstart Scenario **7**.

- [ ] T022 [US3] Finish `spoke-destroy.yml`: typed `destroy-confirm` must equal the spoke name;
      `tofu init` (the spoke's key) → `destroy`; ensure the destroy removes the **hub-side** peering
      via the platform aliased provider (no dangling peering — Article IV) — modeled on
      `fabric-destroy.yml` minus the lock-removal (spokes have no lock)
- [ ] T023 [US3] Run quickstart Scenario 7: destroy `app1`; confirm zero residual spoke resources,
      `peer-hub-to-app1` removed from the hub VNet, `app2`/hub/shared zones untouched, and the
      `10.2.16.0/24` block reusable

**Checkpoint**: Spoke is cleanly destroyable; US1+US2+US3 demonstrable.

---

## Phase 6: User Story 4 — Generalize across subscriptions/regions (Priority: P3)

**Goal**: The same code vends into any registered region and any writable subscription by changing
only inputs.

**Independent Test**: quickstart Scenario **8**.

- [ ] T024 [US4] Audit `infra/spoke/` for hardcoded region/subscription/CIDR — everything MUST flow
      from `var.*`/`local.*`/fabric remote state; fix any leak (FR-010)
- [ ] T025 [US4] Run quickstart Scenario 8: `tofu plan` a spoke into a second subscription and/or a
      second region by changing only inputs; confirm names/space/hub/DNS derive correctly with zero
      source edits

**Checkpoint**: All four stories independently demonstrable.

---

## Phase 7: Polish & cross-cutting

- [ ] T026 [P] Commit `infra/spoke/.terraform.lock.hcl` with multi-platform hashes
      (`linux_amd64` + `windows_amd64`, matching the other stacks) for reproducible CI
- [ ] T027 [P] Update `docs/spec-backlog.md` Status (spec 4 in-progress/complete) and note the
      Gate-G1 deferral (live allocation → spec 006)
- [ ] T028 Full quickstart.md run end-to-end (Scenarios 1–9) on a clean vend→destroy cycle across
      two spokes; record results and confirm SC-001…009 all pass

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 → T002/T003/T004 (stack), T005/T006 [P] (workflow shells).
- **Foundational (Phase 2)**: T007 (fabric remote state + locals) and T008 (identity/fed-cred) —
  **block all of US1**.
- **US1 (Phase 3)**: needs Phase 2. Order: T009 → T010 → {T011 [P], T012 [P]} → {T013, T014, T015}
  → T016 → T017 → T018 → T019.
- **US2 (Phase 4)**: needs US1 (T020 → T021).
- **US3 (Phase 5)**: needs US1 resources (T022 → T023).
- **US4 (Phase 6)**: needs US1 complete (T024 → T025).
- **Polish (Phase 7)**: after the stories you intend to ship.

### Story independence

- **US1** is the MVP and stands alone (a working spoke).
- **US2** is a coexistence/identity demonstration over US1 (Scenario 5).
- **US3** layers the destroy path onto US1 (Scenario 7).
- **US4** is a parameterization audit + second-target plan over US1 (Scenario 8).

### Parallel opportunities

- T005 [P] / T006 [P] alongside the stack scaffolding.
- Within US1: T011 [P] (NSG) and T012 [P] (route table) after T010.
- T026 [P] / T027 [P] in polish.

---

## Implementation Strategy

### MVP first (US1 only)
Phase 1 → Phase 2 → Phase 3 US1 → **STOP & VALIDATE** (quickstart 1–4, 6, 9) → demo a live spoke in
westus3. Then US2 → US3 → US4 as independent increments.

## Notes

- [P] = different files, no incomplete-task dependency.
- **Deploy-time prerequisite (not an impl task)**: a `spoke_cidr` chosen within the region `/16`
  and clear of the hub carve-out; until spec 006 automates by-size allocation, the owner supplies
  it (Gate G1 / research §1). Cross-spoke non-overlap becomes ledger-enforced with spec 006.
- The live IPAM allocator, the verb/CLI/MCP that invoke vending, workloads-in-spokes, hub-to-hub,
  and diagnostics are **specs 006/008/009/010** — not here.
