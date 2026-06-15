# Contract: Fabric Interfaces (upstream IPAM, downstream spokes/control-plane)

The fabric's typed boundary with the rest of the platform. Stable names here are what spec
004 (spoke vending) and spec 006 (action layer) bind to.

## I1 — Upstream: IPAM ledger (spec 002) → fabric

The fabric **consumes** the region's reserved hub carve-out; it never allocates.

| Item | Source | Contract |
|---|---|---|
| `region_index` | `region_pool.region_index` (set by `register_region`) | The region MUST be registered before the fabric deploys (FR-004). Index ∈ [1,255]. |
| Hub carve-out | `region_pool.hub_carveout` = `10.<index>.252.0/22` | Deterministic top-`/22` of the region `/16` (spec 002 data-model §1). The fabric derives this from `region_index`; it MUST match the ledger record exactly. |
| Allocation rows | — | The fabric creates **none**. The carve-out is a standing reservation, not an `allocation` (FR-003). |

**Enforcement point**: the *live* "is `region` registered?" check is the **control plane's**
(spec 006) — it queries the private, Entra-only ledger and only then dispatches the fabric
workflow with `region`+`region_index` (research §4). At the IaC layer the registered index is
a typed input; standalone applies trust it (as all PDP stacks trust their typed inputs).

## I2 — Downstream: fabric → spoke vending (spec 004) & control plane (spec 006)

Exposed as OpenTofu **outputs** (data-model §5), read by spec 004 via remote state / by spec
006 via the dispatch/inventory path:

| Output | Spec 004 / 006 use | Stability |
|---|---|---|
| `hub_vnet_id` | Create the **hub side** of cross-sub spoke peering. | Stable name. |
| `hub_resource_group_name` | Locate the hub for peering & inventory. | Stable. |
| `firewall_private_ip` | Spoke `0.0.0.0/0` UDR next-hop = this IP (Article VII egress). | Stable name; value may change if the firewall is re-allocated — consumers read it live, never hardcode. |
| `shared_dns_zone_ids` | Link **spoke** VNets to the same shared zones at vending. | Stable map keys (`postgres`/`blob`/`kv`, grows by PR). |
| `hub_address_space` | Inventory / overlap sanity (SC-002 trace). | Stable. |

**Non-goals of this contract** (later specs): the spoke side of peering, route tables/UDRs,
NSGs, spoke DNS links (all spec 004); hub-to-hub connectivity (spec 009); the verb/CLI/MCP
that invoke deploy/destroy (specs 006/007).

## I3 — Egress posture guarantee (Article VII)

The fabric guarantees a **single** egress path per region (the Basic firewall) and exposes
**one** next-hop (`firewall_private_ip`). Spokes will point their default route at it; the
fabric does not (and spokes must not) provide any alternative egress. This is the structural
backing for FR-005 and SC-004.
