# Feature Specification: Environment Inventory

**Feature Branch**: `005-environment-inventory`

**Created**: 2026-06-17

**Status**: Draft

**Input**: User description: "Build environment-inventory: the platform's live, authoritative answer to 'what does PDP manage, and where?' — derived entirely from Azure Resource Graph over the mandatory pdp-* tag schema across every accessible subscription, never from local records or OpenTofu state files (constitution Article III; glossary 'Inventory')."

## Overview

Inventory is PDP's live, authoritative answer to **"what does PDP manage, and where?"** It is
derived **only** from Azure Resource Graph over the mandatory `pdp-*` tag schema
(`docs/conventions.md` §2), across **every** subscription the platform's read identity can see —
never from local records, the IPAM ledger, or OpenTofu state (constitution Article III; glossary
*Inventory*). It discovers subscriptions at runtime, finds every resource group tagged
`pdp-managed == 'true'`, classifies each into the platform taxonomy (regional **fabrics**,
**spokes**, **workloads** grouped into **environments**), answers the charter's headline questions,
returns typed structured results for both humans and the later verb/CLI/MCP layers (specs 006/007),
and surfaces **tag-conformance drift** between tagging intent and reality.

This feature is **read-only**: it performs no Azure mutations and creates no managed infrastructure
of its own.

## Clarifications

### Session 2026-06-17

- Q: Which identity performs cross-subscription Resource Graph reads (the read-only spec's only possible RBAC footprint)? → A: The owner's local credential for the demonstrable surface; the component accepts a pluggable credential so spec 006's control-plane identity injects later. **This spec provisions no new Azure identity or RBAC.**
- Q: Does this spec do tag-side drift only, or also pull a slice of the spec-006 registry forward to detect "in registry but not in Azure"? → A: **Tag-side drift only** (orphan, conformance, invisible, ambiguous — all from Resource Graph); registry↔Azure reconciliation is deferred to spec 006.
- Q: Is the deliverable a pure component, or an independently demonstrable surface? → A: **Both** — a reusable inventory component plus a minimal read-only surface (thin command / test harness) to exercise it; it does **not** implement the 006/007 verb/CLI/MCP.
- Q: Does inventory classify at resource-group granularity only, or drill into individual resources within each managed RG? → A: **Resource-group-level only**; the `pdp-*` tag schema lives on RGs, and "what's in environment X" returns the managed RGs.
- Q: Are drift findings informational, or a blocking pass/fail signal? → A: **Informational only** — drift findings are part of the structured result; inventory itself always succeeds and never fails on drift. Consumers (and Azure Policy in spec 010) decide how to act.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Live, classified inventory across all accessible subscriptions (Priority: P1) 🎯 MVP

The owner asks "what does PDP manage, and where?" The platform discovers every subscription its read
identity can see, queries Azure Resource Graph for every resource group tagged
`pdp-managed == 'true'`, and classifies each into the taxonomy — regional fabrics, spokes, and
workloads grouped into environments — reporting each item with its owning subscription and region.
The answer is computed live from Azure, with no local records consulted.

**Why this priority**: This is the core capability and the charter's headline promise (Article III:
"what do I have deployed?" must be answerable accurately in one turn). It is the foundation every
other story and every later consumer (specs 006/007) builds on, and on its own it delivers a usable,
demoable answer.

**Independent Test**: Against the live platform (a deployed fabric in `westus3` plus the vended
spokes from spec 004), run inventory and confirm every `pdp-managed` resource group appears,
correctly classified by type, with its subscription and region — without any other story.

**Acceptance Scenarios**:

1. **Given** a deployed fabric and one or more vended spokes across one or more subscriptions,
   **When** the owner queries inventory, **Then** every resource group tagged `pdp-managed == 'true'`
   in every accessible subscription is returned, each classified as fabric, spoke, or workload, and
   each labeled with its owning subscription and region.
2. **Given** the inventory result, **When** workloads are present, **Then** they are grouped into
   environments by their `pdp-env` tag, and "what environments do I have deployed?" is answerable
   directly from the result.
3. **Given** a freshly vended spoke (or a newly deployed fabric), **When** inventory is re-run,
   **Then** the new resource group appears and is correctly classified with **no code or
   configuration change** — discovery is purely tag/Graph-driven.
