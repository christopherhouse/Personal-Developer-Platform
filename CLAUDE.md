# CLAUDE.md — Personal Developer Platform (PDP)

A personal, AI-operated developer platform for Azure: regional hub-and-spoke network
fabrics, spoke vending, and workload deployment — all driven through typed platform
verbs consumed by a CLI and an MCP server. One owner, one trust boundary, no SaaS.

## Read these first

The constitution and binding docs are the law for every spec, plan, and line of code:

| Document | What it binds |
|---|---|
| `.specify/memory/constitution.md` | The eleven articles. Violations are wrong by definition. |
| `docs/charter.md` | Vision, capabilities, explicit non-goals, success criteria. |
| `docs/architecture.md` | The *why* behind every structural decision. Changing it is an architecture change, not a feature change. |
| `docs/tech-stack.md` | Flat tool/version reference, including the prohibited-deps list. |
| `docs/glossary.md` | Canonical terms. Use them exactly; add new terms there *before* using them in a spec. |
| `docs/spec-backlog.md` | Build order. Check dependencies before starting a spec. |

## Non-negotiables (constitution, distilled)

- **All infra mutations go through OpenTofu via platform verbs.** Never run raw `tofu`
  or `az` mutations; never generate or apply IaC at runtime. Portal changes = drift.
- **Control plane dispatches; GitHub Actions executes.** OpenTofu runs only in
  dispatched workflows (OIDC auth, no stored cloud secrets) — never in-process,
  never on a laptop.
- **Inventory comes from Azure Resource Graph**, never local records. Every managed
  resource group carries the `pdp-*` tag schema. Untagged = invisible = bug.
- **CIDR ranges come only from the IPAM ledger** (Postgres, native `cidr`,
  GiST-enforced non-overlap). Never invent address space.
- **Spokes never own egress** — all egress through the regional hub.
- **Plan before apply; explicit human confirmation before destroy** — especially
  from chat.
- **Destroyable by design**: every spec that creates resources includes clean
  teardown in its acceptance criteria.
- **Observable by design**: every resource that supports diagnostic settings ships
  logs + metrics to the shared Log Analytics workspace (`infra/platform-observability`);
  platform code emits telemetry to the shared App Insights. A per-spec acceptance
  criterion (Article XI).
- **AVM-first**: prefer Azure Verified Modules; hand-rolled `azurerm`/`azapi` modules
  need a recorded justification in their README.
- **Private and cheap by default**: no public endpoints unless a spec demands one;
  smallest viable SKU.
- **Specs before code**: specify → plan → tasks → implement. No improvised features.

## Tech stack quick facts

- **IaC**: OpenTofu **1.11.x only** (no Terraform, no Bicep) · `azurerm` 4.x primary,
  `azapi` 2.x escape hatch · AVM modules (Terraform flavor) smoke-tested under our
  pinned OpenTofu before adoption.
- **Platform code**: **.NET 10 (LTS)** — no Python. ASP.NET Core minimal APIs
  (control plane), MCP C# SDK (`pdp-mcp`), System.CommandLine (`pdp` CLI),
  Wolverine + EF Core/Npgsql on Postgres, `Azure.Identity` /
  `Azure.ResourceManager.*`, Octokit + GitHub App for `workflow_dispatch`.
