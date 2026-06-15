# Feature Specification: IPAM Ledger

**Feature Branch**: `002-ipam-ledger`

**Created**: 2026-06-15

**Input**: User description: "ipam-ledger — the platform's single source of truth for IP
address space. Stand up the control-plane Postgres database and the IPAM schema and
operations: regional supernets, hub carve-outs, and concurrency-safe allocate/release,
with non-overlap enforced by the database itself, so later specs (regional-hub-fabric,
spoke-vending) ask for space instead of hard-coding it."

> **Note on terminology**: Per the constitution (Article VI) and `docs/architecture.md`,
> the IPAM ledger is **control-plane Postgres with native `cidr` types and
> database-enforced non-overlap (a GiST exclusion constraint)**; the deployment rides the
> OpenTofu plan-on-PR / apply-on-merge rails delivered by spec 001. These are binding
> architectural constraints for this project, not implementation choices made by this
> spec — they are referenced below as fixed context. The glossary (`docs/glossary.md`)
> defines all platform terms used here (IPAM ledger, control plane, fabric, hub, spoke,
> spoke vending).

## Clarifications

### Session 2026-06-15

- Q: Global address layout and regional supernet size? → A: `10.0.0.0/8` base, one `/16`
  supernet per region (2nd octet = region index) — ~256 regions, ~256 `/24` spokes each.
- Q: Hub carve-out size and placement within the regional supernet? → A: a fixed `/22`
  (1024 addresses) reserved at the **top** of each `/16` (e.g. `10.R.252.0/22`); spokes
  allocate from the remainder below (~252 `/24` spokes/region).
- Q: Default and permitted spoke prefix sizes? → A: default `/24`; permitted range `/29`
  (8 addrs) through `/22` (1024 addrs) — a spoke may be as large as the hub carve-out.
- Q: How is an allocation request identified for retry-safety? → A: each allocation carries
  a caller-supplied name unique within its region; `allocate` is idempotent on that name
  (re-request returns the same block); reusing a live name for a different intent is rejected.
- Q: State recovery window for the control-plane database? → A: 30 days point-in-time
  recovery, matching the spec-001 PDP state backend posture.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The control-plane database exists, private and on the rails (Priority: P1)

As the platform owner, I can stand up the platform's control-plane database with a single
reviewed change through the existing CI rails — privately networked, conformant to the
naming convention and tag schema, recoverable, and destroyable — so the IPAM ledger (and
every later control-plane schema) has a durable, secure home that costs little and tears
down cleanly.

**Why this priority**: Nothing else in this spec — or in the action-layer spec that
follows — can exist without the datastore. It is the single hardest infrastructure
prerequisite, and standing it up first lets every later capability land as schema and
operations rather than new infrastructure.

**Independent Test**: Open a PR that introduces the control-plane database; confirm the
plan appears and applies on merge; verify the database is reachable only over the private
network (no public endpoint), authenticates without any stored secret, carries the
mandatory tags, and matches the naming convention; then exercise the documented teardown
and confirm nothing is orphaned.

**Acceptance Scenarios**:

1. **Given** the platform rails from spec 001 and no control-plane database, **When** the
   owner merges the change that introduces it, **Then** a privately-networked database
   exists in the platform subscription with no public endpoint, conformant naming and
   mandatory tags, and is discoverable by an inventory query over those tags.
2. **Given** the deployed database, **When** its authentication configuration is audited,
   **Then** access is identity-based (Entra ID) with no stored database password, access
   key, or connection secret anywhere in the repository, its variables, or its secret
   stores.
3. **Given** the deployed database, **When** the owner runs the documented teardown with
   explicit confirmation, **Then** the database and its resource group are removed with no
   orphaned resources, and the teardown refuses to proceed without that confirmation.

---

### User Story 2 - Address space is allocated without ever overlapping (Priority: P1)

As any caller that needs network address space (today the owner directly; later the
fabric and spoke-vending verbs), I can register a region's address pool and request a
block of a given size, and the platform reserves the next free block and records it — and
it is **impossible** for two allocations to overlap, even when many requests arrive at
once, because the database itself rejects any overlapping range.