4. **Given** subscriptions the read identity cannot access, **When** inventory runs, **Then** those
   subscriptions are reported as inaccessible/skipped rather than silently omitted (coverage is
   honest).

---

### User Story 2 - Scoped answers to the headline questions (Priority: P2)

The owner asks narrower questions — "which spokes exist, in which subscription and region?" and
"what is in environment X?" — and gets a filtered, correct slice of the live inventory without
re-deriving it by hand.

**Why this priority**: The charter names these specific questions as the value of inventory. They
build directly on US1's classified result and are how the owner (and later the chatops layer)
actually uses inventory day-to-day.

**Independent Test**: With several spokes across subscriptions/regions and workloads in at least one
named environment, query "spokes by subscription and region" and "contents of environment X" and
confirm each returns exactly the matching items.

**Acceptance Scenarios**:

1. **Given** spokes vended across more than one subscription and/or region, **When** the owner asks
   "which spokes exist and where?", **Then** each spoke is listed with its name, owning subscription,
   and region.
2. **Given** workloads tagged into environment `X`, **When** the owner asks "what is in environment
   X?", **Then** exactly the resource groups belonging to environment `X` are returned and nothing
   from other environments.
3. **Given** a query for an environment, region, or subscription with no matching managed resources,
   **When** it is run, **Then** an empty result is returned cleanly (not an error).

---

### User Story 3 - Surface tag-conformance drift (Priority: P2)

The owner asks inventory to flag where reality diverges from tagging intent: resource groups that
look PDP-managed but are mis-tagged, unclassifiable, or invisible. Each finding identifies the
resource group and the kind of drift, so the owner can fix the owning stack's IaC.

**Why this priority**: "Untagged = invisible = a bug" is constitutional (Article III). An inventory
that silently drops mis-tagged resources would hide exactly the failures it exists to catch. Drift
surfacing is what makes the inventory trustworthy, but it is a layer on top of the core classified
view (US1).

**Independent Test**: Deliberately mis-tag a resource group (or create a `rg-pdp-*`-named group with
no `pdp-managed` tag), run inventory, and confirm it is flagged as drift with the correct category —
while correctly tagged resources and the owner-managed seed backend are **not** flagged.

**Acceptance Scenarios**:

1. **Given** a resource group tagged `pdp-managed == 'true'` that carries **no** scope tag (or one
   that cannot be classified into fabric/spoke/workload), **When** inventory runs, **Then** it is
   reported as an **orphan** drift finding, not silently dropped.
2. **Given** a managed resource group whose tag values violate `docs/conventions.md` (e.g., an
   invalid region for `pdp-fabric`, a malformed `pdp-spoke`/`pdp-workload`/`pdp-env` value, a
   `pdp-deployed-by` outside the allowed set, or a workload resource group missing `pdp-env`),
   **When** inventory runs, **Then** it is reported as a **conformance** drift finding identifying
   the offending tag.
3. **Given** a resource group whose name matches the PDP convention (`rg-pdp-*`) but which lacks
   `pdp-managed == 'true'`, **When** inventory runs, **Then** it is flagged as **invisible**
   (looks-managed-but-untagged) drift — the Article III failure mode.
4. **Given** a resource group carrying **conflicting** scope tags (e.g., both `pdp-fabric` and
   `pdp-spoke`), **When** inventory runs, **Then** it is flagged as **ambiguous** drift.
5. **Given** the owner-managed seed backend (`RG-TF` / `cmhtfstatesa`), which is explicitly out of
   PDP scope, **When** inventory runs, **Then** it is **not** reported as managed and **not** flagged
   as drift.

---

### User Story 4 - Typed, structured results for programmatic consumers (Priority: P3)

The inventory result is returned as typed, structured data — not just formatted text — so the
verb/CLI/MCP layers (specs 006/007) can consume it directly, while the same data also renders for a
human reader.

**Why this priority**: Inventory exists to be consumed by later layers; a structured contract is what
lets spec 006 build on it without re-parsing. It is an extension of the core result shape rather than
a precondition for the owner getting answers, so it ranks below US1–US3.

**Independent Test**: Have a minimal programmatic consumer read the inventory result and assert on
the taxonomy, per-item subscription/region, environment grouping, and drift findings — proving the
result is machine-consumable, not display-only.

**Acceptance Scenarios**:

1. **Given** an inventory run, **When** a programmatic consumer reads the result, **Then** it
   receives a typed structure exposing environments, fabrics, spokes, workloads (each with
   subscription and region), and drift findings (each with a category) — without scraping
   human-formatted text.
