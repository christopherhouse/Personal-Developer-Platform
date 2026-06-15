# Contract: IPAM Ledger Operations

The four operations the ledger exposes. This is the contract the action-layer (spec 006),
regional-hub-fabric (003), and spoke-vending (004) consume **without modifying the schema or
addressing scheme** (FR-017). Operations are defined behaviorally here; their .NET signatures
live in `src/Pdp.ControlPlane.Ipam`. They are NOT verbs/CLI/MCP yet (that is spec 006).

All operations are transactional. Allocation-mutating operations take a per-pool
transaction-scoped advisory lock (`pg_advisory_xact_lock`) so concurrent callers serialize
per pool; the `allocations_no_overlap` GiST exclusion constraint is the absolute backstop.

## `register_region(region, region_index)`

Registers a region's `/16` supernet and reserves its hub `/22` carve-out in one step.

- **Input**: `region` (text, e.g. `eastus2`), `region_index` (1–255).
- **Derives**: `supernet = 10.<region_index>.0.0/16`, `hub_carveout = 10.<region_index>.252.0/22`.
- **Success**: a `region_pool` row exists with the supernet and reserved carve-out.
- **Idempotent**: re-registering the same `region` with the same index is a no-op success.
- **Errors**: `RegionAlreadyExists` (same region, different index); `SupernetOverlap`
  (index/supernet collides with an existing region — refused by the DB exclusion constraint,
  FR-011); `InvalidRegionIndex` (0 is reserved for the platform pool; outside 1–255).

## `allocate(region, name, prefix_length = 24) → Allocation`

Reserves the next free block of the requested size from the region's pool.

- **Input**: `region`, `name` (caller-supplied, unique within the region), `prefix_length`
  (default `24`; permitted `29`–`22`, i.e. `/29`–`/22`).
- **Behavior**: under the pool's advisory lock, **first-fit lowest aligned free block**
  within `supernet − (hub_carveout + existing allocations)`; insert and return it.
- **Idempotent on `name`** (FR-010): if `(pool, name)` already exists, return the existing
  allocation unchanged (a safe retry). A request whose `name` exists but with a **different**
  `prefix_length`/intent is rejected (`AllocationNameConflict`).
- **Errors**: `RegionNotRegistered` (no pool — Article VI, never invent space, FR-012);
  `PoolExhausted` (no free aligned block of that size — nothing written, FR-007);
  `InvalidPrefixLength` (outside `/29`–`/22`); `AllocationNameConflict`.
- **Guarantee**: the returned `network` overlaps nothing else in the pool — ever (FR-006,
  SC-001/SC-002).

## `release(region, name)`

Returns a previously allocated block to its pool.

- **Input**: `region`, `name`.
- **Behavior**: deletes the `(pool, name)` allocation; the space is immediately reusable.
- **Idempotent** (FR-009): releasing an unknown/already-released name is a clean no-op
  success — never a double-free that could hand the same space out twice.
- **Errors**: `RegionNotRegistered`. Releasing the hub carve-out is **not possible** — it is
  not an `allocation` row (data-model §2); attempting to release reserved names
  (`kind = reservation`) is refused (`CannotReleaseReservation`).

## `query(region) → RegionView` / `query_all() → RegionView[]`

Reports utilization without touching any deployed network (no lock needed; read-only).

- **Output per region**: `supernet`, `hub_carveout`, every live `allocation`
  (`name`, `network`, `prefix_length`, `kind`, `allocated_at`), and **free space**
  (the pool minus carve-out minus live allocations, summarized as remaining blocks/addresses).
- Released blocks do not appear as allocated (SC-004/SC-005).

## Cross-cutting guarantees

| Guarantee | Mechanism | Requirement |
|---|---|---|
| No overlapping ranges, ever | `EXCLUDE USING gist (pool_id WITH =, network inet_ops WITH &&)` | FR-006, SC-002 |
| Concurrency-safe allocation | `pg_advisory_xact_lock(pool)` + GiST backstop | FR-008, SC-001 |
| Retry-safe allocation | `UNIQUE (pool_id, name)` + idempotent return | FR-010 |
| No address outside a registered pool | `register_region` precondition | FR-012 (Article VI) |
| Released space reclaimed | hard delete of the allocation row | FR-009, SC-004 |