**Why this priority**: This is the reason the spec exists. Article VI forbids inventing
address space anywhere else; a single authority with database-level non-overlap
enforcement removes the one networking failure that cannot be fixed in place. Without it,
the hub-fabric and spoke-vending specs have nothing to ask.

**Independent Test**: Against a real database instance, register a regional pool, allocate
several blocks, attempt to commit a deliberately overlapping range and confirm the
database rejects it, then run many concurrent allocation requests for the same region and
confirm every returned block is unique and non-overlapping with zero double-allocations.

**Acceptance Scenarios**:

1. **Given** a registered regional pool with free space, **When** a caller requests a
   block of an allowed size, **Then** the platform returns a single block carved from that
   pool, records it durably with the requesting consumer's identity, and the block does
   not overlap any existing allocation.
2. **Given** existing allocations in a pool, **When** any attempt is made to record a range
   that overlaps an existing allocation (whether through a faulty caller, a retry, or a
   manual edit), **Then** the database rejects it and no overlapping range is ever
   persisted.
3. **Given** a region that has not been registered, **When** a caller requests a block for
   it, **Then** the request is refused — no address space is ever invented outside a
   registered pool (Article VI).
4. **Given** many allocation requests racing for the same region, **When** they execute
   concurrently, **Then** they are serialized so that each receives a distinct,
   non-overlapping block, or fails cleanly with a clear message — never a silent collision
   or a corrupted ledger.
5. **Given** an allocation that is no longer needed, **When** the caller releases it,
   **Then** the space returns to the pool and becomes available for a future allocation,
   with the release recorded.

---

### User Story 3 - The regional addressing scheme with a reserved hub carve-out (Priority: P2)

As the platform owner, each region I bring online has a well-defined supernet from which
the regional hub takes a fixed, reserved carve-out and spokes are allocated from the
remainder — so address layout is predictable per region, the hub's space is never handed
to a spoke, and bringing up a new region is a single registration step.

**Why this priority**: It turns the generic allocator (US2) into the concrete addressing
plan the fabric and spoke-vending specs depend on. It is essential to those specs but not
to proving the non-overlap authority, so it follows US2.

**Independent Test**: Register a region per the scheme; confirm the hub carve-out is
reserved automatically and cannot be allocated to a spoke; allocate spoke blocks and
confirm they come only from the remainder; attempt to register a second region whose
supernet overlaps the first and confirm it is refused.

**Acceptance Scenarios**:

1. **Given** the addressing scheme, **When** the owner registers a new region, **Then** the
   region's supernet is recorded and the hub's carve-out is reserved within it as a
   non-allocatable block in one step.
2. **Given** a registered region, **When** spoke blocks are allocated, **Then** they are
   drawn only from the supernet minus the hub carve-out, and the carve-out is never
   returned to a spoke request.
3. **Given** an existing region, **When** a new region is registered with a supernet that
   overlaps an existing one, **Then** the registration is refused (regional supernets are
   mutually non-overlapping, by the same database guarantee as US2).

---

### User Story 4 - Allocation visibility per region (Priority: P3)

As the platform owner, I can ask what address space is allocated, to whom, and how much
remains free in each region, so I can reason about capacity, spot leaks, and answer
"what's using this range?" without inspecting any deployed network.

**Why this priority**: Visibility makes the ledger trustworthy and operable, and it feeds
later inventory and chatops answers. It is valuable but refines an authority the earlier
stories already establish.

**Independent Test**: With several allocations and one release recorded, query the ledger
and confirm it reports each live allocation with its owning consumer, the reserved hub
carve-out, and the free space remaining per region — and that a released block no longer
appears as allocated.

**Acceptance Scenarios**:

1. **Given** a region with allocations, a hub carve-out, and at least one released block,
   **When** the owner queries the ledger for that region, **Then** the result lists every
   live allocation with its owning consumer and size, distinguishes the hub carve-out, and
   reports the remaining free space.