2. **Given** the same result, **When** rendered for a human, **Then** it presents the same taxonomy
   and drift findings in a readable form derived from the same structured data.

---

### Edge Cases

- **Inaccessible subscription**: a subscription the read identity cannot read is reported as
  skipped/inaccessible (coverage honesty), never silently omitted (US1 AS4).
- **No managed resources yet**: an account with zero `pdp-managed` resource groups returns an empty
  inventory cleanly, not an error.
- **Orphan / unclassifiable RG**: `pdp-managed == 'true'` but no usable scope tag → orphan drift.
- **Conflicting scope tags**: an RG carrying more than one scope tag (e.g., `pdp-fabric` **and**
  `pdp-spoke`) → ambiguous drift.
- **Workload missing `pdp-env`**: a workload-scoped RG without `pdp-env` cannot be grouped → flagged
  as conformance drift (per conventions, `pdp-env` is required on workload RGs).
- **Looks-managed-but-untagged**: a `rg-pdp-*`-named RG lacking `pdp-managed` → invisible drift (the
  Article III bug).
- **Owner-managed seed backend**: `RG-TF` / `cmhtfstatesa` is out of scope — never reported as
  managed and never flagged as drift.
- **Region derivation for spokes**: spoke RGs carry no region *tag* (spec 004); region is derived
  from the resource group's Azure location. A spoke whose location and naming disagree is a
  conformance-drift candidate.
- **Scale / paging**: many subscriptions and resource groups are handled completely (results are not
  truncated by paging limits).
- **Stale read at query time**: inventory reflects Resource Graph state at query time; a
  just-created resource may lag Graph's indexing briefly — inventory reports live Graph state and
  does not compensate from local records (Article III).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Inventory MUST discover **every** subscription the platform read identity can access at
  runtime; the set of subscriptions MUST NOT be hardcoded (target subscriptions are discovered, not
  configured — glossary *Target subscription*).
- **FR-002**: Inventory MUST be derived **solely** from Azure Resource Graph. It MUST NOT read from
  local records, the IPAM ledger, OpenTofu state, or any cached intent store to determine what is
  deployed (Article III; glossary *Inventory*).
- **FR-003**: Inventory MUST treat the presence of the tag `pdp-managed == 'true'` on a resource
  group as the **sole** filter for "PDP-managed"; nothing without it is considered managed
  (`docs/conventions.md` §2).
- **FR-004**: Inventory MUST classify each managed resource group by its scope tag into exactly one
  of: **fabric** (`pdp-fabric == <region>`), **platform-shared** (`pdp-platform == 'true'` —
  foundations / DNS / control plane), **spoke** (`pdp-spoke == <name>`), or **workload**
  (`pdp-workload == <name>`).
- **FR-005**: Inventory MUST group workloads into **environments** by the `pdp-env` tag, and MUST
  answer "what environments do I have deployed?" from that grouping.
- **FR-006**: Inventory MUST report each managed resource group with its **owning subscription** and
  its **region**, deriving subscription from the resource's identity and region from the resource
  group's Azure location (spokes carry no region tag — spec 004).
- **FR-007**: Inventory MUST answer "which spokes exist, and in which subscription and region?" —
  every spoke listed with name, subscription, and region.
- **FR-008**: Inventory MUST answer "what is in environment X?" — a scoped query returning exactly
  the resource groups belonging to the named environment.
- **FR-009**: A newly vended spoke, newly deployed fabric, or newly deployed workload MUST appear in
  inventory, correctly classified, with **no code or configuration change** (discovery is
  tag/Graph-driven only).
- **FR-010**: Inventory MUST return **typed, structured** results suitable for both human reading and
  programmatic consumption by the later verb/CLI/MCP layers (specs 006/007); the structured form MUST
  expose the taxonomy, per-item subscription/region, environment grouping, and drift findings without
  requiring consumers to parse human-formatted text.
- **FR-011**: Inventory MUST report subscriptions the read identity cannot access as
  inaccessible/skipped (coverage honesty); it MUST NOT silently omit them.
- **FR-012**: Inventory MUST flag, as **orphan** drift, any managed resource group
  (`pdp-managed == 'true'`) that carries no usable scope tag or otherwise cannot be classified into
  fabric/platform/spoke/workload — it MUST NOT be dropped (Article III).
