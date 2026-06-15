# Quickstart: Validating the IPAM Ledger

End-to-end validation. Each scenario maps to a success criterion (SC) from [spec.md](spec.md).
Infrastructure scenarios ride the spec-001 CI rails; ledger-behavior scenarios run as
integration tests against a real Postgres (Testcontainers) per FR-016 — see
[contracts/ipam-operations.md](contracts/ipam-operations.md) and [data-model.md](data-model.md).

## Prerequisites

- Spec 001 complete: PDP state backend, naming/tags, CI rails, branch protection.
- Azure platform subscription, Owner RBAC, `az login`; `gh` authenticated.
- .NET 10 SDK per `global.json`; Docker available (Testcontainers); pinned OpenTofu.

## Scenario 1 — Control-plane database deployed, private & on the rails (SC-006, SC-007; US1)

1. Open a PR introducing `infra/control-plane/`; confirm `iac-plan` shows the VNet,
   delegated subnet, private DNS zone + link, and the Flexible Server (creates only). Merge →
   `iac-apply` lands it.
2. **Expect**: `psql-pdp-eastus2-controlplane` exists with `public_network_access = Disabled`
   (no public endpoint), Entra-only auth (no `administrator_login`; `gh secret list` and the
   stack show zero DB secrets — SC-006), `backup_retention_days = 30`, `B_Standard_B1ms`,
   `azure.extensions` includes `BTREE_GIST`; the RG carries `pdp-managed` + `pdp-deployed-by`
   and is returned by the Resource Graph inventory query; names match `docs/conventions.md`.
3. **Teardown drill** (SC-007): run `controlplane-destroy` *without* removing protection →
   blocked by the documented protection; the blocked list matches
   `infra/control-plane/README.md`. (Full teardown is the destructive variant — owner-only.)

## Scenario 2 — Non-overlap is impossible (SC-002; US2)

Run the ledger integration tests. **Expect**: after registering a region and allocating
several blocks, a deliberate attempt to insert an overlapping `cidr` in the same pool is
rejected by `allocations_no_overlap` (the GiST exclusion constraint) — the test asserts the
database raises, and no overlapping range is ever persisted.

## Scenario 3 — Concurrency safety (SC-001; US2)

**Expect**: firing ≥100 concurrent `allocate` calls at the same region yields 100 distinct,
non-overlapping blocks (or clean `PoolExhausted` failures), zero double-allocations, zero
corruption — the advisory lock serializes per pool and the GiST constraint is the backstop.

## Scenario 4 — Allocate / release / idempotency (SC-003, SC-004; US2)

**Expect**: `allocate` returns a valid non-overlapping block effectively instantly (<1s) and
the row is committed before return; repeating `allocate` with the same `name` returns the
**same** block (idempotent retry); a different request reusing a live name is rejected;
`release` removes the block and a subsequent `allocate` reuses the freed space (100%
reclaim); releasing an unknown name is a clean no-op.

## Scenario 5 — Regional scheme & hub carve-out (US3)

**Expect**: `register_region("eastus2", N)` records `10.N.0.0/16` and reserves
`10.N.252.0/22` as the hub carve-out in one step; spoke allocations come only from below the
carve-out and never return the carve-out; registering a second region whose supernet overlaps
an existing one is refused by the database (US3 scenario 3).

## Scenario 6 — Allocation visibility (SC-005; US4)

**Expect**: `query("eastus2")` reports the supernet, the reserved hub carve-out, every live
allocation with its owning `name`/size/kind, and the remaining free space; a released block
no longer appears as allocated. `query_all()` reports every registered region including the
platform pool and its `control-plane-vnet` reservation.

## Scenario 7 — Rails ready for specs 003/004 (SC-008)

**Expect**: the operation contract ([contracts/ipam-operations.md](contracts/ipam-operations.md))
covers everything regional-hub-fabric and spoke-vending need (register, allocate hub +
spokes, release, query) with no schema or addressing change. **Note the handoff** (research
§13): the live Azure ledger becomes *operable* in spec 006 when an in-VNet runtime applies the
migrations; until then the schema/allocator are proven against Testcontainers and the empty
private server is deployed with `BTREE_GIST` allow-listed.
