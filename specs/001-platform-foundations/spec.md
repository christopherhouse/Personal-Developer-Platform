# Feature Specification: Platform Foundations

**Feature Branch**: `001-platform-foundations`

**Created**: 2026-06-11

**Status**: Draft

**Input**: User description: "Platform foundations: bootstrap the platform repo so every later
spec has rails to run on — repository layout, state backend with the bootstrap problem solved,
pinned tool versions, finalized naming convention and tag schema, and the CI skeleton
(plan on PR / apply on merge via OIDC)."

> **Note on terminology**: Per the constitution and `docs/architecture.md`, OpenTofu,
> Azure Storage state, and GitHub Actions are binding architectural constraints for this
> project, not implementation choices made by this spec. They are referenced below as
> fixed context. The glossary (`docs/glossary.md`) defines all platform terms used here.

## Clarifications

### Session 2026-06-11

- Q: Must all platform IaC changes flow through a PR, or may the owner push directly to
  `main`? → A: Branch protection on `main`: all changes require a PR; direct pushes
  blocked — plan review is structurally guaranteed.
- Q: What recovery capability must the state backend provide for state data? → A:
  Versioning + soft delete (point-in-time recovery within a retention window); locally
  redundant storage. No geo-redundancy — re-bootstrap covers regional loss.
- Q: On merge, does apply execute the saved PR plan artifact or re-plan fresh? → A:
  Re-plan fresh at apply time and apply the result; the PR plan is for review, the
  apply-time plan is authoritative.
- Q: When the apply workflow fails after a merge, what is the required behavior? → A:
  Fail visibly and block: workflow red, owner notified, subsequent applies for that state
  halted until resolved; resolution is fix-forward via a new PR or manual re-run.
- Q: What does the `pdp-deployed-by` tag contain? → A: Enumerated actor values
  (`github-actions`, `control-plane`, `owner`); run-level provenance lives in the
  provisioning-run audit trail, not in tags.
- Q: Greenfield state backend or adopt existing? → A (refined 2026-06-11): **Two
  backends, distinct roles.** The owner's existing personal state account
  (`cmhtfstatesa` in `RG-TF`) is the **seed backend**: externally managed (not
  PDP-managed, not imported, not remediated), it holds exactly one PDP state — the
  foundations stack's own. The foundations stack then **creates PDP's own state
  backend** greenfield (new RG + storage account per the naming convention, full
  platform contract config), which holds the state of every other deployable unit. The
  chicken-and-egg is solved by anchoring the bootstrap state outside the system it
  creates.
- Q: How firm is the no-stored-secrets posture? → A: Hard requirement. Entra ID
  everywhere, including the state data plane: shared-key access on the state storage
  account MUST be disabled; no access keys, SAS tokens, or stored cloud credentials
  anywhere in the system.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - PDP's own state backend, bootstrapped from the seed (Priority: P1)

As the platform owner, I can stand up PDP's own state backend with a single documented
procedure — the foundations stack keeps its *own* state in my pre-existing seed backend
and creates a new, platform-contract-conformant state backend for everything else — so
every later deployable unit (fabrics, spokes, workloads) has a durable, parallel-safe,
PDP-owned home for its state, and the bootstrap state is anchored outside the system it
creates.

**Why this priority**: Every subsequent spec (IPAM ledger, hub fabric, spoke vending) writes
state. Nothing can be safely applied until PDP's backend exists, and the platform must not
depend on the owner's personal account for anything beyond the single seed anchor.

**Independent Test**: With only the seed backend existing, run the documented bootstrap
procedure end to end. Verify the foundations state lands in the seed backend, the new PDP
backend exists with contract configuration, and re-running the procedure is a no-op.

**Acceptance Scenarios**:

1. **Given** the seed backend (owner-supplied identifiers) and no PDP resources,
   **When** the owner runs the bootstrap procedure, **Then** the foundations stack's
   state is stored in the seed backend and the new PDP state backend (resource group,
   storage account, container) exists with full contract configuration (Entra-only
   auth, versioning, soft delete, naming convention, tags).
2. **Given** a bootstrapped platform, **When** the bootstrap procedure is run again,
   **Then** it completes without error and changes nothing (idempotent).
3. **Given** the PDP backend exists, **When** a deployable unit stores state using the key
   convention (`fabrics/<region>`, `spokes/<sub-id>/<spoke-name>`,
   `workloads/<sub-id>/<spoke-name>/<workload-name>`), **Then** states are isolated per unit
   and two different units can be operated on concurrently without contention.
4. **Given** the PDP backend holds live state for other units, **When** a destroy of the
   foundations is attempted, **Then** the PDP backend's documented protection prevents
   accidental destruction, and the protected resources are exactly the ones the spec
   enumerates (the explicit Article IV carve-out). The seed backend is never touched by
   any PDP operation.