- **FR-013**: Inventory MUST flag, as **conformance** drift, any managed resource group whose tags
  violate `docs/conventions.md` — including an invalid region for `pdp-fabric`; a malformed
  `pdp-spoke`, `pdp-workload`, or `pdp-env` value; a `pdp-deployed-by` value outside the allowed set;
  and a missing required tag (a workload RG without `pdp-env`, or any RG missing the universal tags).
- **FR-014**: Inventory MUST flag, as **invisible** drift, any resource group whose name matches the
  PDP naming convention (`rg-pdp-*`) but which lacks `pdp-managed == 'true'` — the "untagged =
  invisible" failure mode (Article III).
- **FR-015**: Inventory MUST flag, as **ambiguous** drift, any resource group carrying conflicting
  scope tags (more than one of `pdp-fabric` / `pdp-platform` / `pdp-spoke` / `pdp-workload`).
- **FR-016**: Each drift finding MUST identify the resource group, its subscription, and the **drift
  category** (orphan, conformance, invisible, ambiguous), with enough detail to locate and fix the
  owning stack's IaC. Inventory MUST NOT attempt to remediate drift (read-only — FR-017). Drift is
  **informational**: drift findings are part of the structured result and inventory itself MUST
  always succeed — it MUST NOT fail or return a non-success signal because drift exists. Consumers
  (and Azure Policy enforcement in spec 010) decide how to act on findings.
- **FR-017**: Inventory MUST be **read-only**: it MUST perform no Azure mutations, create no managed
  infrastructure of its own, and write no tags. Running inventory leaves Azure unchanged.
- **FR-018**: Inventory MUST exclude the owner-managed seed backend (`RG-TF` / `cmhtfstatesa`) from
  both the managed inventory and drift findings — it is explicitly out of PDP scope
  (`docs/conventions.md` scope note).
- **FR-019**: Cross-subscription reads MUST authenticate with an identity holding **read-only**
  access across the platform subscription and every accessible target subscription, with no stored
  cloud secrets (Articles I/II). This spec MUST **provision no new Azure identity or RBAC**: the
  inventory component MUST accept a **pluggable credential**, the demonstrable surface (FR-022) MUST
  run under the owner's local credential (read-only Resource Graph queries are permitted locally),
  and the future spec-006 control-plane identity MUST be able to drop in by injecting its own
  credential — no source change required.
- **FR-020**: Inventory MUST NOT depend on the environment-registry lifecycle (owner/status in
  Postgres — spec 006). It reflects **live deployed state only**; this spec performs **tag-side
  drift only** (FR-012–FR-015). Registry↔Azure reconciliation drift (the inverse direction —
  "in the registry but not in Azure") requires the spec-006 registry and is out of scope.
- **FR-021**: A full inventory sweep across all accessible subscriptions MUST complete within a
  stated latency budget (SC-005), and scoped queries (e.g., "environment X") MUST be no slower.
- **FR-022**: The deliverable MUST be **both** a reusable inventory component (consumable by the
  spec 006/007 layers) **and** a minimal read-only surface (a thin command / test harness) that
  exercises it sufficiently to demonstrate the success criteria against the live platform. This spec
  MUST NOT implement the typed verbs, the `pdp` CLI, or the MCP server (specs 006/007).
- **FR-023**: Inventory MUST classify and report at **resource-group granularity** — the unit
  carrying the `pdp-*` tag schema (`docs/conventions.md` §2). It MUST NOT be required to enumerate
  individual resources inside each managed resource group; "what is in environment X?" returns the
  managed resource groups belonging to that environment (resource-level drill-down is out of scope).

### Key Entities

- **Inventory snapshot**: the complete live result of one inventory run — the classified taxonomy
  plus drift findings plus the set of subscriptions covered/skipped, computed from Resource Graph at
  query time.
- **Accessible subscription**: a subscription the read identity can read; discovered at runtime.
  Subscriptions that cannot be read are recorded as skipped/inaccessible.
- **Managed resource group**: a resource group tagged `pdp-managed == 'true'`, carrying universal
  tags and (when applicable) a scope tag; the unit of classification.
- **Fabric / Platform-shared / Spoke / Workload**: the classified item types, derived from
  `pdp-fabric` / `pdp-platform` / `pdp-spoke` / `pdp-workload`; each reported with subscription and
  region. Platform-shared covers managed infrastructure (foundations / DNS / control plane) that
  belongs to no fabric/spoke/workload scope.
