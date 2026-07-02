# Feature Specification: Workload Archetypes

**Feature Branch**: `008-workload-archetypes`

**Created**: 2026-07-02

**Status**: Draft

**Input**: User description: "Workload archetypes: make the platform able to deploy actual solutions into vended spokes. Three coupled deliverables. (1) ARCHETYPE CATALOG — a registry in the existing control-plane Postgres of deployable workload archetypes: module path (OpenTofu module in the platform repo), pinned git tag, and a parameter JSON schema. The catalog is the sole source of deployable truth: a verb can only stamp what the catalog lists, and caller-supplied parameters MUST be validated against the archetype's JSON schema before any dispatch. Include how archetypes get registered/versioned/retired (catalog lifecycle), with a pinned-tag upgrade story — deployed workloads keep the tag they were stamped with. (2) WORKLOAD VERBS — extend the spec-006 verb layer (no reimplementation) with workload deploy/destroy following the established contract: typed verbs, plan→confirm two-phase gate, explicit confirmation with verbatim target restatement before destroy (Article VIII), dispatched GitHub Actions execution with env_id correlation and run tracking, one OpenTofu state per workload (workloads/<sub-id>/<spoke-name>/<workload-name>), consumed by both the pdp CLI and the pdp-mcp chat surface. A workload deploys INTO an existing spoke (spec 004) and never creates network fabric; egress stays via the regional hub. Workloads carry the pdp-* tag schema plus the pdp-env workload-environment tag so ARG inventory and the existing ListWorkloadEnvironments MCP tool light up (Environment (workload sense) finally becomes non-empty). (3) FIRST ARCHETYPE + TEMPLATE REPO — one real archetype proving the pipeline end-to-end: container app + database (no public endpoint unless a parameter explicitly demands one), smallest viable SKUs, AVM-first, diagnostics to the shared Log Analytics workspace (Article XI). Plus a template repo — a GitHub template repository that stamps new workload app repos whose CI consumes the platform repo's reusable workflows. Acceptance MUST include: deploying the archetype into a live spoke via chat and via CLI, inventory shows the workload grouped by pdp-env, invalid parameters rejected before dispatch with a schema-derived error, diagnostics wired to the shared workspace, and clean teardown."

## Clarifications

### Session 2026-07-02

- Q: How are archetype catalog changes (register/version/retire) applied? → A: Repo-managed declarative catalog — a catalog definition file in the platform repo, changed only by PR; the control plane syncs it into Postgres. Git is the source of change; Postgres is the runtime projection. Chat/CLI can never alter deployable truth.
- Q: Which database service does the first archetype deploy? → A: Azure SQL Database serverless with auto-pause — lowest idle cost for transient personal workloads; private access only, smallest viable configuration.
- Q: Where do workload container images come from? → A: Both — any resolvable public image reference works; references to the platform registry additionally get automatic managed-identity pull. Stamped-repo CI publishes to the platform registry; live acceptance may use a public sample image.
- Q: What application skeleton does the workload template repo stamp? → A: A .NET 10 containerized minimal API — health endpoint plus a sample SQL-backed route — so a stamped app can exercise the full first archetype; its CI (via the platform's reusable workflows) builds/tests, produces the image, and pushes to the platform registry. The reusable workflow itself stays Dockerfile-based (language-agnostic).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Deploy a workload from the catalog into a vended spoke (Priority: P1)

The owner has a vended spoke (spec 004) and wants an actual solution running in it. From
either the `pdp` CLI or a chat conversation (pdp-mcp), they ask to deploy a catalogued
archetype into that spoke, supplying a workload name, a workload environment name (e.g.
`dev`), and the archetype's parameters. The platform validates the parameters against the
archetype's parameter schema, shows a plan (target spoke, archetype and pinned version,
resolved parameters, what will be created), and — only after explicit confirmation —
dispatches execution. The owner watches the tracked run complete and ends with a running,
tagged, private-by-default workload inside the spoke.

