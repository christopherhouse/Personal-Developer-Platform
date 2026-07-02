# Phase 0 Research — Workload Archetypes (spec 008)

All unknowns from Technical Context resolved. Sources: live repo exploration
(2026-07-02), Microsoft Learn MCP (Azure SQL serverless, ACA VNet requirements,
cross-RG environment references), AVM module repos, spec 006/007 artifacts.

## R1 — Catalog storage & sync path

**Decision**: The catalog is a declarative file `archetypes/catalog.json` in the
platform repo, changed only by PR (clarify 2026-07-02). It is **baked into the api
container image** (Dockerfile `COPY archetypes/catalog.json`;
`controlplane-host-images.yml` gains `archetypes/**` in its path filter) and a
`CatalogSyncService` (hosted service in `Pdp.ControlPlane.Api` only — single writer)
upserts it into the `registry` schema at startup: insert new archetypes/versions,
apply status changes (retire/reactivate), record a `catalog_syncs` audit row
(content hash, applied-at, per-entry summary). Sync is idempotent; an unchanged hash
short-circuits to a no-op.

**Rationale**: git→image→startup-sync gives a deterministic pipeline with zero new
runtime dependencies (no GitHub API fetch at boot, no manual step): merging a catalog
PR triggers the existing image build + deploy, and the new revision projects the
definition into Postgres. The MCP app (which hosts the verb layer in-process) reads the
same registry tables and needs no sync of its own — avoiding dual writers.

**Alternatives considered**: fetch `catalog.json` from GitHub at startup (runtime
coupling to GitHub availability + ref ambiguity — rejected); hand-written EF migrations
per catalog change (audit-poor, conflates schema and data — rejected); owner-invoked
`archetype register` verbs (rejected by clarification: chat/CLI must never alter
deployable truth).

## R2 — Version immutability enforcement

**Decision**: An archetype **version is immutable once synced**: `(archetype, version)`
rows carry a content hash of `(module_path, parameter_schema)`; a sync that presents
different content for an existing version **fails the whole sync loudly** (telemetry +
log + `catalog_syncs` row recording the rejection) rather than silently mutating.
Retire/reactivate are status-only changes on the archetype (and optionally per-version
`deprecated` flag — v1 keeps status at archetype level only, matching the spec).
Deploys always resolve *the newest active version* (US4); the resolved version is
stamped permanently onto the workload (FR-005).

**Rationale**: FR-005's pinned-tag story only holds if a tag's meaning can't drift;
enforcing it at sync time makes the git history the complete audit trail.

## R3 — Parameter schema validation library & semantics

**Decision**: `JsonSchema.Net` (json-everything) — add to `Directory.Packages.props`
(latest 7.x at implement time; JSON Schema draft 2020-12 default). Each archetype
version stores its schema as `jsonb`. `WorkloadVerbs.PlanDeployAsync` evaluates
caller parameters (a `JsonObject`) with `OutputFormat.List` and maps failures to a
`WorkloadParameterValidationException` carrying per-parameter messages
(instance location + keyword), surfaced verbatim by CLI and MCP (FR-003 / SC-003).
Validation ordering: FluentValidation shape check (names/format, `pdp-env`
`^[a-z0-9-]{1,16}$` — same regex as `TagSchema.IsValidEnvName`) → catalog lookup
(active archetype, newest active version) → JSON-schema evaluation → spoke
existence/status check → intent recording. Nothing dispatches, and no intent row is
written, before all five pass.

**Rationale**: the schema is per-archetype *data*, so compile-time FluentValidation
cannot express it; JsonSchema.Net is the tech-stack-sanctioned validator
(`docs/tech-stack.md` names it for exactly this). Draft 2020-12 keeps authoring
conventions modern; the contract doc pins conventions (additionalProperties: false,
defaults, required).

**Alternatives**: Corvus.JsonSchema (code-gen oriented, wrong fit for runtime schemas);
hand-rolled validation (rejected — reimplementation, poor error fidelity).

## R4 — Workload in the registry: modeling & natural key