2. **Given** the ledger, **When** it is queried across all registered regions, **Then**
   each region's pool and utilization are reported, and released space is reflected as
   free.

---

### Edge Cases

- **Pool exhaustion**: a region has no free block of the requested size. The request fails
  cleanly with an explicit "no space" result; no partial or oversized allocation is made
  and the ledger is unchanged.
- **Invalid request size**: a requested block is larger than the available pool, smaller
  than the smallest permitted size, or larger than the largest permitted size. It is
  rejected before any allocation is recorded.
- **Concurrent allocations, same region**: simultaneous requests must serialize; each gets
  a distinct block or a clean failure — never overlapping blocks, never a corrupted ledger.
- **Duplicate / retried allocation request**: a caller retries the same logical request
  (e.g., after a timeout). The platform must not leak a second block for the same intent —
  either the request is idempotent for a given consumer+intent, or the duplicate is
  rejected, so retries cannot silently consume the pool.
- **Release of an unknown or already-released block**: handled cleanly and idempotently —
  no error cascade, no double-free that could let space be handed out twice.
- **Database unreachable**: an operation cannot reach the ledger. It fails safely; no
  address is ever considered allocated without a committed ledger record, and no caller
  proceeds to create a network on an unrecorded range.
- **Teardown with live allocations**: destroying the database destroys the ledger and every
  recorded allocation. This is a destructive operation and MUST require explicit human
  confirmation (Article VIII); the consequence (loss of the allocation record of record)
  is stated in the teardown documentation.
- **Region supernet overlap on registration**: registering a region whose supernet overlaps
  an existing region is refused by the same non-overlap guarantee.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The platform MUST provide a control-plane database in the platform
  subscription, deployed through the spec-001 rails (plan on PR, apply on merge, OIDC
  auth, its own state under a `platform/`-scoped key in the state-key registry). The
  deployment MUST be framed as the reusable **control-plane database**; IPAM is its first
  schema, and later specs add schemas to the same database rather than standing up new
  datastores.
- **FR-002**: The control-plane database MUST have no public endpoint — it is reachable
  only over the platform's private network (Article IX, private by default). External
  administrative access for the owner, if any, MUST be over a private/identity-gated path,
  not a public one.
- **FR-003**: Access to the database MUST be identity-based (Entra ID) with **zero stored
  secrets** — no database passwords, access keys, SAS tokens, or connection secrets in the
  repository, its variables, or its secret stores (consistent with the spec-001
  no-stored-secrets posture).
- **FR-004**: Every resource created by this feature MUST conform to the published naming
  convention and tag schema (`docs/conventions.md`) and MUST be discoverable by an
  inventory query over the mandatory tags (Article III).
- **FR-005**: The IPAM ledger MUST model, at minimum: **regional pools** (a region's
  supernet), **allocations** (blocks carved from a pool, each attributed to an owning
  consumer), and the **hub carve-out** (a reserved, non-allocatable block within a region's
  pool). Address ranges MUST be stored as native network/CIDR values.
- **FR-006**: Non-overlap MUST be enforced by the database itself (a GiST exclusion
  constraint), not by application logic. It MUST be impossible to commit two overlapping
  ranges within the address space the constraint governs — under any caller, any retry, any
  concurrency — full stop.
- **FR-007**: The platform MUST provide an **allocate** operation: given a registered region
  and a requested block size, it reserves the next free block of that size from the region's
  pool (excluding the hub carve-out), records it durably with the requesting consumer's
  identity, and returns it. If no free block of the requested size exists, it MUST fail
  cleanly without recording anything.
- **FR-008**: Concurrent allocation requests against the same pool MUST be serialized (via
  advisory locking) so that concurrent callers either receive distinct, non-overlapping
  blocks or fail cleanly — never a double-allocation and never a corrupted ledger.
- **FR-009**: The platform MUST provide a **release** operation that returns a previously
  allocated block to its pool, records the release, and makes the space available for future
  allocation. Release MUST be idempotent and MUST NOT permit a double-free that could hand
  the same space out twice.
