# Quickstart: Regional Hub Fabric — Validation

Runnable validation that the fabric meets its spec. Applies/destroys execute **only** via the
dispatched GitHub Actions stacks (never locally — Article I/II); local steps are read-only
(`tofu fmt/validate/plan`, `az ... show`, Resource Graph). Each scenario maps to a Success
Criterion (spec.md). East US 2 (`region_index = 1`, carve-out `10.1.252.0/22`) is the
worked example.

## Prerequisites
- Spec 001 rails live (state backend, naming/tags, `iac-plan`/`iac-apply`). ✅ merged.
- Spec 002: **East US 2 registered** in the ledger (`region_index = 1`, hub carve-out
  `10.1.252.0/22`). Until the spec-006 runtime can apply the schema, this is the documented
  prerequisite the owner satisfies (research §4); the `region_index` input must match it.
- `platform-dns` stack applied once (shared `privatelink.*` zones exist).
- OpenTofu 1.11.x; `az login` (read-only context).

## Setup (local, read-only)
```pwsh
cd infra/fabric
tofu fmt -check ; tofu init ; tofu validate
tofu plan -var "region=eastus2" -var "region_index=1"   # advisory; CI plan is the gate
```

---

### Scenario 1 — Single-verb stand-up, no hand-plumbing → **SC-001**
1. Merge the PR adding `infra/fabric`; `iac-apply` (matrix `stack=fabric`) applies it.
2. **Expect**: one RG `rg-pdp-eastus2-fabric` with hub VNet, firewall (Basic), bastion
   (Basic), 3 PIPs, hub→zone links — created by the single apply, no manual networking, no
   hand-entered CIDR.
```pwsh
az group show -n rg-pdp-eastus2-fabric -o table
az network firewall show -g rg-pdp-eastus2-fabric -n afw-pdp-eastus2-hub --query "sku" -o json
az network bastion show  -g rg-pdp-eastus2-fabric -n bas-pdp-eastus2-hub --query "sku" -o json
```

### Scenario 2 — Address space traces to the ledger carve-out → **SC-002**
```pwsh
az network vnet show -g rg-pdp-eastus2-fabric -n vnet-pdp-eastus2-hub --query "addressSpace.addressPrefixes" -o json
# Expect exactly ["10.1.252.0/22"] — equal to region_pool.hub_carveout for index 1; nothing else.
```
**Pass**: VNet space == `10.1.252.0/22`; subnets are the three reserved `/26`s (research §5);
no `allocation` row was created for the hub (ledger query — spec 002 `query(eastus2)`).

### Scenario 3 — Exactly one egress path / next-hop → **SC-004**, Article VII
```pwsh
az network firewall show -g rg-pdp-eastus2-fabric -n afw-pdp-eastus2-hub `
  --query "ipConfigurations[].privateIpAddress" -o json    # one private IP = the next-hop
tofu output firewall_private_ip                              # equals the above
```
**Pass**: one firewall, one `firewall_private_ip` output; no NAT gateway / second egress; no
spoke route table created here (that's spec 004).

### Scenario 4 — No public management endpoint → **SC-005**, FR-007
```pwsh
az network bastion show -g rg-pdp-eastus2-fabric -n bas-pdp-eastus2-hub --query "sku.name" -o tsv  # Basic
# Public IPs present, by purpose — only firewall data/mgmt + bastion (all platform-mandated):
az network public-ip list -g rg-pdp-eastus2-fabric --query "[].name" -o json
# Expect: pip-pdp-eastus2-afw, pip-pdp-eastus2-afw-mgmt, pip-pdp-eastus2-bas — and no other.
```
**Pass**: management reachability is the private bastion only; no public management endpoint.

### Scenario 5 — Discoverable in inventory → **SC-003**, Article III
```pwsh
az graph query -q "Resources | where type=='microsoft.resources/subscriptions/resourcegroups' and tags['pdp-managed']=='true' and tags['pdp-fabric']=='eastus2'" -o table
```
**Pass**: `rg-pdp-eastus2-fabric` returned, carrying `pdp-fabric=eastus2`.

### Scenario 6 — Private DNS shared, not duplicated → **FR-006**, clarify Q3
```pwsh
az network private-dns zone list -g rg-pdp-eastus2-dns --query "[].name" -o json   # the shared zones
az network private-dns link vnet list -g rg-pdp-eastus2-dns -z privatelink.postgres.database.azure.com --query "[].name" -o json
# Expect a hub link vnetlink-pdp-eastus2-hub-postgres; zones live in the DNS RG, not the fabric RG.
```
**Pass**: zones exist once (platform-dns RG); hub VNet is linked; no per-region duplicate zone
in the fabric RG.

### Scenario 7 — Clean, gated teardown → **SC-006/SC-007**, Article IV/VIII
1. Confirm protection: a `tofu plan -destroy` on `infra/fabric` **fails** on `prevent_destroy`
   / the lock.
2. Run **`fabric-destroy`** (`workflow_dispatch`), typed confirmation `eastus2`.
```pwsh
az group exists -n rg-pdp-eastus2-fabric          # → false
az graph query -q "Resources | where tags['pdp-fabric']=='eastus2'" -o table  # → empty
# Ledger carve-out + shared zones SURVIVE:
az network private-dns zone show -g rg-pdp-eastus2-dns -n privatelink.postgres.database.azure.com  # still present
# ledger query(eastus2) still shows hub_carveout 10.1.252.0/22 reserved.
```
**Pass**: zero residual fabric resources/PIPs/peerings; carve-out and shared zones untouched;
a re-apply reuses `10.1.252.0/22` identically (SC-007 — no address drift).

### Scenario 8 — Generalizes to a second region → **SC-008**, FR-016
```pwsh
tofu plan -var "region=westus3" -var "region_index=2"   # only the two vars change
# Expect names rg/vnet/afw/bas-pdp-westus3-*, space 10.2.252.0/22 — no module/code edits.
```
**Pass**: a second registered region plans correctly from the same code by changing only
`region`+`region_index` (and the backend key).

### Scenario 9 — Everything via dispatched CI → **SC-009**, Article I/II
**Pass**: every apply/destroy above corresponds to a GitHub Actions run (`iac-apply` matrix /
`fabric-destroy`), OIDC-authed, no local apply, no stored cloud secret.

---

## Traceability

| Scenario | SC / FR | Article |
|---|---|---|
| 1 | SC-001 | I |
| 2 | SC-002, FR-002/003 | VI |
| 3 | SC-004, FR-005 | VII |
| 4 | SC-005, FR-007 | IX |
| 5 | SC-003, FR-010 | III |
| 6 | FR-006 | — |
| 7 | SC-006/007, FR-014/015 | IV, VIII |
| 8 | SC-008, FR-016 | — |
| 9 | SC-009, FR-009 | I, II |
