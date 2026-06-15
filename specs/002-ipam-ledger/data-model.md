# Data Model: IPAM Ledger

The ledger is two tables in the control-plane Postgres database. Identifiers are snake_case
(`EFCore.NamingConventions`); IP ranges are native `cidr` (`System.Net.IPNetwork` in .NET).
The non-overlap invariant lives in the **database** (a GiST exclusion constraint), never in
application code (FR-006).

## §1 Addressing scheme (ratified — clarify 2026-06-15)

- Base: `10.0.0.0/8`, partitioned into one **`/16` supernet per region**; **2nd octet =
  region index**.
- **Index 0 (`10.0.0.0/16`) is the platform-shared supernet** (control plane + shared infra),
  not a geographic region (research §12). Geographic regions use indices **1–255**.
- Within each region's `/16`: a fixed **hub carve-out `/22` at the top** (`10.R.252.0/22`),
  reserved/non-allocatable; **spokes** allocate from the remainder below.
- Spoke prefix: **default `/24`, permitted `/29`–`/22`**.
- The **control-plane VNet** is a seeded reservation `10.0.0.0/24` within the platform
  supernet (delegated subnet `10.0.0.0/28`).

## §2 Entity: `region_pool`

One row per registered region (and one for the platform supernet). The pool from which
allocations are carved.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | Surrogate key; used to derive the advisory-lock key. |
| `region` | `text` UNIQUE NOT NULL | Region identifier, e.g. `eastus2`; `platform` for index 0. |
| `region_index` | `smallint` UNIQUE NOT NULL | 0 = platform-shared; 1–255 = geographic. 2nd octet. |
| `supernet` | `cidr` NOT NULL | The region's `/16` (e.g. `10.1.0.0/16`). |
| `hub_carveout` | `cidr` NULL | The reserved hub `/22` (`10.R.252.0/22`); NULL for the platform pool. |
| `created_at` | `timestamptz` NOT NULL | Audit. |

**Rules**
- `supernet` is a `/16`; `region_index` equals its 2nd octet (validated on register).
- `hub_carveout` (when present) is the top `/22` of `supernet` and is **not** an allocatable
  block — it is recorded here, not in `allocation`, so it can never be released or
  double-issued.
- Regional supernets are mutually non-overlapping — enforced by an exclusion constraint on
  `supernet` (the same GiST mechanism as allocations), so overlapping region registration is
  refused at the database (FR-011, US3 scenario 3).

## §3 Entity: `allocation`

One row per live block handed out from a pool.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | Surrogate key. |
| `pool_id` | `uuid` NOT NULL FK → `region_pool(id)` | Owning pool. |
| `name` | `text` NOT NULL | Caller-supplied allocation name (e.g. spoke name). Idempotency key. |
| `network` | `cidr` NOT NULL | The allocated block (e.g. `10.1.0.0/24`). |
| `prefix_length` | `smallint` NOT NULL | Requested size; `29 ≤ prefix_length ≤ 22`-equivalent (8–1 host-bits range). |
| `kind` | `text` NOT NULL | Enumerated: `spoke` \| `reservation` (e.g. the control-plane VNet). |
| `allocated_at` | `timestamptz` NOT NULL | Audit. |

**Constraints**
- **`allocations_no_overlap`** (the core guarantee, FR-006): `EXCLUDE USING gist (pool_id
  WITH =, network inet_ops WITH &&)` — no two rows in the same pool may have overlapping
  `network`. Requires the `btree_gist` extension. Raw SQL in the migration.
- **`uq_allocation_pool_name`**: `UNIQUE (pool_id, name)` — a name is unique within its pool
  (per region). Backs idempotency (FR-010): a repeat `allocate` with the same name returns
  the existing row; a different request reusing a live name is rejected.
- `network` MUST fall within the owning pool's `supernet` and MUST NOT overlap the pool's
  `hub_carveout` (enforced by the allocator within the advisory-locked transaction; the
  exclusion constraint is the backstop against overlap with other allocations).
- `prefix_length` MUST be within the permitted range (`/29`–`/22`); validated before insert.

## §4 Lifecycle / state transitions

```
register_region ─▶ region_pool row (+ hub_carveout reserved, + supernet non-overlap checked)
allocate(name,size) ─▶ [advisory_xact_lock(pool)] first-fit aligned free block ─▶ allocation row
                       └▶ repeat with same (pool,name) ⇒ returns existing row (idempotent)
                       └▶ no free block ⇒ PoolExhausted, nothing written
release(name)        ─▶ delete allocation row (space returns to pool); idempotent if absent
query(region)        ─▶ supernet, hub_carveout, live allocations (name/size/kind), free space
```

- **No `pending`/`reserved` transient state**: allocation is a single committed insert under
  the advisory lock — there is no two-phase reserve, so there is nothing to leak on crash.
- **Release is a hard delete** of the `allocation` row (the audit trail of *who held what
  when* is a spec-006 concern — the ledger records current truth, Article III defers history
  to the provisioning-run trail). Released space is immediately reusable (FR-009).

## §5 Seed data (migration)

The initial migration seeds:
1. The **platform pool**: `region = 'platform'`, `region_index = 0`, `supernet =
   10.0.0.0/16`, `hub_carveout = NULL`.
2. The **control-plane VNet reservation**: an `allocation` in the platform pool, `name =
   'control-plane-vnet'`, `network = 10.0.0.0/24`, `kind = 'reservation'` (research §12) —
   so the platform's own VNet range is registered and Article VI holds.

East US 2 and other geographic regions are registered at runtime via `register_region` (not
seeded), since their indices are an operational choice.

## §6 Mapping notes (implementation)

- `network`/`supernet`/`hub_carveout` → `System.Net.IPNetwork`, `[Column(TypeName="cidr")]`
  (research §7).
- `kind` → C# enum mapped to text (no integer codes — readable in the DB).
- Advisory-lock key: a stable `bigint` derived from `pool_id` (e.g. hashtext/bigint hash) so
  concurrent allocators serialize per pool, parallel across pools (research §9).
- The exclusion constraint and `btree_gist` are validated against a real Postgres via
  Testcontainers (FR-016) — in-memory providers cannot enforce them.
