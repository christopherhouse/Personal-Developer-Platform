<!--
Sync Impact Report
- Version change: (template, unversioned) → 1.0.0
- Modified principles: none (initial ratification — all ten principles adopted from
  docs/constitution.md draft)
- Added sections:
  - Core Principles (Articles I–X)
  - Additional Constraints (technology & platform bindings)
  - Development Workflow (Spec Kit flow, gates, testing discipline)
  - Governance
- Removed sections: none (all template placeholders filled)
- Templates requiring updates:
  - ✅ .specify/templates/plan-template.md — Constitution Check gate is generic and is
    populated per-feature from this file; no structural change required
  - ✅ .specify/templates/spec-template.md — Success Criteria guidance extended with the
    Article IV teardown acceptance criterion
  - ✅ .specify/templates/tasks-template.md — task categories align (tests optional per
    workflow section; no principle mandates TDD); no change required
  - ✅ docs/constitution.md — annotated as the superseded draft pointing here
- Follow-up TODOs: none
-->

# Personal Developer Platform (PDP) Constitution

## Core Principles

### Article I — Infrastructure is Code

All PDP-managed infrastructure MUST be created, changed, and destroyed through OpenTofu
executed by platform verbs. Portal mutations to managed resources are forbidden; anything
changed by hand MUST be treated as drift to be reverted.

**Rationale**: A single, declarative path for every mutation is what makes the platform
auditable, reproducible, and safely operable by an AI.

### Article II — The AI Calls Verbs, Never Improvises

Chatops MUST work exclusively through the typed action layer. The AI MUST NOT generate,
edit, or apply IaC at runtime, and MUST NOT run raw `tofu` or `az` mutations. New
capability = new verb = new spec.

**Rationale**: Constraining the AI to deterministic, reviewed verbs bounds the blast
radius of any conversation; trust lives in the verbs, not the model.

### Article III — Tagged, Tracked, or It Doesn't Exist

Every managed resource group MUST carry the mandatory tag schema (`pdp-managed`,
`pdp-fabric`, `pdp-spoke`, `pdp-workload`, `pdp-env`, `pdp-deployed-by`). Inventory MUST
be answered from live Azure state (Resource Graph), never from memory or local records.
A resource PDP cannot find in inventory is a bug.

**Rationale**: "What do I have deployed?" must always be answerable accurately in one
turn; stale local records make that promise impossible to keep.

### Article IV — Destroyable by Design

Every fabric, spoke, and workload MUST tear down cleanly with a single verb: no orphaned
resources, no leaked address allocations, no dangling peerings. "Can it be destroyed?"
MUST be part of every spec's acceptance criteria.

**Rationale**: A personal platform survives on cost control; anything that cannot be torn
down cheaply and completely will eventually be abandoned in place.

### Article V — AVM-First Modules