**Why this priority**: This is the entire point of the feature — and of the platform's
critical path ("deploy me a solution, not just a network"). Every other story either
feeds it (catalog) or observes it (inventory).

**Independent Test**: With one archetype pre-seeded in the catalog and one live spoke,
run a deploy end-to-end from the CLI and separately from chat; verify the workload's
resources exist in the spoke, carry the mandatory tags, and the run is recorded and
correlated.

**Acceptance Scenarios**:

1. **Given** a live spoke and an active catalogued archetype, **When** the owner requests
   a workload deploy with valid parameters via the CLI, **Then** the platform returns a
   plan naming the target spoke, archetype, pinned version, and parameters, and after
   explicit confirmation dispatches execution, tracks the run to completion, and the
   workload's resources exist inside the spoke.
2. **Given** the same setup, **When** the owner performs the same deploy through a chat
   conversation, **Then** the same plan→confirm sequence occurs and the same outcome
   results — with no capability difference between the two surfaces.
3. **Given** a deploy request whose parameters violate the archetype's parameter schema,
   **When** the request is submitted, **Then** it is rejected **before any dispatch or
   intent is recorded**, with an error derived from the schema that names the offending
   parameter(s) and the violated constraint.
4. **Given** a deploy request naming an archetype that is not in the catalog (or is
   retired), **When** the request is submitted, **Then** it is refused before dispatch
   with an error stating the archetype is unknown or no longer deployable.
5. **Given** a deploy request targeting a spoke that does not exist or is not
   platform-managed, **When** the request is submitted, **Then** it is refused before
   dispatch.
6. **Given** a completed deploy, **When** the owner inspects the workload, **Then** its
   resources carry the mandatory `pdp-*` tag schema including `pdp-workload` and the
   `pdp-env` workload-environment tag, and expose no public endpoint (no parameter
   requested one).

---

### User Story 2 - Destroy a workload cleanly (Priority: P2)

The owner wants to remove a deployed workload — and only that workload — from its spoke.
They request a workload destroy from either surface; the platform shows what will be
destroyed and requires explicit confirmation including a verbatim restatement of the
workload's target name before executing. Afterwards the spoke is intact and unchanged,
and no workload resources or state linger.

**Why this priority**: Destroyable-by-design (Article IV) is a ratification requirement —
a workload that cannot be torn down cheaply is a cost leak. Destroy also gates acceptance
of Story 1 (clean teardown is an acceptance criterion).

**Independent Test**: Deploy a workload (Story 1), destroy it from chat with the
confirmation flow, verify the spoke's own resources are untouched and no workload
resources, tags, or state remain.

**Acceptance Scenarios**:

1. **Given** a deployed workload, **When** the owner requests its destruction, **Then**
   the platform presents what will be destroyed and requires explicit confirmation with a
   verbatim restatement of the workload's target name before anything executes — on both
   the CLI and chat surfaces.
2. **Given** a confirmed destroy, **When** execution completes, **Then** all of the
   workload's resources are gone, its state is removed, the containing spoke and its
   network configuration are unchanged, and the destroy run is recorded and correlated.
3. **Given** a destroy confirmation whose restated target name does not match the
   workload, **When** it is submitted, **Then** the destroy is refused and nothing is
   executed.
4. **Given** a chat turn that asks to destroy a workload without completing the
   confirmation contract, **When** the model relays the request, **Then** no destructive
   action occurs.

---

### User Story 3 - Workload environments appear in inventory (Priority: P3)

The owner asks — in chat or via the CLI — "what workload environments do I have
deployed?" Because deployed workloads carry the `pdp-env` tag, live inventory now returns
a non-empty, grouped answer: environments (e.g. `dev`, `demo`), the workloads in each,
and which spoke and subscription each workload lives in.

**Why this priority**: Inventory is how the platform keeps its "tagged, tracked, or it
doesn't exist" promise (Article III). The existing workload-environment inventory surface
has been empty by construction until this spec; lighting it up proves the tag schema is
correct end-to-end.

