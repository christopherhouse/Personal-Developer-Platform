# Tasks: Regional Hub Fabric

**Input**: Design documents from `/specs/003-regional-hub-fabric/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: **No automated (xUnit) test tasks** — this spec is pure OpenTofu infrastructure with
no .NET surface. Acceptance is `tofu fmt/validate/plan` in CI, **AVM smoke-validation under
OpenTofu 1.11.x** (Article V, recorded per-stack README), and the **9 quickstart.md scenarios**
mapped to SC-001…009. Each user story names the quickstart scenarios that prove it.

**Organization**: Tasks grouped by user story — US1 stand-up (MVP), US2 clean teardown, US3
region generalization — so each is an independently demonstrable increment.

## Key facts carried from design (do not re-derive)

| Fact | Value |
|---|---|
| Hub address space | `10.<region_index>.252.0/22` — the ledger hub carve-out; **only** address input is `region_index` (eastus2 = 1 → `10.1.252.0/22`) |
| Subnets (via `cidrsubnet(hub,4,n)`) | `AzureFirewallSubnet` `…252.0/26` (n0), `AzureFirewallManagementSubnet` `…252.64/26` (n1), `AzureBastionSubnet` `…252.128/26` (n2); no NSG/UDR on any |
| Egress | Azure Firewall **Basic** (`AZFW_VNet`) + Basic policy + **2 Standard PIPs** (data+mgmt NIC is mandatory for Basic); outputs `firewall_private_ip` |
| Management | Azure Bastion **Basic** + 1 Standard PIP (Developer SKU rejected — no peering) |
| Private DNS | platform-shared global zones in `infra/platform-dns`; fabric **links** hub only |
| AVM pins | virtualnetwork 0.18.0 · azurefirewall 0.4.0 · firewallpolicy 0.3.4 · bastionhost 0.9.0 · publicipaddress 0.2.1 · privatednszone 0.5.0 |
| State keys | `infra/fabric` → `fabrics/eastus2` · `infra/platform-dns` → `platform/dns` |
| Backend | account `stpdpeus2stateokoq`, RG `rg-pdp-eastus2-foundations`, container `tfstate`, `use_azuread_auth = true` |
| Providers | `required_version "~> 1.11.0"`, `azurerm "~> 4.77.0"` (+ transitive `random`/`time`/`modtm`), `storage_use_azuread = true` |
| Teardown gate | `prevent_destroy` + `CanNotDelete` lock on fabric RG; `fabric-destroy` workflow, typed confirm = region |

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no incomplete-task dependency)
- **[Story]**: US1–US3 per spec.md

---

## Phase 1: Setup (shared scaffolding)

**Purpose**: Both OpenTofu stack skeletons (validate-green stubs) and the CI matrix wiring
everything else lands on. Rides spec-001 rails — no rail/convention changes.

- [ ] T001 Scaffold `infra/platform-dns/`: `versions.tf` (`required_version "~> 1.11.0"`,
      `azurerm "~> 4.77.0"` + `random`/`time`/`modtm`, `storage_use_azuread = true`,
      `features {}`), `backend.tf` (PDP backend, key `platform/dns`, `use_azuread_auth = true`),
      `variables.tf` (`platform_subscription_id` default as control-plane stack),
      `outputs.tf` (stubs) — `tofu validate` green
- [ ] T002 Scaffold `infra/fabric/`: `versions.tf` (same provider block), `backend.tf`
      (key `fabrics/eastus2`, `use_azuread_auth = true`), `variables.tf` with `region`,
      `region_index` (**`validation` block: integer 1–255**, message cites index 0 = platform
      supernet), `platform_subscription_id`, `platform_dns_resource_group_name` (default
      `rg-pdp-eastus2-dns`), `outputs.tf` (stubs) — `tofu validate` green
- [ ] T003 [P] Add `platform-dns` and `fabric` to the stack matrix in
      `.github/workflows/iac-plan.yml` and `.github/workflows/iac-apply.yml`
      (`stack: [foundations, control-plane, platform-dns, fabric]`)

**Checkpoint**: Both stacks validate empty; CI plans/applies them as no-ops.

---

## Phase 2: Foundational (blocking prerequisite) — platform-shared DNS

**Purpose**: The shared Private DNS zones the fabric links to MUST exist before US1's DNS-link
task. ⚠️ Blocks the DNS-link portion of US1.

- [ ] T004 Implement `infra/platform-dns/main.tf`: RG `rg-pdp-eastus2-dns` (universal tags
      only — `pdp-managed`, `pdp-deployed-by`; **no** `pdp-fabric`) + three zones via
      `Azure/avm-res-network-privatednszone/azurerm` 0.5.0 —
      `privatelink.postgres.database.azure.com`, `privatelink.blob.core.windows.net`,
      `privatelink.vaultcore.azure.net` (no VNet links here — links belong to fabrics)
- [ ] T005 [P] Implement `infra/platform-dns/outputs.tf` (`dns_resource_group_name`, a
      `map` of zone name → `resource_id`) and `infra/platform-dns/README.md` (ownership rule:
      **zones live here, links live in fabrics**; AVM smoke-validation result for
      privatednszone 0.5.0 under OpenTofu 1.11.x)

**Checkpoint**: `platform/dns` applies; three global zones exist, untagged with `pdp-fabric`.

---

## Phase 3: User Story 1 — Stand up a regional hub fabric (Priority: P1) 🎯 MVP

**Goal**: One `tofu apply` of `infra/fabric` produces a complete, inventory-visible, spoke-ready
hub: VNet from the ledger carve-out, single Basic-firewall egress, Basic bastion, hub→shared-DNS
links, and the downstream outputs.

**Independent Test**: quickstart.md Scenarios **1, 2, 3, 4, 5, 6, 9** (single-verb stand-up;
address traces to carve-out; one egress next-hop; no public mgmt endpoint; Resource-Graph
discoverable; DNS shared not duplicated; all via dispatched CI).

- [ ] T006 [US1] Add `infra/fabric/locals.tf` (or in `main.tf`): `local.region`,
      `local.region_short`, `local.hub_address_space = "10.${var.region_index}.252.0/22"`,
      `local.tags = { pdp-managed="true", pdp-deployed-by="github-actions",
      pdp-fabric=var.region }`, and `data.azurerm_client_config.current`
- [ ] T007 [US1] Fabric RG `rg-pdp-eastus2-fabric` in `infra/fabric/main.tf` with `local.tags`
      (egress/teardown protection added in US2 — keep US1 a clean stand-up increment)
- [ ] T008 [US1] Hub VNet via `Azure/avm-res-network-virtualnetwork/azurerm` 0.18.0:
      name `vnet-pdp-eastus2-hub`, `address_space = [local.hub_address_space]`, `subnets` map
      with reserved `name`s `AzureFirewallSubnet`/`AzureFirewallManagementSubnet`/
      `AzureBastionSubnet` at `cidrsubnet(...,4,0|1|2)` — **no** `network_security_group` or
      `route_table` on any (Azure rule, research §5)
- [ ] T009 [P] [US1] Three Standard/Static public IPs via
      `Azure/avm-res-network-publicipaddress/azurerm` 0.2.1: `pip-pdp-eastus2-afw`,
      `pip-pdp-eastus2-afw-mgmt`, `pip-pdp-eastus2-bas` (`sku="Standard"`,
      `allocation_method="Static"`)
- [ ] T010 [P] [US1] Basic firewall policy via `Azure/avm-res-network-firewallpolicy/azurerm`
      0.3.4: `afwp-pdp-eastus2-hub`, `firewall_policy_sku = "Basic"`
- [ ] T011 [US1] Azure Firewall (Basic) via `Azure/avm-res-network-azurefirewall/azurerm`
      0.4.0: `afw-pdp-eastus2-hub`, `firewall_sku_tier="Basic"`, `firewall_sku_name="AZFW_VNet"`,
      `firewall_policy_id` = T010, `ip_configurations` = data PIP + `AzureFirewallSubnet`,
      `firewall_management_ip_configuration` = mgmt PIP + `AzureFirewallManagementSubnet`
      (depends on T008, T009, T010)
- [ ] T012 [US1] Azure Bastion (Basic) via `Azure/avm-res-network-bastionhost/azurerm` 0.9.0:
      `bas-pdp-eastus2-hub`, `sku="Basic"`, `ip_configuration{ subnet_id=AzureBastionSubnet,
      create_public_ip=false, public_ip_address_id=<pip-bas> }` (depends on T008, T009)
- [ ] T013 [US1] Hub→shared-zone links in `infra/fabric/main.tf`:
      `data "azurerm_private_dns_zone"` for each shared zone (in
      `var.platform_dns_resource_group_name`) + `azurerm_private_dns_zone_virtual_network_link`
      `vnetlink-pdp-eastus2-hub-<zone>` (`registration_enabled=false`) (depends on T008 + Phase 2)
- [ ] T014 [US1] Implement `infra/fabric/outputs.tf`: `hub_vnet_id`, `hub_vnet_name`,
      `hub_resource_group_name`, `hub_address_space`, `firewall_private_ip` (from T011 module
      output), `shared_dns_zone_ids` (map) — per contracts/fabric-interfaces.md §I2
- [ ] T015 [US1] `infra/fabric/README.md`: AVM smoke-validation results (all 5 fabric modules
      under OpenTofu 1.11.x), the **two-Standard-PIP / mandatory mgmt-NIC** Basic-firewall note,
      cross-subscription peering-readiness note (hub side), and the Article-VI consumption note
      (carve-out from `region_index`, no allocation)
- [ ] T016 [US1] `tofu fmt -check` + `validate` clean; run quickstart Scenarios 1–6 & 9 against
      the applied stack (record outputs); confirm a **second `apply` is a no-op** (0 changes —
      the re-deploy idempotency Edge case; OpenTofu convergence, no duplicate hub)

**Checkpoint**: A registered region's hub is deployed, discoverable, single-egress, private-
management, DNS-linked — MVP complete and demoable.

---

## Phase 4: User Story 2 — Tear down a regional hub fabric cleanly (Priority: P2)

**Goal**: Accidental destroy is blocked; deliberate destroy is a single confirmed workflow that
leaves zero residue while the ledger carve-out and shared zones survive.

**Independent Test**: quickstart.md Scenario **7** (a `tofu plan -destroy` fails on protection;
`fabric-destroy` with typed confirm removes everything; carve-out + shared zones untouched;
re-apply reuses the identical `/22`).

- [ ] T017 [US2] Add `lifecycle { prevent_destroy = true }` to the fabric RG in
      `infra/fabric/main.tf` (accidental-`tofu destroy` guard, research §7)
- [ ] T018 [US2] Add `azurerm_management_lock` `lock-pdp-eastus2-fabric` (`CanNotDelete`) scoped
      to the fabric RG, with a `notes` value citing the protection-removal PR (mirrors
      control-plane stack)
- [ ] T019 [US2] Create `.github/workflows/fabric-destroy.yml` (`workflow_dispatch`, input
      `destroy-confirm` must equal the region e.g. `eastus2`, concurrency group
      `tofu-fabric`, `working-directory: infra/fabric`) modeled on
      `.github/workflows/controlplane-destroy.yml` — removes the lock then destroys. In the
      workflow comment + `infra/fabric/README.md`, note the scope boundary: **peering-aware
      teardown** (US2 acceptance scenario 4 / Edge "destroy with live peerings") is a **spec
      004/006** concern — spokes own the peering and live in their own state, so within this
      spec the destroy plan has no hub-side peerings to surface (don't assume this is covered)
- [ ] T020 [US2] Extend `infra/fabric/README.md` with the protected-resource list + the
      `fabric-destroy` handoff (how to deliberately tear down), then run quickstart Scenario 7

**Checkpoint**: Fabric is destroyable-by-design yet guarded — US1 + US2 both demonstrable.

---

## Phase 5: User Story 3 — Generalize to additional regions (Priority: P3)

**Goal**: A second registered region stands up by changing only `region` + `region_index` (and
the backend key) — no module/code edits.

**Independent Test**: quickstart.md Scenario **8** (`tofu plan -var region=westus3 -var
region_index=2` yields `…-pdp-westus3-…` names and `10.2.252.0/22` with no code change).

- [ ] T021 [US3] Audit `infra/fabric/` for any hardcoded `eastus2`/`1`/`10.1.…` — everything
      MUST flow from `var.region`/`var.region_index`/`local.*`; fix any leak (the parameter
      contract of FR-016)
- [ ] T022 [US3] Run quickstart Scenario 8: `tofu plan` a second region (e.g. `westus3`,
      index 2) and confirm names/CIDRs derive correctly with zero source edits; capture the plan
- [ ] T023 [US3] Document multi-region usage in `infra/fabric/README.md`: the backend key is
      per-region (`fabrics/<region>`) and set at init/dispatch; full multi-region ergonomics are
      spec 009 (this task only proves the parameterization)

**Checkpoint**: All three stories independently demonstrable.

---

## Phase 6: Polish & cross-cutting

- [ ] T024 [P] Commit all `.terraform.lock.hcl` files (both stacks) with the pinned provider
      hashes (reproducible CI, spec-001 rule)
- [ ] T025 [P] Update `docs/spec-backlog.md` note marking spec 3 in-progress/complete and
      confirm no new CAF abbreviation/region-short rows were needed (all pre-pinned)
- [ ] T026 Full quickstart.md run end-to-end (Scenarios 1–9) on a clean apply→destroy cycle;
      record results and confirm SC-001…009 all pass

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001, T002 independent; T003 [P]. No external deps.
- **Foundational (Phase 2)**: T004 → T005; depends on T001. **Blocks T013 only** (the DNS link).
- **US1 (Phase 3)**: needs Phase 1. Internal order: T006 → T007 → T008 → {T009 [P], T010 [P]} →
  T011 → T012 → T013 (also needs Phase 2) → T014 → T015 → T016.
- **US2 (Phase 4)**: needs the fabric RG/resources from US1 (T007+). T017, T018 then T019, T020.
- **US3 (Phase 5)**: needs US1 complete (the full resource set to parameter-audit). T021 → T022
  → T023.
- **Polish (Phase 6)**: after the stories you intend to ship.

### Story independence

- **US1** is the MVP and stands alone (a working hub) — no dependency on US2/US3.
- **US2** layers protection + a destroy path onto US1's resources; independently testable via
  Scenario 7.
- **US3** is a parameterization audit + second-region plan over US1; independently testable via
  Scenario 8.

### Parallel opportunities

- T003 [P] alongside T001/T002.
- Within US1: T009 [P] (public IPs) and T010 [P] (firewall policy) run together after T008.
- T024 [P] and T025 [P] in polish.

---

## Implementation Strategy

### MVP first (US1 only)
1. Phase 1 Setup → 2. Phase 2 Foundational (shared DNS) → 3. Phase 3 US1 → **STOP & VALIDATE**
(quickstart 1–6, 9) → demo a live East US 2 hub.

### Incremental delivery
US1 (stand up) → US2 (safe teardown) → US3 (multi-region-ready) — each a shippable increment
that doesn't break the prior. Commit after each task or logical group; every apply/destroy goes
through dispatched CI (Article I/II).

## Notes

- [P] = different files, no incomplete-task dependency.
- **Prerequisite (deploy-time, not an impl task)**: East US 2 must be registered in the ledger
  (`register_region`, index 1) before a meaningful apply — the documented spec-006-staged check
  (research §4); the IaC trusts its typed `region_index`.
- Smoke-validate every AVM module under OpenTofu 1.11.x **before** reliance and record it in the
  stack README (Article V) — folded into T005 (DNS) and T015 (fabric).
- The `0.0.0.0/0 → firewall_private_ip` UDR, spoke peering, NSGs, and spoke DNS links are
  **spec 004**, not here — the fabric only exposes the outputs they consume.