- **FR-010**: Each allocation MUST carry a caller-supplied **name that is unique within its
  region**, and `allocate` MUST be **idempotent on that name**: a repeated request with the
  same name returns the existing block rather than allocating a new one, so timeouts and
  retries cannot silently leak a second block or exhaust a pool. A request that reuses a live
  name for a different size or intent MUST be rejected. The hub carve-out occupies a reserved
  name (e.g. `hub`) within its region.
- **FR-011**: The platform MUST provide a **register-region** operation that records a
  region's supernet and reserves its hub carve-out as a non-allocatable block in one step.
  Registering a region whose supernet overlaps an already-registered region MUST be refused.
- **FR-012**: Allocation MUST be refused for any region that has not been registered — no
  address space is ever assigned outside a registered pool (Article VI). No VNet/subnet range
  may originate anywhere other than this ledger.
- **FR-013**: The ledger MUST be queryable to report, per region: the registered supernet,
  the reserved hub carve-out, every live allocation with its owning consumer and size, and
  the remaining free space; released blocks MUST NOT appear as allocated.
- **FR-014**: State data in the control-plane database MUST be recoverable via point-in-time
  restore within a **30-day** retention window (covering accidental deletion, corruption, or
  bad write), matching the spec-001 PDP state backend posture and consistent with the
  cheap-by-default principle (Article IX); the recovery capability and window MUST be
  documented.
- **FR-015**: The control-plane database and its contents MUST tear down cleanly with no
  orphans (Article IV). Because teardown destroys the ledger of record, it MUST require
  explicit human confirmation (Article VIII), and the documentation MUST state the
  consequence and any protection applied to prevent accidental destruction.
- **FR-016**: The IPAM allocator's correctness — specifically the database-enforced
  non-overlap and the concurrency safety — MUST be validated against a real database
  instance (the exclusion constraint and advisory locking cannot be verified in-memory), per
  the constitution's testing discipline.
- **FR-017**: The ledger's operations and contracts (allocate, release, register-region,
  query) MUST be consumable by the later action-layer, hub-fabric, and spoke-vending specs
  without modifying this spec's schema or addressing contract. This spec delivers the data
  authority and its operations only; the typed verbs, CLI, and MCP surface that wrap them are
  out of scope (action-layer spec) as is any VNet/subnet creation (hub-fabric / spoke-vending).

### Key Entities

- **Control-plane database**: the platform's durable, privately-networked datastore in the
  platform subscription; home to the IPAM ledger and every later control-plane schema; the
  only foundation component beyond the state backend with deliberate destroy protection.
- **Regional pool (supernet)**: the address supernet assigned to one region; the root from
  which that region's hub carve-out and spoke allocations are drawn. Regional supernets are
  mutually non-overlapping.
- **Allocation**: a block of address space carved from a regional pool and attributed to an
  owning consumer (a fabric hub, a spoke, or a future workload need); the unit the ledger
  hands out and reclaims.
- **Hub carve-out**: a fixed, reserved, non-allocatable block within a region's pool, held
  for the regional hub so it is never handed to a spoke.
- **Consumer / allocation name**: the caller-supplied name a block is allocated for (e.g. a
  spoke name, or `hub` for the carve-out), unique within its region; the idempotency key for
  retry-safe allocation and the human-meaningful answer to "what is using this range?".
- **Non-overlap guarantee**: the database-level invariant (exclusion constraint) that makes
  overlapping ranges impossible to persist; the property every later networking spec relies
  on.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Under at least 100 allocation requests racing against the same region, 100% of
  returned blocks are distinct and non-overlapping, with zero double-allocations and zero
  ledger corruption.
- **SC-002**: Every attempt to persist an overlapping range is rejected — across 0 observed
  exceptions, no overlapping ranges ever exist in the ledger.
- **SC-003**: A caller can obtain a valid, non-overlapping block for a registered region
  effectively instantly (well under one second under normal load), and the result is durably
  recorded before it is returned.
- **SC-004**: 100% of released blocks become available for future allocation (released space
  is fully reclaimed), and a released block never appears as a live allocation.