**Independent Test**: After deploying one workload with `pdp-env=dev`, query workload
environments from live inventory and verify the workload appears grouped under `dev`
with its spoke and subscription; after destroy, verify it disappears.

**Acceptance Scenarios**:

1. **Given** a deployed workload tagged `pdp-env=dev`, **When** the owner lists workload
   environments, **Then** the answer is derived from live Azure state, is non-empty, and
   shows the workload grouped under `dev`.
2. **Given** the workload is destroyed, **When** the owner lists workload environments
   again, **Then** the workload no longer appears (and the environment disappears if it
   was the only member).

---

### User Story 4 - Manage the archetype catalog lifecycle (Priority: P4)

The owner registers a new archetype (module path, pinned version, parameter schema),
registers a newer version of an existing archetype, and retires an archetype. The catalog
is the sole source of deployable truth: only active catalogued archetypes can be
deployed. Registering a newer version never changes any deployed workload — each workload
keeps the exact pinned version it was stamped with. Retiring an archetype stops new
deploys but leaves existing workloads running and still destroyable.

**Why this priority**: The lifecycle makes the catalog trustworthy over time, but a
seeded catalog with one archetype is enough to deliver Stories 1–3.

**Independent Test**: Register a second version of the seeded archetype, verify new
deploys use the new version while the previously deployed workload still reports its
original version; retire the archetype, verify a new deploy is refused while the
existing workload can still be destroyed.

**Acceptance Scenarios**:

1. **Given** an archetype with a deployed workload, **When** a newer version of the
   archetype is registered, **Then** the deployed workload is unchanged and continues to
   report the version it was stamped with, and subsequent deploys use the newest active
   version.
2. **Given** a retired archetype, **When** a new deploy of it is requested, **Then** the
   request is refused before dispatch with an error stating the archetype is retired.
3. **Given** a retired archetype with a deployed workload, **When** the owner destroys
   that workload, **Then** the destroy succeeds normally.
4. **Given** any catalog change (register, new version, retire), **When** it is applied,
   **Then** the change is auditable: what changed, when, and by what path.

---

### User Story 5 - Stamp a new workload app repo from the template (Priority: P5)

The owner creates a new application repository from the platform's workload template
repository. The stamped repo arrives with working CI that consumes the platform repo's
reusable workflows — so a brand-new app project starts with the platform's build/publish
conventions instead of hand-rolled pipelines.

**Why this priority**: The template repo completes the "actual solutions" story — it is
how future app code reaches the workloads the other stories deploy — but the archetype
pipeline is provable without it.

**Independent Test**: Stamp a new repo from the template, run its CI, and verify the
workflow run succeeds by invoking the platform repo's reusable workflows.

**Acceptance Scenarios**:

1. **Given** the workload template repository, **When** the owner stamps a new repo from
   it, **Then** the new repo's CI runs green on first push, consuming the platform
   repo's reusable workflows rather than defining its own build logic.
2. **Given** a stamped repo, **When** its CI publishes an application artifact, **Then**
   the artifact is usable as the application input of the first archetype (the deploy
   parameterizes which application the workload runs).

---

### Edge Cases

- **Duplicate deploy (idempotency)**: deploying the same workload name into the same
  spoke again with identical inputs MUST NOT create duplicates; the platform follows the
  established idempotent-convergence behavior of the existing verb contract. A repeat
  deploy with *different* parameters against an existing workload is refused (in-place
  reconfiguration is out of scope; the path is destroy → deploy).
- **Spoke destroyed out from under a workload**: a spoke destroy request for a spoke that
  still contains deployed workloads MUST be refused (or explicitly warn and require the
  workloads to be destroyed first) — teardown order is workloads before spoke.
- **Catalog entry points at a missing module version**: if the pinned version reference
  no longer resolves in the platform repo at execution time, the run fails with a clear,
  correlated error and no partial workload is left registered as healthy.