- **Testing**: xUnit, Testcontainers.PostgreSql (required for IPAM — the GiST
  constraint can't be tested in-memory), Respawn, `WebApplicationFactory`,
  NSubstitute, Shouldly, WireMock.Net.
- **❌ Never introduce**: MediatR, MassTransit, AutoMapper, Moq, Serilog,
  FluentAssertions v8+ (licensing/trust — see `docs/tech-stack.md`).
- **State**: Azure Storage backend, one state per deployable unit
  (`fabrics/<region>`, `spokes/<sub-id>/<spoke-name>`,
  `workloads/<sub-id>/<spoke-name>/<workload-name>`).
- **Naming**: CAF-style — `<type>-pdp-<region>-<name>` (e.g., `vnet-pdp-eastus2-hub`).

## MCP tooling — use it, don't guess

Two MCP servers are available. Prefer them over training data, which goes stale fast
for exactly the things this project depends on:

- **Microsoft Learn MCP** (`microsoft-learn`) — the authority for everything
  Microsoft/Azure: `azurerm`/`azapi` resource behavior, AVM module guidance, Azure
  networking (peering, private DNS, NAT/Firewall), ACA, Postgres Flexible Server,
  Entra ID auth flows, .NET 10 / ASP.NET Core / EF Core APIs. Workflow: `search`
  for breadth → `code_sample_search` for snippets → `fetch` for full pages. Use it
  *before* writing Azure-touching IaC or SDK code, even when the answer feels known.
- **context7** (`context7`) — current docs for the non-Microsoft libraries: Wolverine,
  Testcontainers, Octokit, `JsonSchema.Net`/json-everything, FluentValidation,
  Shouldly, WireMock.Net, the MCP C# SDK, OpenTofu itself. Resolve the library ID
  first, then query.

Rules of thumb: Azure/Microsoft/.NET → MS Learn first. Third-party NuGet/OpenTofu
ecosystem → context7 first. Version-sensitive questions (provider schemas, AVM module
inputs, MCP SDK surface) → always verify live; never answer from memory.

## Workflow

- Features follow Spec Kit: `/speckit-specify` → `/speckit-clarify` →
  `/speckit-plan` → `/speckit-tasks` → `/speckit-implement`. Specs live under
  `specs/<###-feature-name>/`.
- Git extension hooks fire automatically around speckit commands
  (`.specify/extensions.yml`): feature branches before specify, auto-commit
  prompts before/after most stages. Mandatory hooks execute; optional ones are
  offered — don't fight them.
- `/speckit-plan` re-checks the constitution (Constitution Check gate). If a plan
  needs to violate an article, justify it in Complexity Tracking or amend the
  constitution first — never silently bypass.
- Pick specs from `docs/spec-backlog.md` in dependency order. Specs 1–4 are the
  critical path to "deploy me a spoke in East US 2."

## Environment

- **OS**: Windows 11 · **shell**: PowerShell 7+ (use PowerShell syntax: `$env:VAR`,
  `$null`, `;`/`&&` chaining — not bash-isms). POSIX scripts go through the Bash tool.
- **Common commands** (as code lands):
  - `dotnet build` / `dotnet test` / `dotnet format` — pinned by `global.json`.
  - `tofu fmt` / `tofu validate` / `tofu plan` — version pinned via
    `.opentofu-version`. **Never `tofu apply` locally** — applies run only in
    dispatched GitHub Actions workflows.
  - `az` — local auth context (`az login`) and read-only queries only.
  - .NET Aspire (`dotnet run` on the AppHost) for local control-plane dev:
    Postgres container + control plane + dashboard in one F5.
- CI: plan on PR, apply on merge, for the platform's own IaC.

## Writing style for specs & docs

- Use glossary terms exactly (fabric, spoke, vending, verb, archetype, environment,
  control plane, execution plane, IPAM ledger).
- Requirements use MUST/SHOULD language; success criteria are measurable and
  technology-agnostic; teardown is always an acceptance criterion for
  resource-creating features.

<!-- SPECKIT START -->
Active feature: 008-workload-archetypes (branch `008-workload-archetypes`).
Current plan: specs/008-workload-archetypes/plan.md — read it for technical context, project structure,
and constitution gates (PASS, no deviations). Supporting artifacts: specs/008-workload-archetypes/
research.md (R1–R11), data-model.md, quickstart.md, contracts/{workload-verbs,archetype-catalog,
execution-plane}.md.
Design decisions (clarify + plan 2026-07-02): make the platform deploy SOLUTIONS into vended spokes.
(1) ARCHETYPE CATALOG = repo-managed declarative archetypes/catalog.json (PR-only; chat/CLI can NEVER
alter deployable truth), BAKED into the api image and startup-SYNCED into registry Postgres by
CatalogSyncService (api = sole writer; sync idempotent; versions append-only + content-hash IMMUTABLE —
differing hash fails the sync loudly, invalid file keeps last good projection). Tables: archetypes,
archetype_versions (module_path + parameter_schema jsonb + git-tag version), catalog_syncs (audit),
workloads (1:1 env detail). Params validated with JsonSchema.Net (NEW package, draft 2020-12,
OutputFormat.List → per-parameter errors) BEFORE any intent/dispatch; validation order: FluentValidation
shape → catalog (active, newest active version) → JSON schema → spoke exists+Active → intent.
(2) WORKLOAD VERBS extend spec-006 with ZERO reimplementation: workload = environments row
(EnvironmentKind.Workload, new EF migration) + registry.workloads detail; same saga via
BeginWorkloadDeploy/BeginWorkloadDestroy (NO IPAM steps — workloads carve no address space); natural key
unchanged ⇒ workload names unique PER SUBSCRIPTION. IWorkloadVerbs Plan/Deploy/PlanDestroy/Destroy mirrors
ISpokeVerbs; ConfirmationGuard verbatim restatement + workflow destroy-confirm input (both layers). MCP:
PlanWorkloadDeploy/ApplyWorkloadDeploy/PlanWorkloadDestroy/DestroyWorkload (+2 ConfirmationOperation
members, same 15-min single-use token). CLI: pdp workload deploy|destroy (destroy --confirm restates name,
no --yes bypass). SpokeVerbs destroy gains FR-021 guard: refuse while active workloads exist, naming
survivors. State per workload: workloads/<sub-id>/<spoke-name>/<workload-name> (partial backend key at
init). Workflows workload-deploy.yml/workload-destroy.yml mirror spoke-vend/destroy with run-name
`pdp <mode> <env_id>`; PINNED-TAG execution = actions/checkout ref=archetype/<name>/<version> then
tofu -chdir=<module_path> (deployed workloads keep their stamped tag; destroy checks out the stamped tag).
(3) FIRST ARCHETYPE archetypes/container-app-sql: container app (AVM containerapp 0.9.0) on a NEW
per-spoke SHARED ACA environment + Azure SQL SERVERLESS auto-pause (NEW AVM avm-res-sql-server 0.2.1 —
smoke-validate under OpenTofu 1.11 first; managedenvironment stays 0.4.0, 0.5.0 needs TF ~>1.12).
Private by default: ingress.external=false unless publicEndpoint param; SQL Entra-only auth
(workload UAMI = server Entra admin — zero secrets), public access disabled, PE into spoke workload
subnet. Tags pdp-workload + pdp-env (env regex ^[a-z0-9-]{1,16}$) ⇒ ListWorkloadEnvironments lights up
with NO inventory code change. CROSS-SPEC STACK EDITS (recorded, 007 precedent — subnets belong to the
VNet-owning stack): infra/spoke default subnets = workload + aca (/27 delegated Microsoft.App/environments)
+ shared cae-pdp-<region>-<spoke> (workload-profiles, consumption-only, EXTERNAL env, VNet-integrated,
diagnostics → shared LA) + output spoke_aca_environment_id; infra/platform-dns +
privatelink.database.windows.net; infra/fabric shared_dns_zone_ids += sql (spokes auto-link);
controlplane-host-images.yml paths += archetypes/catalog.json. RISK R10: hub firewall may need ACA
platform FQDN application rules (mcr.microsoft.com etc.) — live-acceptance contingency via fabric PR.
TEMPLATE REPO pdp-workload-template (separate GitHub template repo): .NET 10 minimal API (health + SQL
route via Microsoft.Data.SqlClient managed identity), CI consumes the platform's FIRST reusable
workflow_call workflow reusable-container-build.yml (OIDC → az acr login → build/push sha+latest);
one-time per stamped repo: federated credential + AcrPush + AZURE_* variables (README, SC-010 precedent).
Image refs: any public ref OR platform ACR (auto managed-identity pull, no creds as params). CLI live
acceptance via transient ACA job (laptop can't reach private Postgres — by design). NON-GOALS: no in-place
workload upgrade/reconfigure (destroy→deploy), no second archetype, no workload CD, no multi-region
(spec 9), no cost reporting (spec 10). NO prohibited deps. Platform context: live in WEST US 3; specs
002–007 merged; spec-007 host teardown verification still deferred to spec 9.
<!-- SPECKIT END -->