**Decision**: A workload **is a managed unit**: one `environments` row with new enum
member `EnvironmentKind.Workload` (persisted `"workload"`), reusing status machine,
saga, run tracking, and `EnvRef` resolution untouched. Workload-specific facts live in
a 1:1 detail table `registry.workloads` (PK/FK `env_id`): `spoke_subscription`,
`spoke_name`, `archetype_name`, `archetype_version`, `pdp_env`, `parameters` (jsonb,
the validated caller input). New saga inputs `BeginWorkloadDeploy` /
`BeginWorkloadDestroy` mirror the spoke messages minus IPAM allocation/release (the
saga's allocation step is skipped for workloads — no ledger interaction at all).
The existing natural-key uniqueness `(kind, subscription, name)` is kept as-is ⇒
**workload names are unique per subscription** (slightly stricter than the per-spoke
scope the state key implies).

**Rationale**: "no reimplementation" is hard; riding `environments` gets dispatch,
webhooks, reconciler, wedge-recovery (`ForceTerminalAsync`), and `ShowEnvironment` for
free. Per-subscription name uniqueness costs nothing at this scale and makes chat
references unambiguous ("destroy workload demo-api" resolves without naming the spoke).

**Alternatives**: separate `workloads` aggregate with its own saga (reimplementation —
rejected); widening the natural key to include spoke (touches the core uniqueness
index + `EnvRef` surface of specs 006/007 for no owner-visible benefit — rejected,
recorded as the trade-off behind per-subscription uniqueness).

## R5 — Where workload containers run: per-spoke shared ACA environment

**Decision**: The **spoke stack** (`infra/spoke`) vends, by default: (a) a second
subnet `aca` — `/27`, delegated `Microsoft.App/environments`, carved inside the spoke's
ledgered CIDR, route-table-associated like every spoke subnet; (b) a shared **ACA
managed environment** `cae-pdp-<region>-<spoke_name>` (AVM `avm-res-app-managedenvironment`
0.4.0 — the OpenTofu-compatible pin), workload-profiles type, consumption profile only,
VNet-integrated into `aca`, **external** environment, diagnostics → shared Log
Analytics; (c) new outputs `spoke_aca_environment_id` (+ existing `spoke_subnets`).
The workload stack references the environment by ID (container apps may live in a
different resource group than their environment — confirmed against ARM contract) and
deploys its container app with **`ingress.external = false` by default**; the
archetype's `publicEndpoint` parameter flips it (visible in the plan per spec edge
case).

**Rationale**: ACA environments require a *dedicated, delegated* subnet, and the
platform's recorded precedent (spec 007) is that **subnets belong to the VNet-owning
stack** — so per-workload environments would force the workload verb to mutate spoke
state per deploy (violates one-state-per-unit isolation). A shared per-spoke env with
consumption profile idles at $0, supports UDR (hub egress, Article VII — this is why
workload-profiles type is mandatory; legacy consumption-only needs /23 and no UDR),
and an *external* env with internal-by-default apps is exactly the spec-007 host
pattern — private until a parameter opts an app out.