- **Confirmation expiry**: a plan confirmation that is not exercised within its validity
  window lapses; a lapsed or reused confirmation is refused (established two-phase-gate
  contract).
- **Parameter requests a public endpoint**: the workload gets a public endpoint *only*
  when a parameter explicitly demands one; the default is private. The plan MUST make an
  explicitly requested public endpoint visible before confirmation.
- **Dispatch or run failure**: a failed execution run is recorded and correlated like any
  other; the workload's recorded intent reflects the failure, and the owner can retry or
  destroy.
- **Invalid workload environment value**: `pdp-env` values are validated (non-empty,
  tag-safe format) before dispatch.
- **Registry/Azure disagreement**: a workload present in the intent registry but missing
  from live Azure (or vice versa) surfaces through the existing drift/reconciliation
  surfaces; inventory answers remain derived from live Azure state.

## Requirements *(mandatory)*

### Functional Requirements

**Archetype catalog**

- **FR-001**: The platform MUST maintain an archetype catalog — a registry, in the
  existing control-plane data store, of deployable workload archetypes. Each catalog
  entry MUST record at minimum: the archetype's identity, the location of its
  infrastructure module within the platform repo, an immutable pinned version reference
  (git tag), and a machine-enforceable parameter schema.
- **FR-002**: The catalog MUST be the sole source of deployable truth: the workload
  deploy verb MUST refuse any archetype (or archetype version) not listed as active in
  the catalog, before any dispatch or intent recording.
- **FR-003**: Caller-supplied parameters MUST be validated against the archetype's
  parameter schema before any dispatch or intent recording. Validation failures MUST
  produce a schema-derived error that names the offending parameter(s) and the violated
  constraint(s), on both the CLI and chat surfaces.
- **FR-004**: The catalog MUST support a lifecycle of register (new archetype), version
  (register a newer pinned version of an existing archetype), and retire (archetype
  accepts no new deploys). Retirement MUST NOT affect already-deployed workloads,
  including their destroyability.
- **FR-005**: Every deployed workload MUST permanently record the exact archetype version
  (pinned reference) it was stamped with. Registering a newer version MUST NOT change any
  deployed workload. Any future redeploy-at-a-newer-version capability is out of scope;
  the supported path in this spec is destroy → deploy.
- **FR-006**: Catalog changes (register, version, retire) MUST be applied only through
  a declarative catalog definition maintained in the platform repo and changed by
  reviewed pull request; the control plane synchronizes the definition into the catalog
  store. No conversational or CLI surface may alter what is deployable. Audit = the
  repo history of the definition plus a record of each applied sync.

**Workload verbs**

- **FR-007**: The platform MUST extend the existing typed verb layer with workload deploy
  and workload destroy verbs, following the established verb contract (typed
  requests/results, structured output) with no reimplementation of existing machinery,
  and consumed identically by the `pdp` CLI and the chat surface.
- **FR-008**: Workload deploy MUST follow the established plan→confirm two-phase gate:
  the plan MUST state the target spoke, subscription, archetype and pinned version,
  workload name, workload environment, and the resolved parameters; nothing executes
  before explicit confirmation.
- **FR-009**: Workload destroy MUST additionally require explicit confirmation including
  a verbatim restatement of the workload's target name before execution (Article VIII).
  A mismatched or absent restatement MUST refuse the operation.
- **FR-010**: Confirmed operations MUST execute exclusively as dispatched execution-plane
  runs (GitHub Actions), correlated by the platform's correlation identifier and tracked
  to completion in the existing run-audit trail. The control plane MUST NOT execute
  infrastructure changes in-process.
- **FR-011**: Each workload MUST have exactly one isolated infrastructure state, keyed as
  `workloads/<subscription-id>/<spoke-name>/<workload-name>`, independent of the spoke's
  and fabric's state.
- **FR-012**: A workload MUST deploy only into an existing, platform-managed spoke. The
  deploy verb MUST verify the target spoke's existence and managed status before
  dispatch. A workload MUST NOT create network fabric — no VNets, no peerings, no routes,
  no egress paths; all egress remains via the regional hub (Article VII).
