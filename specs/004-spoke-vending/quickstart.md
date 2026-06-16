# Quickstart: Spoke Vending — Validation

Runnable validation that the spoke stack meets its spec. Applies/destroys execute **only** via the
dispatched workflows (`spoke-vend` / `spoke-destroy`) — never locally (Articles I/II); local steps
are read-only (`tofu fmt/validate/plan`, `az ... show`, Resource Graph). Each scenario maps to a
Success Criterion. Worked example: `westus3` (`region_index = 2`), spoke `app1`, `spoke_cidr =
10.2.16.0/24`, in target subscription `<sub>`.

## Prerequisites
- Specs 001–003 live; the **westus3 fabric** deployed (exposes `hub_vnet_id`,
  `firewall_private_ip`, `shared_dns_zone_ids`).
- The deploy identity has Contributor on the **target** subscription and Network Contributor on the
  hub RG + Private DNS Zone Contributor on the DNS RG in the **platform** subscription (I3).
- A `spoke_cidr` chosen within `10.2.0.0/16`, clear of `10.2.252.0/22`, **and not overlapping any
  existing spoke** (typed allocation; the stack validates region containment + hub-carve-out
  avoidance, but cross-spoke non-overlap is operator-checked until spec 006's allocator enforces it
  — FR-008/FR-016).

## Setup (local, read-only)
```pwsh
cd infra/spoke
tofu fmt -check ; tofu init -backend-config="key=spokes/<sub>/app1" ; tofu validate
tofu plan -var region=westus3 -var region_index=2 -var target_subscription_id=<sub> `
  -var spoke_name=app1 -var spoke_cidr=10.2.16.0/24    # advisory; CI plan is the gate
```

---

### Scenario 1 — Single-verb vend → **SC-001**
Dispatch `spoke-vend` (region=westus3, sub=<sub>, name=app1, cidr=10.2.16.0/24). **Expect** one RG
`rg-pdp-westus3-spoke-app1` with a VNet, NSG-protected subnet(s), a route table, both peerings, and
DNS links — from one dispatch, no manual networking.
```pwsh
az group show -n rg-pdp-westus3-spoke-app1 --subscription <sub> -o table
az network vnet peering list -g rg-pdp-westus3-spoke-app1 --vnet-name vnet-pdp-westus3-app1 --subscription <sub> -o table
```

### Scenario 2 — Address traces to the region /16 → **SC-002**
```pwsh
az network vnet show -g rg-pdp-westus3-spoke-app1 -n vnet-pdp-westus3-app1 --subscription <sub> --query "addressSpace.addressPrefixes" -o json
# Expect ["10.2.16.0/24"] — within 10.2.0.0/16, outside 10.2.252.0/22.
```

### Scenario 3 — Single egress via the hub → **SC-004**, Article VII
```pwsh
az network route-table show -g rg-pdp-westus3-spoke-app1 -n rt-pdp-westus3-app1 --subscription <sub> --query "routes[?addressPrefix=='0.0.0.0/0'].{nextHop:nextHopType,ip:nextHopIpAddress}" -o json
# Expect one route: VirtualAppliance → the fabric's firewall_private_ip; no other default route.
```

### Scenario 4 — Every subnet has an NSG → **SC-005**, FR-005
```pwsh
az network vnet subnet list -g rg-pdp-westus3-spoke-app1 --vnet-name vnet-pdp-westus3-app1 --subscription <sub> --query "[].{name:name, nsg:networkSecurityGroup.id}" -o json
# Expect every subnet to have a non-null nsg.
```

### Scenario 5 — Many spokes per subscription, independent → **SC-003**
Vend `app2` (cidr=10.2.17.0/24) into the **same** subscription; confirm both exist, non-overlapping,
each peered; then re-vend `app1` (converges, no duplicate) and confirm `app2` untouched.

### Scenario 6 — Discoverable in inventory → **SC-007**, Article III
```pwsh
az graph query -q "Resources | where type=='microsoft.resources/subscriptions/resourcegroups' and tags['pdp-spoke']=='app1'" -o table
```

### Scenario 7 — Clean teardown, both peerings gone → **SC-006**, Articles IV/VIII
Dispatch `spoke-destroy` (typed confirm `app1`). **Expect**: `rg-pdp-westus3-spoke-app1` gone; the
**hub-side** peering `peer-hub-to-app1` removed from the hub VNet (no dangling peering); `app2`, the
hub, and the shared zones untouched; the `10.2.16.0/24` block reusable.
```pwsh
az group exists -n rg-pdp-westus3-spoke-app1 --subscription <sub>          # → false
az network vnet peering list -g rg-pdp-westus3-fabric --vnet-name vnet-pdp-westus3-hub --query "[?name=='peer-hub-to-app1']" -o json  # → []
```

### Scenario 8 — Generalizes by parameters → **SC-008**
Vend into a second subscription and/or region by changing only inputs; confirm correct names,
address space, hub, and DNS with no source edits.

### Scenario 9 — Everything via dispatched CI → **SC-009**, Articles I/II
Every vend/destroy corresponds to a `spoke-vend`/`spoke-destroy` run, OIDC into both subs, no local
apply, no stored secret.

---

## Traceability

| Scenario | SC / FR | Article |
|---|---|---|
| 1 | SC-001 | I |
| 2 | SC-002, FR-016 | VI |
| 3 | SC-004, FR-004 | VII |
| 4 | SC-005, FR-005/018 | IX |
| 5 | SC-003, FR-007/009 | — |
| 6 | SC-007, FR-012 | III |
| 7 | SC-006, FR-013/014 | IV, VIII |
| 8 | SC-008, FR-010 | — |
| 9 | SC-009, FR-015 | I, II |