Specs and plans MUST use Azure Verified Modules where viable; fall back to
`azurerm`/`azapi` only with a recorded justification (in the module's README). Hand-rolled
modules are the exception and MUST carry their maintenance burden visibly. Each adopted
AVM module MUST be smoke-validated under the pinned OpenTofu version before being relied
on.

**Rationale**: AVM modules carry Microsoft's maintenance and best practices; every
hand-rolled module is debt this single-owner platform must service alone.

### Article VI — No Address Space Without Allocation

CIDR ranges MUST come only from the IPAM ledger (control-plane Postgres, native `cidr`
types, database-enforced non-overlap). No VNet or subnet may be created with an
unregistered range. Allocation precedes peering, always.

**Rationale**: Address conflicts are the one networking failure that cannot be fixed
in place; a single authoritative ledger with database-level enforcement removes the
failure mode entirely.

### Article VII — Hub Owns Egress

Spokes MUST NOT define their own internet egress or cross-spoke routing. All egress
flows through the regional hub. A spoke that can bypass the hub is misconfigured.

**Rationale**: One egress point per region keeps the security posture inspectable and
the routing model simple enough to reason about in conversation.

### Article VIII — Plan Before Apply, Confirm Before Destroy

Every mutation MUST show its plan before applying. Destructive operations (destroy,
address reassignment, peering removal) additionally REQUIRE explicit human
confirmation — including, and especially, when initiated through chat.

**Rationale**: Visible plans and explicit confirmation are the guardrails that make an
AI-operated platform safe; convenience never outranks them.

### Article IX — Secure and Cheap by Default

Private by default: no public endpoints unless a spec explicitly requires one.
Cost-conscious by default: prefer the smallest viable SKU; anything expensive to keep
running MUST be easy to tear down and recreate (see Article IV).

**Rationale**: This is a personal platform with no production SLAs; cost control and a
small attack surface beat high availability.

### Article X — Specs Before Code

Features MUST follow the Spec Kit flow: specify → plan → tasks → implement. The charter
(`docs/charter.md`), architecture doc (`docs/architecture.md`), and glossary
(`docs/glossary.md`) are binding context for every spec.

**Rationale**: Spec-first development is the project's chosen method for keeping an
AI-heavy workflow deliberate; skipping it reintroduces improvisation by the back door.

## Additional Constraints

- **IaC engine**: OpenTofu only (pinned 1.11.x). No raw Terraform, no Bicep. Providers:
  `azurerm` primary, `azapi` as the escape hatch, pinned per module.
- **Platform language**: .NET 10 (LTS, pinned via `global.json`) for the control plane,
  `pdp` CLI, and MCP server. No Python.
- **Control plane vs. execution plane**: the control plane validates, allocates, records
  intent, and dispatches; OpenTofu plan/apply/destroy runs ONLY in GitHub Actions
  workflows in the platform repo, authenticated to Azure via OIDC. No stored cloud
  secrets; the GitHub App credential is the only non-Azure secret.
- **Division of truth**: Postgres records intent and allocation; live Azure via Resource
  Graph is the truth for what's deployed. No reconciliation loop for resources created
  outside the platform.
- **Prohibited dependencies**: MediatR, MassTransit, AutoMapper, Moq, Serilog, and
  FluentAssertions v8+ MUST NOT be introduced (licensing/trust; see
  `docs/tech-stack.md`). Wolverine, NSubstitute, Shouldly, and built-in logging + OTel
  cover the needs.
- **Naming**: CAF-style abbreviations, `<type>-pdp-<region>-<name>`; exact convention
  finalized in the platform-foundations spec.
- **Binding documents**: `docs/charter.md`, `docs/architecture.md`,
  `docs/tech-stack.md`, and `docs/glossary.md` are inherited by every spec. Changing
  them is an architecture change, not a feature change.

## Development Workflow

- Every feature runs the Spec Kit flow (Article X). The spec backlog
  (`docs/spec-backlog.md`) orders work; dependencies between specs MUST stay visible
  there before a spec is started.
- `/speckit-plan` MUST evaluate its Constitution Check gate against this document before
  Phase 0 research and re-check after Phase 1 design. Violations MUST be justified in
  the plan's Complexity Tracking table or the plan reworked.
- Every spec's acceptance criteria MUST include clean teardown (Article IV) for any
  capability that creates resources.
- New terminology MUST be added to `docs/glossary.md` before a spec uses it.
- Testing discipline: integration tests against real dependencies where behavior depends
  on them (e.g., the IPAM allocator MUST be tested against real Postgres via
  Testcontainers — the GiST exclusion constraint cannot be verified in-memory). Unit
  tests elsewhere; test tasks are included in a feature when its spec calls for them.
- CI runs plan on PR and apply on merge for the platform's own IaC. Provider and module
  version bumps are deliberate PRs, never silent.

## Governance

This constitution supersedes all other practices for PDP. Specs, plans, and
implementations that violate it are wrong by definition and MUST be corrected or the
constitution amended first.

- **Amendments**: proposed as a PR touching this file, including a Sync Impact Report
  and any propagation to dependent templates (`.specify/templates/*`) and binding docs.
  The platform owner approves amendments; there is no other authority.
- **Versioning**: semantic versioning of this document. MAJOR for incompatible
  removals or redefinitions of principles; MINOR for new principles or materially
  expanded guidance; PATCH for clarifications and wording.
- **Compliance review**: every `/speckit-plan` run re-checks this document (the
  Constitution Check gate). Code review of any PR MUST verify changes stay within the
  action-layer and execution-plane boundaries (Articles I, II) and the dependency
  constraints above. Complexity beyond what a principle allows MUST be justified in
  writing in the plan.

**Version**: 1.0.0 | **Ratified**: 2026-06-11 | **Last Amended**: 2026-06-11