- **FR-013**: Every workload resource group MUST carry the mandatory `pdp-*` tag schema,
  including `pdp-workload` (the workload's name) and `pdp-env` (the workload-environment
  name supplied at deploy). The workload-environment value MUST be validated before
  dispatch.
- **FR-014**: The control plane MUST record each workload as a tracked managed unit
  (intent, natural key, correlation identifier, run history) consistent with how vended
  fabrics and spokes are tracked today; the glossary's "managed unit" definition is
  extended to include workloads.

**Inventory**

- **FR-015**: Live inventory MUST list deployed workloads grouped by workload environment
  (`pdp-env`), including each workload's spoke and subscription, derived from live Azure
  state — making the existing workload-environment inventory surface (including the
  existing chat inventory tool) return non-empty, correct answers once workloads exist.

**First archetype + template repo**

- **FR-016**: The catalog MUST ship with one real, deployable archetype: a containerized
  application plus a database — Azure SQL Database at the serverless tier with
  auto-pause, private access only — deployable into a spoke. It MUST be private by
  default —
  no public endpoint unless a parameter explicitly requests one — use the smallest viable
  service tiers, and prefer Azure Verified Modules with any exception justified in
  writing (Article V, Article IX).
- **FR-017**: The first archetype's parameter schema MUST cover at minimum: the
  application (container image reference) to run, the workload-environment name, and the
  explicit opt-in for a public endpoint. Parameters MUST have safe defaults consistent
  with private-and-cheap-by-default. The image reference MAY be any resolvable public
  reference; when it points at the platform's container registry, the archetype MUST
  wire managed-identity pull automatically (no registry credentials as parameters).
- **FR-018**: Every resource created by the first archetype that supports diagnostic
  settings MUST ship logs and metrics to the platform-shared Log Analytics workspace
  (Article XI).
- **FR-019**: The platform MUST provide a workload template repository — a GitHub
  template repository from which new workload application repos are stamped — whose
  stamped repos' CI consumes the platform repo's reusable workflows (build/publish
  conventions defined once, in the platform repo). The template contains a .NET 10
  containerized minimal-API skeleton with a health endpoint and a sample
  database-backed route, so a stamped app can exercise the first archetype end to end;
  its CI builds and tests the app, produces the container image, and publishes it to
  the platform's container registry. The reusable workflow operates on the repo's
  Dockerfile and does not itself constrain the application language.
- **FR-020**: Workload destroy MUST remove all of the workload's resources and its
  isolated state without touching the containing spoke, its network configuration, or
  any other workload (Article IV).
- **FR-021**: A spoke destroy targeting a spoke that still contains deployed workloads
  MUST NOT silently orphan them: the platform MUST refuse the spoke destroy until its
  workloads are destroyed, and the refusal MUST name the surviving workloads.

### Key Entities

- **Archetype**: a parameterized, reusable template for a class of solution (glossary
  term). Identified by name; carries a lifecycle status (active/retired).
- **Archetype version**: one immutable, deployable revision of an archetype: module
  location in the platform repo + pinned version reference (git tag) + parameter schema.
  New versions append; existing versions never mutate.
- **Archetype catalog**: the registry of archetypes and their versions in the
  control-plane data store (glossary term). Sole source of deployable truth.
- **Workload**: a deployed instance of an archetype running inside a spoke (glossary
  term). Records: name, target spoke and subscription, archetype + stamped version,
  validated parameters, workload environment, correlation identifier, run history.
- **Workload environment**: the named workload grouping (e.g. `dev`, `demo`) carried by
  the `pdp-env` tag (glossary term, workload sense); an inventory-derived grouping, not
  a stored entity.
- **Provisioning run**: one recorded execution-plane run (existing entity), extended to
  cover workload deploy/destroy runs.