**Alternatives**: per-workload ACA env + per-workload delegated subnet (spoke-state
mutation per deploy — rejected); first-workload-creates-env (destroy-order coupling
between sibling workloads, Article IV violation — rejected); non-VNet env (no internal
ingress possible, public by construction — rejected); container app on the spec-007
host env (workloads would live inside the control plane's blast radius — rejected).

**Consequences**: spoke vend creates ~2 more resources (+3–5 min); default `subnets`
map in `infra/spoke/variables.tf` changes from one full-block `workload` subnet to
`workload` + `aca` (/27) — no live spokes exist today, so no migration concern.

## R6 — Azure SQL serverless: config, auth, private access

**Decision**: AVM **`avm-res-sql-server` 0.2.1** (new family adoption; Article V
smoke-validation under OpenTofu 1.11.x is an explicit implementation task). Server:
`azuread_authentication_only = true` (zero SQL passwords — no secrets anywhere),
Entra admin = the **workload's UAMI** (created by the archetype module; also the
container app's identity), `public_network_access_enabled = false`, private endpoint
into the spoke's `workload` subnet with DNS zone group →
`privatelink.database.windows.net`. Database: `GP_S_Gen5_1` serverless
(min_capacity 0.5), `auto_pause_delay_in_minutes = 60` (floor 15, default 60 — keep
default), small `max_size_gb` (2). Diagnostic settings on server+db → shared Log
Analytics workspace (data-source-by-name pattern from `control-plane-host`).
**New DNS zone**: `privatelink.database.windows.net` added to `infra/platform-dns`
(AVM privatednszone 0.5.0, key `sql`) and surfaced through `infra/fabric`'s
`shared_dns_zone_ids` output — spokes link every published zone automatically (spec-004
mechanism), so no spoke change is needed for DNS.

**Rationale**: clarified choice (2026-07-02) for lowest idle cost; UAMI-as-Entra-admin
sidesteps the create-contained-SQL-user bootstrap (which needs a T-SQL session no
pipeline has) — acceptable for a single-owner archetype v1 and recorded in the module
README; the app authenticates with `Authentication=Active Directory Managed Identity`
in the connection string — credential-free end to end.

**Alternatives**: SQL auth + Key Vault secret (stored secret — rejected); owner as
Entra admin + manual `CREATE USER FROM EXTERNAL PROVIDER` per workload (manual step per
deploy violates the no-touch vending story — rejected, revisit if multi-app-per-server
ever matters).

## R7 — Pinned-tag execution: how a workflow runs a versioned module

**Decision**: Archetype versions are **git tags on the platform repo** with the
convention `archetype/<name>/v<semver>` (e.g. `archetype/container-app-sql/v1.0.0`).
`workload-deploy.yml` (running from the default branch, as all dispatched workflows do)
does `actions/checkout` with `ref: inputs.archetype_ref`, then executes
`tofu -chdir=archetypes/<module_path>` with `-backend-config="key=workloads/<sub>/<spoke>/<workload>"`.
The verb layer supplies `archetype_ref` + `module_path` from the catalog row — the
caller never chooses either.

**Rationale**: the workflow *definition* stays current (main) while the *module
content* is exactly the stamped tag — deployed workloads keep their tag (FR-005), and a
destroy of an old workload checks out its stamped tag so plan/destroy semantics match
what was applied.

**Alternatives**: module `source = git::...?ref=tag` inside a wrapper stack (init-time
credential + nested-module output friction — rejected); floating `main` checkout
(breaks pinning outright — rejected).

## R8 — Reusable workflow + template repo + stamped-repo auth

**Decision**: Ship the platform's **first `workflow_call` workflow**
`reusable-container-build.yml`: inputs `image_repository`, `dockerfile`, `context`;
steps = OIDC `azure/login@v3` → `az acr login` (token, no secret) → `docker build`
(tags `:$GITHUB_SHA` + `:latest`) → push — extracted from the proven
`controlplane-host-images.yml` pattern (which keeps its matrix but can consume the
reusable workflow later; out of scope here). The **template repo**
(`pdp-workload-template`, a separate GitHub template repository) contains: .NET 10
minimal API (health endpoint + one SQL-backed route using
`Microsoft.Data.SqlClient` with managed-identity auth), Dockerfile, xUnit smoke test,
and a CI workflow that `uses:
<owner>/Personal-Developer-Platform/.github/workflows/reusable-container-build.yml@main`.
**One-time bootstrap per stamped repo** (documented in the template README, spec-007
SC-010 precedent): add a federated credential for
`repo:<owner>/<stamped-repo>:ref:refs/heads/main` on the existing CI app registration +
`AcrPush` on the platform ACR, and set the three `AZURE_*` repo variables.

**Rationale**: OIDC subjects are per-repo, so cross-repo image push needs an explicit,
reviewed trust grant — a documented single manual step per new app repo beats standing
PATs/secrets (constitution: no stored cloud secrets).

**Alternatives**: PAT/ACR admin creds in stamped-repo secrets (stored secret —
rejected); building images inside the platform repo on behalf of app repos (couples app
CI to platform CI — rejected).

## R9 — AVM adoption + versions (Article V)

**Decision**: reuse pins already proven under OpenTofu 1.11: `avm-res-app-containerapp`
0.9.0, `avm-res-app-managedenvironment` 0.4.0 (0.5.0 requires Terraform ~>1.12 —
recorded incompatibility), `avm-res-network-privatednszone` 0.5.0,
`avm-res-network-virtualnetwork` 0.18.0 (spoke). **New**: `avm-res-sql-server` 0.2.1
(azurerm ~>4.26 fits our 4.x) — smoke-validate `plan`+`apply`+`destroy` of a minimal
serverless config under OpenTofu 1.11 before the archetype relies on it. Raw provider
resources limited to UAMI + role assignments (README-justified, spec-007 precedent).

## R10 — Known risk: hub firewall vs. ACA platform egress

**Decision**: proceed; verify at live acceptance. The spoke ACA subnet routes 0.0.0.0/0
through the hub firewall; ACA's infrastructure needs specific FQDN allowances (MCR:
`mcr.microsoft.com` + `*.data.mcr.microsoft.com`, Entra endpoints, ACR host when
pulling platform images). If the current fabric firewall policy blocks them, the fix is
application-rule additions to `infra/fabric`'s firewall policy **by PR** (Article I) —
budgeted as a live-acceptance contingency task, not a design change. (Spec-007's host
env never crossed the hub firewall, so this is the first exercise of spoke-side ACA
egress.)

## R11 — CLI acceptance path to the private ledger

**Decision**: SC-002 (deploy via CLI) runs the `pdp` CLI via the established
**transient ACA job** mechanism inside the control-plane VNet (memory/spec-006 closure
precedent) — the laptop cannot reach the private Postgres, and that is by design.
Quickstart documents it; no new access path is created (Article IX).
