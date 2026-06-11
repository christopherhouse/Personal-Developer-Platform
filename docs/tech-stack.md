# Tech Stack — Personal Developer Platform (PDP)

The flat list of what we build with. The *why* behind these choices lives in
[architecture.md](architecture.md); this doc is the quick reference specs cite.
Versions marked **pinned** are enforced in config/CI; others track latest.

## Infrastructure as Code

| Tool | Version | Role |
|---|---|---|
| OpenTofu | **1.11.x** (pinned, currently 1.11.6) | The only IaC engine. No raw Terraform, no Bicep. |
| `azurerm` provider | latest 4.x (pinned per module) | Primary Azure provider. |
| `azapi` provider | latest 2.x (pinned per module) | Escape hatch for resources/properties azurerm lags on. |
| Azure Verified Modules (Terraform flavor) | per-module pins | Default building blocks; AVM-first per [constitution Article V](constitution.md). Each adopted module smoke-tested under our OpenTofu version. |

## Platform code (action layer)

| Tool | Version | Role |
|---|---|---|
| .NET | **10 (LTS)**, pinned via `global.json` | Language/runtime for the control plane, `pdp` CLI, and MCP server. No Python. |
| ASP.NET Core minimal APIs | 10.x | Control-plane service: verb endpoints + GitHub webhook receiver. |
| MCP C# SDK (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) | latest | `pdp-mcp` server exposing platform verbs as tools over streamable HTTP. |
| `Microsoft.Identity.Web` | latest | Entra ID bearer-token validation on the MCP server / control plane; paired with OAuth protected-resource metadata (RFC 9728) so MCP clients discover sign-in. |
| Wolverine (`WolverineFx` + `WolverineFx.Postgresql`) | latest | Command bus + durable messaging on the control-plane Postgres: durable outbox for webhook processing, scheduled messages (TTL/lease reaper), retries with backoff, sagas for the environment lifecycle. |
| EF Core + Npgsql | 10.x / latest | Data access to the control-plane Postgres; native `cidr` ↔ `IPNetwork` mapping for IPAM. |
| `EFCore.NamingConventions` | latest | snake_case tables/columns on the Postgres side. |
| System.CommandLine | latest | `pdp` CLI front-end over the same verbs. |
| `Azure.Identity` | latest | Auth everywhere — `DefaultAzureCredential` locally, managed identity on ACA. |
| `Azure.ResourceManager.*` | latest | Resource Graph inventory queries, subscription discovery, RG operations. |
| Octokit + `GitHubJwt` | latest | GitHub App auth (JWT → installation token) and `workflow_dispatch` calls. |
| `Octokit.Webhooks.AspNetCore` | latest | Webhook endpoint: HMAC signature validation + typed `workflow_run` payloads. |
| `JsonSchema.Net` (json-everything) | latest | Validates env-request parameters against the archetype catalog's JSON schema — the golden-path contract enforced at the front door. |
| FluentValidation | latest | Ordinary verb input validation. |
| `Microsoft.Extensions.Http.Resilience` | latest | Polly v8 retry/timeout policies on outbound HTTP (GitHub API). |
| OpenTelemetry + `Azure.Monitor.OpenTelemetry.AspNetCore` | latest | Tracing/metrics/logs via built-in `ILogger`; no Serilog. |
| .NET Aspire | latest | Local dev orchestration: Postgres container + control plane + dashboard in one F5. |
| .NET analyzers + `dotnet format` | built-in | Lint + format. |

## Testing

| Tool | Role |
|---|---|
| xUnit | Test framework. |
| `Testcontainers.PostgreSql` | Throwaway real Postgres per test run — required: the IPAM GiST exclusion constraint and advisory-lock allocator can't be tested in-memory. |
| Respawn | Fast DB reset between integration tests. |
| `Microsoft.AspNetCore.Mvc.Testing` | In-process API/MCP endpoint tests via `WebApplicationFactory`. |
| NSubstitute | Mocks (chosen over Moq). |
| Shouldly | Assertions (chosen over FluentAssertions v8+, which is commercial-licensed). |
| WireMock.Net | Fake GitHub API: assert dispatch inputs, simulate webhook deliveries. |

### Deliberately not used

MediatR, MassTransit, AutoMapper (all moved to commercial licensing; Wolverine
and hand-mapping cover the needs), Moq (SponsorLink trust damage), Serilog
(built-in logging + OTel suffices). Specs should not introduce these.

## AI / chatops

| Tool | Role |
|---|---|
| Claude (Claude Code / Claude desktop) | The chat client; no hosted bot infra in v1. |
| MCP (Model Context Protocol) | The contract between Claude and platform verbs. |

## Azure services (platform plane)

| Service | Role |
|---|---|
| Azure Container Apps | Hosts the control plane / `pdp-mcp` server: vnet-integrated, internal Postgres access, scale-to-zero. No APIM in front. |
| Azure Database for PostgreSQL Flexible Server | Solution-scoped control-plane DB: environment registry, provisioning runs, archetype catalog, and the IPAM ledger (native `cidr` types, GiST exclusion for non-overlap). Burstable SKU. |
| Azure Storage (blob) | OpenTofu state backend, one state per deployable unit. |
| Azure Resource Graph | Source of truth for inventory ("what's deployed?"). |
| Azure Private DNS | Centralized DNS zones, linked to spokes at vending. |
| VNet peering (cross-subscription) | Hub↔spoke connectivity. |
| Azure Policy | Tag and egress guardrail enforcement (spec #10). |
| Hub egress | **Undecided** — NAT Gateway + NSGs vs. Azure Firewall Basic; resolved in the regional-hub-fabric spec. |

## Delivery & dev tooling

| Tool | Version | Role |
|---|---|---|
| GitHub Actions | — | CI/CD **and the execution plane**: plan on PR / apply on merge for platform changes; env provision/destroy workflows dispatched by the control plane (`workflow_dispatch` via GitHub App), status reported back via `workflow_run` webhooks. Auth via OIDC federated credentials — no stored secrets. |
| GitHub App (`pdp-orchestrator`) | — | Identity the control plane uses to dispatch workflows and receive webhooks; short-lived installation tokens, no PATs. |
| Azure CLI (`az`) | 2.83+ | Local auth context; ad-hoc queries. |
| Specify CLI (Spec Kit) | 0.9.x | Spec-driven development workflow. |
| git | — | Version control. Catalog modules are versioned by git tag; no per-environment records are committed — Postgres is the registry. |

## Version policy

- OpenTofu and .NET get explicit pins (`.opentofu-version`, `global.json`)
  because version drift there breaks state and tooling.
- Providers and AVM modules pin per module with renovate-friendly constraints;
  bumps are deliberate PRs, never silent.
- Exact pin mechanics are defined in the **platform-foundations** spec.
