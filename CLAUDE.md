# CLAUDE.md — Personal Developer Platform (PDP)

A personal, AI-operated developer platform for Azure: regional hub-and-spoke network
fabrics, spoke vending, and workload deployment — all driven through typed platform
verbs consumed by a CLI and an MCP server. One owner, one trust boundary, no SaaS.

## Read these first

The constitution and binding docs are the law for every spec, plan, and line of code:

| Document | What it binds |
|---|---|
| `.specify/memory/constitution.md` | The ten articles. Violations are wrong by definition. |
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
Active feature: 006-control-plane (branch `006-control-plane`).
Current plan: specs/006-control-plane/plan.md — read it for technical context, project structure, and
constitution gates. Supporting design artifacts: specs/006-control-plane/ research.md, data-model.md,
quickstart.md, contracts/{verb-surface,dispatch-and-tracking,cli-surface}.md.
Design decisions (clarify 2026-06-17; plan PASS, no constitution deviations): build the ACTION LAYER
(control plane) — the .NET 10 service that turns owner intent into VALIDATED, DISPATCHED, and TRACKED
infra ops, wrapping specs 2–5 behind TYPED VERBS exposed via the `pdp` CLI. The control plane
DISPATCHES; it NEVER runs OpenTofu in-process (Article II): each mutating verb validates → allocates
from the IPAM ledger → records intent in Postgres → dispatches a GitHub Actions workflow (GitHub App +
workflow_dispatch, OIDC in CI) → tracks to completion correlated by env_id. Plan before apply; explicit
confirm before destroy (Article VIII) via TWO-PHASE dispatch (mode=plan → confirm → apply/destroy).
Closes Gate-G1: spoke CIDR allocated LIVE BY SIZE from the ledger at vend (IIpamLedger.AllocateAsync),
released on destroy — spoke_cidr is no longer a typed input. Stands up the Postgres environment
REGISTRY (intent/owner/status) + provisioning-run AUDIT trail; division of truth holds (ARG = deployed,
Postgres = intent). Clarify decisions (2026-06-17): SCOPE = verb layer + registry + run-tracking + pdp
CLI runnable under the OWNER'S CONTEXT; production ACA hosting + public ingress + managed identity
DEFERRED to spec 007 (no new Azure resources here). RUN TRACKING = workflow_run webhook via a YARP
ingress → internal handler container + POLLING RECONCILE for missed deliveries (≤60s sweep, ~2min
settle); MVP closes the loop via polling. REGISTRY = existing platform Postgres, new `registry` schema.
env_id = UUIDv7 surrogate (correlation key) + unique natural key (kind, subscription, name) for
idempotent convergence; ADDED to docs/glossary.md. Single-flight per environment (reject mutating verb
while a run is in flight). Adds fabric-vend.yml (fabric create had no dispatch path) + env_id/mode
inputs on existing env workflows. Observability = Azure Monitor OTel → App Insights, env_id-correlated
(App Insights resource ships with spec-007 host). Deliverable: NEW projects under src/ —
Pdp.ControlPlane.Registry / .Dispatch / .Verbs / .Api / .Ingress, Pdp.Cli, Pdp.AppHost (+ tests).
Stack: .NET 10, ASP.NET Core minimal APIs, Wolverine (+WolverineFx.Postgresql: durable outbox/inbox,
scheduled messages, EF Core saga), EF Core/Npgsql + EFCore.NamingConventions, Octokit + GitHubJwt,
Octokit.Webhooks.AspNetCore, Yarp.ReverseProxy, FluentValidation, Http.Resilience (Polly v8),
System.CommandLine 2.0 GA, Azure.Identity/.ResourceManager (reuses spec-005 Pdp.ControlPlane.Inventory
via its pluggable TokenCredential), Azure.Monitor.OpenTelemetry.AspNetCore, .NET Aspire. TESTS: xUnit +
Shouldly + NSubstitute; Testcontainers.PostgreSql + Respawn (REQUIRED — ledger/registry/saga/outbox not
testable in-memory); WireMock.Net (fake GitHub: dispatch inputs, webhook, missed-webhook→reconcile);
WebApplicationFactory (webhook endpoint). NO prohibited deps (no MediatR/MassTransit/AutoMapper/Moq/
Serilog/FluentAssertions v8+). Platform context: live platform in WEST US 3; specs 002–005 merged
(IPAM ledger + westus3 fabric + app1/app2 spokes); the control plane reaches the PRIVATE Postgres CI
cannot (the Gate-G1 premise).
<!-- SPECKIT END -->