- **SC-005**: The owner can answer "what is allocated, to whom, and what is free in region
  X?" from a single query, for every registered region.
- **SC-006**: A credentials audit finds zero stored database secrets; the database is
  confirmed to have no reachable public endpoint.
- **SC-007**: Tearing down the control-plane database leaves no orphaned resources, and the
  teardown cannot proceed without explicit confirmation.
- **SC-008**: The next networking specs (regional-hub-fabric, spoke-vending) can request,
  release, and query address space using this ledger's operations without modifying its
  schema, addressing scheme, or the rails this and spec 001 deliver.

## Assumptions

- **Spec 001 is complete** (merged): the repository layout, OpenTofu version pins, PDP state
  backend, naming convention, tag schema, and CI rails (plan on PR / apply on merge via OIDC,
  branch protection) all exist and are the rails this spec rides. This spec adds neither new
  rails nor new conventions.
- **Region and initial scope**: the control-plane database and the first registered region
  are **East US 2**, consistent with the charter and spec 001. The addressing scheme must not
  hard-code East US 2 — later regions register through the same operation.
- **Addressing plan (ratified — `/speckit-clarify` session 2026-06-15)**:
  - Private RFC 1918 space `10.0.0.0/8` partitioned into one **`/16` supernet per region**
    (2nd octet = region index), with the region→supernet mapping recorded in the ledger.
    **Region index 0 (`10.0.0.0/16`) is reserved for platform-shared infrastructure** (the
    control-plane VNet — see Key Entities / plan §Constitution Check note); geographic regions
    use indices **1–255**. East US 2 is the first registered geographic region. The specific
    region→index assignment is operator-supplied at `register_region` time; a canonical
    region→index registry is deferred (a spec-003 concern) — for now the ledger's
    supernet-overlap refusal and idempotent registration prevent collisions.
  - A fixed **hub carve-out** of **`/22`** (1024 addresses) reserved at the **top** of each
    regional `/16` (e.g. `10.R.252.0/22`), held for hub subnets (egress/firewall,
    management/bastion, gateway, DNS resolver, shared services) and never allocatable to a
    spoke.
  - **Spoke blocks** allocated from the remainder of the `/16` below the carve-out, **default
    `/24`**, **permitted `/29`–`/22`** (a spoke may be as large as the hub carve-out).
  The storage decision (Postgres ledger with database-enforced non-overlap) and these
  numbers are now settled; the functional requirements remain written agnostic to the exact
  prefix sizes so the plan can encode them as configuration.
- **Consumers are platform-internal**: callers of allocate/release/register today are the
  owner and, later, the platform's own verbs and execution-plane workflows — not external
  tenants. Multi-tenant access control is out of scope.
- **No VNet creation here**: this spec records and reclaims address space only; turning an
  allocation into an actual VNet/subnet/peering belongs to regional-hub-fabric and
  spoke-vending.
- **No verb / CLI / MCP surface here**: the typed action layer that wraps these operations
  is the action-layer spec (#6). This spec's operations are exercised directly (and by tests)
  against the database; the operational interface contract is defined so the action layer can
  wrap it unchanged.
- **Live-ledger application boundary**: this spec **deploys the empty private control-plane
  database** (US1) with the `BTREE_GIST` extension allow-listed, and **authors + proves the
  schema, constraints, and allocator against a real Postgres** via Testcontainers (US2–US4,
  FR-016). Because the deployed database is private and Entra-only, nothing in this spec
  reaches it from CI. **Applying the schema to the live Azure database, and operating the
  ledger against it, binds to spec 006** (the action-layer runtime runs inside the VNet). US1
  satisfies "the database exists"; the production ledger becomes *operable* in spec 006. This
  is a scope boundary, not a deferred requirement — every FR is fully met here (against
  Testcontainers for the data-layer FRs, against the deployed server for the infra FRs).
- **IPv4 only** for the addressing scheme; IPv6 is out of scope for this spec.
- **Cost posture**: smallest viable database SKU consistent with the durability and recovery
  requirements (Article IX); the database is expected to be inexpensive to keep running and
  cheap to recreate if torn down.