- **Environment**: a grouping of workloads sharing a `pdp-env` value; the answer to "what
  environments do I have deployed?" Spokes are environment-agnostic (conventions §2).
- **Drift finding**: a divergence between tagging intent and reality, with a category — **orphan**
  (managed but unclassifiable), **conformance** (tag value/required-tag violation), **invisible**
  (looks-managed by name but untagged), or **ambiguous** (conflicting scope tags).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of resource groups tagged `pdp-managed == 'true'` in every accessible subscription
  appear in inventory and are classified correctly (fabric / spoke / workload), each with its
  subscription and region.
- **SC-002**: A freshly vended spoke or newly deployed fabric appears in inventory, correctly
  classified, with **zero** code or configuration changes.
- **SC-003**: A deliberately mis-tagged, orphaned, conflicting, or invisible-by-naming resource group
  is flagged as drift with the correct category; correctly tagged resources and the owner-managed
  seed backend are **never** falsely flagged (no false positives).
- **SC-004**: The three headline questions — "what environments do I have deployed?", "which spokes
  exist in which subscription and region?", and "what is in environment X?" — each return correct,
  complete answers from a single inventory result.
- **SC-005**: A full inventory sweep across all accessible subscriptions returns within a small,
  stated budget (target: **P95 under 5 seconds**); a scoped query (e.g., environment X) returns in
  **under 2 seconds**.
- **SC-006**: The inventory result is consumable both as human-readable output and as typed
  structured data by an independent programmatic consumer, proven without parsing human-formatted
  text.
- **SC-007**: Running inventory produces **zero** Azure mutations (no resource created, modified, or
  deleted; no tag written) — verifiable from the Azure activity log over the run window.
- **SC-008**: Every subscription the read identity cannot access is reported explicitly as
  skipped/inaccessible; none is silently omitted.
- **SC-009**: **Teardown** — being read-only and provisioning **no** Azure identity or RBAC
  (FR-019), this feature creates nothing in Azure to tear down; there is no residual footprint to
  remove (Article IV is satisfied vacuously — there is no managed artifact to destroy).

## Assumptions

- The mandatory `pdp-*` tag schema and the naming convention in `docs/conventions.md` (finalized by
  spec 001, extended by 003/004) are authoritative and stable; inventory reads them, it does not
  redefine them.
- `pdp-env` is a **workload-scope** tag; spokes and fabrics are environment-agnostic (conventions
  §2). "Environments" therefore group **workloads**, not spokes.
- Spoke and fabric resource groups carry **region** via their Azure location (and name), not a
  dedicated region tag; inventory derives region from location (spec 004 set no region tag on spokes).
- The owner-managed seed backend (`RG-TF` / `cmhtfstatesa`) is out of PDP scope and is neither
  inventoried nor flagged (conventions scope note).
- This spec **provisions no Azure identity or RBAC** (Clarifications 2026-06-17): the demonstrable
  surface runs under the owner's local credential (read-only Resource Graph queries are permitted
  locally), and the component accepts a pluggable credential so the spec-006 control-plane identity
  injects later. The owner's credential is assumed to already hold read access across the platform
  and accessible target subscriptions.
- This spec performs **tag-side drift only**. The inverse drift ("in the registry but not in Azure")
  requires the Postgres environment registry that lands in spec 006 and is out of scope.
- The capability is **independently demonstrable** through a minimal read-only surface sufficient to
  prove the success criteria, without implementing the spec 006/007 verb/CLI/MCP layers (FR-022).
- The live platform context is the deployed `westus3` fabric (spec 003) plus the spec-004 spoke stack
  pattern (`spokes/<sub-id>/<spoke-name>`), across the platform subscription and target subscriptions
  such as `chhouse-1`.

### Out of scope (non-goals)

- The typed **verbs**, the `pdp` **CLI**, and the **MCP server** that consume this inventory (specs
  006/007).
- The **environment-registry lifecycle** — owner/status intent in Postgres — and any registry↔Azure
  **reconciliation drift** (spec 006).
- **Any Azure mutation or drift remediation** — inventory reports; fixing a flagged resource is done
  through the owning stack's IaC, not here.
- **Multi-region ergonomics** (spec 009) and **cost visibility per environment** (spec 010).
