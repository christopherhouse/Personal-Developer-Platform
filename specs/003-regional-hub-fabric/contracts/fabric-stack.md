# Contract: Fabric IaC Stacks

Behavioral contract for the two OpenTofu stacks this spec delivers. "MUST" items are
acceptance-testable (quickstart.md) and constitution-bound. Module versions: research §6.

## C1 — `infra/fabric` (state `fabrics/<region>`)

### Provisions (per region)
A single resource group `rg-pdp-<region>-fabric` containing: the hub VNet
`vnet-pdp-<region>-hub` (address space = the ledger hub carve-out `10.<index>.252.0/22`) with
`AzureFirewallSubnet` / `AzureFirewallManagementSubnet` / `AzureBastionSubnet`; an Azure
Firewall **Basic** (`afw-pdp-<region>-hub`) + Basic firewall policy (`afwp-pdp-<region>-hub`)
+ data & management Standard PIPs; an Azure Bastion **Basic** (`bas-pdp-<region>-hub`) + its
Standard PIP; and hub→shared-zone Private DNS VNet links. (data-model §3.)

### Invariants (MUST)
1. **Address space = ledger carve-out only.** The hub VNet space MUST equal
   `10.<region_index>.252.0/22`; the stack MUST create **no** IPAM `allocation` row and MUST
   NOT use any other CIDR (Article VI; FR-002/FR-003). `region_index` is the sole address
   input.
2. **Single egress, hub-owned.** Exactly one Azure Firewall provides egress; its **private
   IP** is exported as `firewall_private_ip` and is the only intended `0.0.0.0/0` next-hop for
   spokes (Article VII; FR-005). The stack MUST NOT create a spoke route table (spec 004).
3. **No public management endpoint.** Management access is the Basic bastion only; the stack
   MUST NOT expose any public management endpoint. The only public IPs are the two firewall
   PIPs (data+mgmt, platform-mandated) and the bastion PIP (FR-007; Article IX).
4. **Smallest viable SKUs.** Firewall = Basic, Bastion = Basic, PIPs = Standard/Static (the
   minimum Azure permits for these resources) (FR-012; Article IX).
5. **Tagged & discoverable.** The RG MUST carry `pdp-managed=true`, `pdp-deployed-by`, and
   `pdp-fabric=<region>`, discoverable via Resource Graph on `pdp-managed=='true'` (Article
   III; FR-010).
6. **AVM-first.** All compute/network resources come from pinned AVM modules (research §6);
   any primitive (e.g. the management lock, the DNS VNet links) is a thin azurerm resource
   with a recorded reason in the README (Article V; FR-011).
7. **Region-parameterized.** Changing `region`+`region_index` (and the backend key) MUST be
   sufficient to stand up another region — no module/code edits (FR-016).
8. **Cross-sub peering ready.** The hub exposes `hub_vnet_id` + `hub_resource_group_name`; the
   hub side accepts a peering from a spoke in any writable subscription (FR-008). (The peering
   itself is created by spec 004.)

### Teardown (MUST — Article IV/VIII, FR-014/FR-015)
- Running stack carries `prevent_destroy=true` (RG) **and** a `CanNotDelete` lock — accidental
  `tofu destroy` fails at plan.
- Deliberate teardown = `fabric-destroy` `workflow_dispatch` with typed confirmation = the
  region; it removes the lock and destroys the RG and all contents.
- After destroy: **zero** residual fabric resources/PIPs/links; the ledger hub carve-out and
  the platform-shared zones are **untouched** (they are not fabric-scoped).

## C2 — `infra/platform-dns` (state `platform/dns`)

### Provisions (once, region-agnostic)
`rg-pdp-eastus2-dns` holding the platform-shared `privatelink.*` Private DNS zones
(data-model §4). Universal tags only (platform scope — **no** `pdp-fabric`).

### Invariants (MUST)
1. **Global, single-set.** Exactly one set of shared zones platform-wide; no per-region
   duplicates (clarify Q3; FR-006).
2. **Owns zones, not links.** This stack owns the zones; **fabric** stacks own the hub VNet
   links. Destroying a fabric MUST NOT delete these zones.
3. **Long-lived / protected.** Holds resolution other stacks depend on; teardown is not a
   normal operation (no dedicated destroy workflow in this spec; protect like other platform
   data if/when extended).

## C3 — CI / execution plane (MUST — Article I/II/VIII)
- `fabric` and `platform-dns` added to the existing `iac-plan.yml` / `iac-apply.yml` matrix:
  `stack: [foundations, control-plane, platform-dns, fabric]`. Plan-on-PR, apply-on-merge.
- All apply/destroy run **only** in dispatched GitHub Actions via OIDC — never locally, never
  in-process. No stored cloud secrets (FR-009).
- AVM modules smoke-validated under OpenTofu 1.11.x; results recorded in each stack README
  (Article V).