---

### User Story 2 - Conventions finalized: naming and tags (Priority: P2)

As the author of every future spec, I have a finalized, written naming convention and tag
schema — including exactly which tags apply to which resource scopes and what values they
take — and the foundation's own resources demonstrate both, so later specs inherit
conventions instead of inventing them.

**Why this priority**: `docs/architecture.md` explicitly defers both conventions to this
spec. Inventory (Article III) is built on the tag schema; retro-tagging or renaming
resources later is disruptive and error-prone.

**Independent Test**: Read the published conventions document; query the platform
subscription for all PDP resources and verify 100% conformance of names and tags against it.

**Acceptance Scenarios**:

1. **Given** the finalized conventions, **When** any foundation resource is inspected,
   **Then** its name matches `<type>-pdp-<region>-<name>` with the documented type
   abbreviations and its resource group carries the applicable mandatory tags.
2. **Given** the tag schema, **When** a resource group's scope is platform-level (e.g., the
   state backend, which is no fabric, spoke, or workload), **Then** the schema explicitly
   defines which tags are required (`pdp-managed`, `pdp-deployed-by`) and how
   scope-specific tags (`pdp-fabric`, `pdp-spoke`, `pdp-workload`, `pdp-env`) are handled
   for that scope — nothing is left to per-spec interpretation.
3. **Given** an inventory query over the mandatory tags, **When** it runs against the
   platform subscription, **Then** every foundation resource group is returned (nothing
   PDP-managed is invisible — Article III).

---

### User Story 3 - CI rails: plan on PR, apply on merge, no stored secrets (Priority: P2)

As the platform owner, every pull request that touches platform IaC automatically shows me
the full plan of what would change, and merging to the default branch applies exactly that
change — authenticated to Azure with federated credentials, with no cloud secrets stored
anywhere in the repository or its settings.

**Why this priority**: Article VIII (plan before apply) must be mechanical, not
discipline-based, from the very first PR. Every later spec's IaC rides these same rails.

**Independent Test**: Open a PR with a trivial IaC change (e.g., a tag value); verify the
plan appears on the PR and shows exactly that change; merge; verify the change is applied in
Azure; verify the repository contains no cloud credentials.

**Acceptance Scenarios**:

1. **Given** an open PR touching platform IaC, **When** CI runs, **Then** the PR shows
   formatting/validation results and a readable plan of the proposed changes before merge.
2. **Given** a PR whose IaC fails formatting or validation, **When** CI runs, **Then** the
   PR is blocked from merging until fixed.
3. **Given** a merged PR, **When** the apply workflow completes, **Then** live Azure state
   matches the apply-time plan, the applied plan is recorded in the run history, and the
   run is traceable to the merged change.
4. **Given** the repository and its CI configuration, **When** audited for secrets,
   **Then** no Azure credentials exist in code, variables, or secret stores — cloud access
   uses workload identity federation only.

---

### User Story 4 - Fresh clone is productive immediately (Priority: P3)

As the platform owner (or a future developer cloning PDP as a template), a fresh clone of
the repository tells me exactly which tool versions it needs, those versions are enforced
rather than suggested, and I can run format and validation checks locally with no further
setup decisions.

**Why this priority**: Version drift in IaC tooling breaks state compatibility. Valuable,
but it refines the experience of rails the earlier stories already establish.

**Independent Test**: Clone the repository onto a machine with only the baseline toolchain
installed; follow the documented setup; run the format and validate checks locally and get
the same results CI reports.

**Acceptance Scenarios**:

1. **Given** a fresh clone, **When** the developer inspects the repository, **Then** pinned
   tool versions are declared in version files (IaC engine and platform runtime) and the
   repository documents how each pin is enforced.
2. **Given** locally run format/validate checks, **When** the same commit goes through CI,
   **Then** local and CI results agree (same pinned versions, same checks).
3. **Given** a proposed provider or module version bump, **When** it is introduced,
   **Then** it arrives as an explicit, reviewable PR diff in a pin file — never as a silent
   drift at plan time.

---

### Edge Cases

- **Bootstrap re-entry after partial failure**: the bootstrap fails midway (e.g., PDP
  resource group created, storage account not yet). Re-running plan/apply must converge
  to the complete state — no duplicates, no orphaned resources.
- **Seed backend unavailable**: the owner's seed account is unreachable (deleted,
  network, RBAC). Only the foundations stack is affected — every other unit's state
  lives in the PDP backend; recovery is documented (restore seed access or re-anchor
  foundations state and re-import the small foundations resource set).