- **Workload template repo**: the GitHub template repository that stamps new workload
  app repos (glossary term).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The owner deploys the first archetype into a live spoke **via chat** —
  request, schema validation, plan, explicit confirmation, dispatched execution, tracked
  completion — within a single conversation, and the workload is running in the spoke at
  the end of it.
- **SC-002**: The owner completes the same deploy **via the CLI** with the same verbs and
  the same plan→confirm behavior; the two surfaces expose no capability difference for
  workload operations.
- **SC-003**: 100% of deploy requests with schema-invalid parameters are rejected before
  any dispatch or intent recording, with an error that names the offending parameter(s);
  zero execution-plane runs are triggered by invalid requests.
- **SC-004**: Immediately after a deploy completes, listing workload environments returns
  a non-empty answer derived from live Azure state showing the workload grouped under its
  `pdp-env` value, in one query/turn.
- **SC-005**: The deployed workload exposes zero public endpoints by default, and its
  running cost uses the smallest viable tiers for every billable resource it creates.
- **SC-006**: Every diagnostics-capable resource created by the first archetype has its
  logs and metrics flowing to the platform-shared Log Analytics workspace, verified after
  the live deploy (Article XI).
- **SC-007**: Clean teardown (Article IV): destroying the workload removes 100% of its
  resources and its state; the containing spoke's resources and network configuration are
  byte-for-byte unaffected (verified by plan showing no changes on the spoke); a
  subsequent workload-environment listing no longer shows the workload.
- **SC-008**: After registering a newer archetype version, the previously deployed
  workload still reports the exact version it was stamped with, and a fresh deploy uses
  the newer version — verified in the same environment.
- **SC-009**: A repo stamped from the workload template repository runs its CI green on
  first push, consuming the platform repo's reusable workflows.
- **SC-010**: Every workload deploy and destroy appears in the run-audit trail with its
  correlation identifier, linkable from request to execution-plane run to outcome.

## Assumptions

- **Existing machinery is reused, not rebuilt**: the spec-006 verb layer (typed verbs,
  two-phase gate, dispatch + correlation + run tracking, registry) and the spec-007
  hosted control plane and chat surface are in place and are extended, not modified in
  contract. "No reimplementation" is a hard constraint.
- **Target spokes come from spec 004**: workload deploys target spokes vended by the
  platform; deploying into unmanaged VNets is out of scope.
- **Database flavor (resolved 2026-07-02)**: the first archetype's database is Azure SQL
  Database, serverless tier with auto-pause, private access only, smallest viable
  configuration. Chosen for lowest idle cost on a transient personal workload; this is a
  new service family for the platform, so its module adoption follows Article V
  (AVM-first, smoke-validated under the pinned OpenTofu).
- **Application image (resolved 2026-07-02)**: the image reference parameter accepts any
  resolvable public reference; platform-registry references get automatic
  managed-identity pull (FR-017). Template-stamped repos publish to the platform
  registry; live acceptance may use a public sample image. Producing the image is CI's
  job, not the deploy verb's.
- **Catalog change path (resolved 2026-07-02)**: catalog changes are repo-managed — a
  declarative definition file changed only by PR and synced to the store by the control
  plane (see FR-006). The module change and its catalog entry land in one reviewed diff.
- **No in-place workload mutation**: reconfiguring or upgrading a deployed workload
  in place (new parameters or newer archetype version) is out of scope; the path is
  destroy → deploy. A dedicated upgrade story is future work.
- **Single region**: the live platform runs in one region (West US 3); multi-region
  behavior is spec 009. Nothing in this spec may preclude it.
- **One trust boundary**: single owner; no multi-user authorization, no APIM, no
  new auth surface. The existing owner-gated auth model covers the new verbs.
- **Glossary**: "managed unit" is extended to include workloads; no other new terms are
  needed (archetype, archetype catalog, workload, workload template repo, environment
  (workload sense) are already defined).
- **Non-goals**: no second archetype, no workload-level CD (auto-deploy on app CI), no
  cost reporting (spec 010), no reconciliation of workloads created outside the platform.