- **Concurrent applies**: two workflow runs target the same state key. State locking must
  serialize them; the loser fails cleanly with a clear message rather than corrupting state.
- **Stale plan at merge time**: the infrastructure changed between PR plan and merge apply.
  The apply re-plans fresh and applies the result (the PR plan is advisory, the apply-time
  plan authoritative); the applied plan is recorded in the run so any divergence from the
  reviewed plan is visible after the fact.
- **Protected-resource destroy attempt**: a teardown explicitly targets the state backend.
  The documented protection must stop it; removing the protection must itself require a
  deliberate, reviewable action.
- **Tag/name drift**: someone hand-edits a tag or renames a managed resource in the portal.
  The next plan must surface the drift for reversion (Article I), and inventory queries must
  still find the resource group via remaining mandatory tags.
- **Subscription unavailable in CI**: federated credential misconfiguration or expired
  trust. The workflow must fail with a diagnosable error before attempting any mutation.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST define and document a layout with distinct homes for:
  fabric/archetype IaC modules, provisioning and reusable CI workflows, control-plane
  source, and platform documentation — such that later specs add content without
  restructuring.
- **FR-002**: The platform MUST provide its **own** remote state backend (new resource
  group + storage account, created by this feature per the naming convention) in the
  platform subscription, supporting per-unit state isolation and locking, using the
  deterministic key convention `fabrics/<region>`, `spokes/<sub-id>/<spoke-name>`, and
  `workloads/<sub-id>/<spoke-name>/<workload-name>`, plus platform-plane keys
  (`platform/<stack>`) per the extensible key registry. The owner's seed backend holds
  exactly one PDP state (the foundations stack's own, key `pdp/foundations`) and
  nothing else.
- **FR-003**: The bootstrap MUST be explicitly resolved and documented: the foundations
  stack's own state lives in the owner's **seed backend** (external anchor —
  owner-supplied identifiers, not PDP-managed, never imported or remediated), and the
  stack creates the PDP state backend greenfield. The procedure MUST be idempotent and
  MUST converge after partial failure. The seed backend is a documented external
  dependency: PDP stores exactly one state blob there and requires only data-plane
  read/write to it.
- **FR-004**: The state backend MUST be protected against accidental destruction. The spec's
  deliverables MUST enumerate exactly which resources are protected and why (the explicit
  Article IV carve-out), and removing the protection MUST require a deliberate, reviewable
  step. All other foundation resources MUST tear down cleanly.
- **FR-005**: Tool versions for the IaC engine and the platform runtime MUST be pinned in
  version files committed to the repository, and the pinned versions MUST be what both
  local tooling and CI actually use.
- **FR-006**: Provider and module version pinning mechanics MUST be defined per module such
  that any version change appears as an explicit file diff in a PR — version bumps are
  deliberate and reviewable, never silent.
- **FR-007**: The resource naming convention MUST be finalized and published: the
  `<type>-pdp-<region>-<name>` pattern, the authoritative list of type abbreviations
  (following Cloud Adoption Framework style), rules for resources with naming restrictions
  (e.g., globally-unique or character-limited names), and worked examples.
- **FR-008**: The mandatory tag schema MUST be finalized and published, specifying for each
  tag (`pdp-managed`, `pdp-fabric`, `pdp-spoke`, `pdp-workload`, `pdp-env`,
  `pdp-deployed-by`): its exact allowed values, which resource scopes it applies to, and
  how inapplicable scope tags are handled. Universal tags (`pdp-managed`,
  `pdp-deployed-by`) MUST apply to every managed resource group regardless of scope.
  `pdp-deployed-by` takes one of a small enumerated set of actor values
  (`github-actions`, `control-plane`, `owner`); run-level provenance (run IDs, URLs)
  belongs to the provisioning-run audit trail, not to tags.
- **FR-009**: Every resource created by this feature MUST conform to the published naming
  convention and tag schema, and MUST be discoverable by an inventory query over the
  mandatory tags (Article III).
- **FR-010**: Every PR changing platform IaC MUST automatically receive formatting checks,
  validation checks, and a readable plan of proposed changes, visible on the PR before
  merge. Failing checks MUST block merge.
- **FR-011**: Merging to the default branch MUST automatically apply the change to the
  platform subscription by re-planning fresh at apply time and applying that result (the
  PR plan is for review; the apply-time plan is authoritative). The run MUST record the
  applied plan and be traceable to the merged change.
- **FR-012**: Zero stored secrets is a hard requirement, end to end. CI authentication to
  Azure MUST use workload identity federation (OIDC); no cloud credentials may be stored
  in the repository, its variables, or its secret stores. Entra ID auth extends to the
  state data plane: shared-key access on the state storage account MUST be disabled, and
  no access keys or SAS tokens may be used by any tooling, local or CI.
- **FR-013**: The repository MUST document the local developer workflow: required baseline
  tools, how pins are enforced locally, and how to run the same format/validate checks CI
  runs.
- **FR-014**: The default branch MUST be protected: direct pushes are blocked and every
  change — IaC or otherwise — reaches the default branch only through a pull request, so
  the plan-before-apply guarantee (Article VIII) is structural, not discipline-based.
- **FR-015**: State data in the **PDP backend** MUST be recoverable: prior versions of
  any state file MUST be retrievable within a documented retention window of 30 days
  (covering accidental deletion, corruption, or bad overwrite), using locally redundant
  storage. The single foundations state blob in the seed backend inherits the seed's
  owner-managed protections (versioning + 7-day soft delete, verified 2026-06-11) —
  accepted as-is, since PDP performs no remediation on the seed. Geo-redundancy is
  explicitly out of scope — regional loss is handled by re-bootstrap (Article IX:
  cheap by default).
- **FR-016**: A failed apply on the default branch MUST fail visibly (red workflow run,
  owner notified) and MUST block subsequent applies against the same state until resolved.
  Resolution is fix-forward (a new PR) or a manual re-run of the failed workflow; there is
  no automatic retry or automatic revert.

### Key Entities

- **PDP state backend**: the durable store for all per-unit IaC state (created by this
  feature, platform subscription); the only foundation component with deliberate destroy
  protection.
- **Seed backend**: the owner's pre-existing personal state account — an external
  dependency holding exactly one PDP state blob (the foundations stack's own); outside
  PDP's management, naming, tagging, and inventory scope.
- **State key convention**: the deterministic mapping from deployable unit (fabric, spoke,
  workload) to its isolated state location; the contract every later spec writes against.
- **Naming convention**: the published pattern and abbreviation list every PDP resource
  name follows.
- **Tag schema**: the published set of mandatory tags, their allowed values, and scope
  applicability rules; the foundation of inventory.
- **Version pins**: committed files declaring exact tool, provider, and module versions;
  the unit of deliberate, reviewable upgrade.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The documented bootstrap completes in a single sitting (under 30 minutes
  of owner time) and ends with the foundations state visible in the seed backend and the
  new PDP state backend live with contract-conformant configuration.
- **SC-002**: 100% of changes reach the default branch via PR (0 direct pushes possible);
  100% of PRs touching platform IaC display a plan before merge; 0 merges occur with
  failing format/validation checks.
- **SC-003**: A merged IaC change is live in Azure without any manual step beyond the merge
  itself, and the audit trail links the applied change to its PR.
- **SC-004**: An inventory query over the mandatory tags returns 100% of foundation
  resource groups; 100% of foundation resources pass an automated naming-convention check.
- **SC-005**: A credentials audit of the repository and CI configuration finds zero stored
  cloud secrets.
- **SC-006**: A fresh clone reaches passing local format/validate checks with no
  undocumented setup steps, and local results match CI results for the same commit.
- **SC-007**: Tearing down the foundations removes everything except the enumerated
  protected resources, leaving no orphans; the protected list in documentation matches
  exactly what remains.
- **SC-008**: The next spec in the backlog (ipam-ledger) can begin without modifying the
  repository layout, state convention, tag schema, naming convention, or CI rails this
  feature delivers.

## Assumptions

- The platform subscription already exists and the owner holds Owner-level RBAC on it;
  subscription creation is out of scope.
- The platform repository is this repository, hosted on GitHub; repository creation and
  GitHub organization setup are out of scope.
- The **seed backend** (the owner's personal TF state account) already exists; verified
  2026-06-11: storage account `cmhtfstatesa`, resource group `RG-TF`, container
  `tfstate`, subscription `8bd05b2f-62c5-4def-9869-f0617ebb3970`, eastus2, shared-key
  access already disabled. It is owner-managed, outside PDP's inventory, naming, and
  tagging scope — PDP stores exactly one state blob there and never modifies the
  account.
- All PDP foundation resources (including the new PDP state backend) are created in
  **East US 2** as the initial primary region (consistent with charter examples); the
  conventions must not hardcode this choice for later regions.
- The CI identity's Azure role assignments are scoped to the platform subscription for this
  spec; cross-subscription rights arrive with spoke vending (spec #4).
- The control-plane source tree gets a designated home in the repo layout now, but no
  control-plane code is written in this spec (that's the action-layer spec, #6).
- GitHub App setup, `workflow_dispatch` provisioning flows, and webhook handling are out of
  scope here — this spec delivers CI for the platform's own IaC only (the execution-plane
  machinery for environments arrives with the action-layer spec).
