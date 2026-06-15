# Architecture & Tech Stack — Personal Developer Platform (PDP)

Decisions recorded here are inherited by every spec. Changing one of these is
an architecture change, not a feature change. For the flat tool/version quick
reference, see [tech-stack.md](tech-stack.md) — this doc carries the *why*.

## Infrastructure as Code

- **Tool:** OpenTofu (pinned version, managed via `.opentofu-version` /
  tooling of choice). No raw Terraform; no Bicep.
- **Providers:** `azurerm` primary, `azapi` for resources/properties azurerm
  lags on.
- **Modules:** **AVM-first.** Use Azure Verified Modules (Terraform flavor)
  wherever one fits. Fall back to hand-rolled `azurerm`/`azapi` modules only
  when no AVM module is viable, and record why in the module's README.
  - ⚠️ *Known risk:* AVM officially supports Terraform, not OpenTofu. In
    practice the modules run on OpenTofu, but each adopted module gets a smoke
    validation under our pinned OpenTofu version before it's relied on.
- **State:** Azure Storage backend in the platform subscription. One state per
  deployable unit — per regional hub fabric, per spoke, per workload
  deployment — with a deterministic key convention
  (`fabrics/<region>`, `spokes/<sub-id>/<spoke-name>`,
  `workloads/<sub-id>/<spoke-name>/<workload-name>`). Small blast radius,
  parallel-safe operations.

## Subscription topology

- **Platform subscription:** hosts all regional hubs (one resource group per
  regional fabric), the state backend, and shared platform services
  (DNS zones, the control plane on Container Apps, the control-plane
  Postgres, etc.).
- **Target subscriptions:** spokes and workloads deploy into *any subscription
  the owner's identity can write to*. "Available subscriptions" is discovered
  at runtime (ARM subscription list filtered by writable RBAC), never
  hardcoded.
- Cross-subscription hub↔spoke peering is therefore a first-class, normal case.

## Networking model

- Classic hub-and-spoke per region. Spokes peer to their regional hub only.
- **All egress through the hub.** Spokes get a default route to the hub's
  firewall/NVA; spokes never define their own internet egress.
- Private DNS is centralized in the platform subscription and linked to spokes
  at vending time.
- **IPAM:** every regional fabric owns a supernet (e.g., a /16 per region);
  the hub takes a fixed carve-out and spokes are allocated non-overlapping
  blocks from the remainder. Allocations live in the **control-plane Postgres**
  using native `cidr` columns; non-overlap is enforced *by the database* with
  a GiST exclusion constraint (`EXCLUDE USING gist (pool_id WITH =, cidr
  inet_ops WITH &&)` over live allocations) and allocation is serialized per
  pool with `pg_advisory_xact_lock`. No address space is ever assigned ad hoc.
  Azure-native IPAM (AVNM) was evaluated and rejected — we own the ledger.
  Exact pool scheme is defined in the IPAM spec.

## Action layer (the platform's API)

- A single set of typed, deterministic **verbs** (`fabric create`,
  `spoke create`, `workload deploy`, `env list`, `… destroy`, etc.)
  implemented once and consumed by two front-ends: the `pdp` CLI and the
  `pdp-mcp` MCP server.
- **Language:** .NET 10 (no Python) — ASP.NET Core minimal APIs for the
  control-plane service, the official MCP C# SDK for the server,
  System.CommandLine for the CLI, `Azure.ResourceManager` for queries.
- The verb implementation is a **control plane, not an executor**. A mutating
  verb validates the request against the archetype catalog, allocates address
  space from the IPAM ledger, records intent in Postgres, and **dispatches a
  GitHub Actions workflow** that performs the actual OpenTofu plan/apply.
  OpenTofu never runs inside the control-plane process.
- The **MCP server is a thin adapter** over the same verbs the CLI uses. The
  AI never generates or applies IaC directly; it only calls platform verbs.
  One implementation, two front-ends (CLI, chat).

## Control-plane data (Postgres)

- One **Azure Database for PostgreSQL Flexible Server** in the platform
  subscription is the solution-scoped store, holding four things:
  the **environment registry** (owner, status lifecycle
  `requested → provisioning → active → destroying → destroyed`, archetype +
  version, parameters), **provisioning runs** (audit trail correlated to
  GitHub Actions run IDs/URLs), the **archetype catalog** (module path,
  git tag, parameter JSON schema — which doubles as verb input validation),
  and the **IPAM ledger** (pools + allocations, see Networking model).
- Division of truth: Postgres records **intent and allocation**; live Azure
  via Resource Graph remains the truth for **what's deployed** (inventory).
- Resources created outside the platform never integrate with it — accepted
  by design; there is no reconciliation loop.

## Execution plane (GitHub Actions)

- **All applies and destroys run as GitHub Actions workflows** in this
  (platform) repo — never on a laptop, never in the control plane.
- **Kick-off:** the control plane authenticates as a **GitHub App** and calls
  `workflow_dispatch` with typed inputs (`env_id`, allocated CIDR, archetype +
  version tag, region, …). The workflow's `run-name` embeds `env_id` for
  correlation, since the dispatch API returns no run ID.
- **Monitoring:** the GitHub App delivers `workflow_run` webhooks to the
  control plane, which updates run + environment status in Postgres. Status
  queries (`env list`, chat questions) read Postgres and link to the live
  Actions run. No polling in the happy path.
- **No IaC generation at provision time** — not by the tool, not by the
  model. Workflows marry pinned catalog module versions (git tags) to
  per-run tfvars built from dispatch inputs: templates are code,
  environments are rows. New archetypes enter only as reviewed PRs to this
  repo. Nothing per-environment is committed to git; Postgres and OpenTofu
  state are the records.
- Workload app repos are stamped from a **template repo** and consume this
  repo's reusable workflows for deploys; fabric IaC never lives in workload
  repos.

## AI / chatops

- **v1 surface:** MCP server (`pdp-mcp`) + Claude (Claude Code / Claude
  desktop) as the chat client. The server is **remote**: ASP.NET Core with
  streamable HTTP transport, hosted on **Azure Container Apps** in the
  platform subscription (vnet-integrated, scale-to-zero). **No APIM in
  front** — auth is handled by the server itself (Entra ID; exact flow
  resolved in the mcp-chatops spec).
- **Guardrails:** destructive verbs (destroy, detach, reassign address space)
  require explicit confirmation; plan/preview output is surfaced to the user
  before apply for anything that mutates infrastructure.
- Teams/Slack bot or web chat are possible later consumers of the same action
  layer — out of scope for v1.

## Inventory & tagging

- **Source of truth for "what's deployed" is live Azure**, queried via Azure
  Resource Graph across all accessible subscriptions — not local state files.
- Mandatory tag schema on every PDP-managed resource group (hyphenated keys):
  `pdp-managed`, `pdp-fabric` (region), `pdp-spoke`, `pdp-workload`,
  `pdp-env`, `pdp-deployed-by`. Untagged = unmanaged = invisible to PDP. The
  finalized schema (allowed values, scope applicability, omission rule) is
  published in [conventions.md](conventions.md) §2.

## Identity & execution

- The hosted control plane (ACA) runs under a **managed identity** for
  Postgres and Resource Graph access; its GitHub App credentials are the only
  non-Azure secret in the system.
- Provisioning workflows (GitHub Actions) use **OIDC federated credentials**
  to reach Azure — no stored cloud secrets anywhere.
- Local `pdp` CLI use runs under the owner's `az login` context via
  `DefaultAzureCredential` for read/inventory paths and calls the control
  plane for mutations — so every apply, however initiated, flows through the
  same verbs, dispatch path, state backend, and tag schema.
- CI also runs plan on PR / apply on merge for changes to the platform's own
  IaC (hubs, control plane, state backend).

## Naming

- Cloud Adoption Framework-style abbreviations:
  `<type>-pdp-<region>-<name>` (e.g., `vnet-pdp-eastus2-hub`,
  `rg-pdp-eastus2-fabric`). The finalized convention — pinned abbreviation
  subset, region-short table, and constrained-name exception — is published in
  [conventions.md](conventions.md) §1.

## Open questions (to resolve in specs)

- Firewall tier in the hub: Azure Firewall (cost!) vs. NVA vs. NAT Gateway +
  NSGs for a personal-scale "egress through hub" posture.
- Supernet-per-region scheme details (pool sizes, hub carve-out convention) —
  the IPAM *storage* decision (Postgres ledger) is made; the addressing plan
  itself lands in the IPAM spec.
- Archetype parameter contract details (JSON schema conventions, defaults).
- MCP server auth specifics: Entra app registration, OAuth
  protected-resource metadata, local-client sign-in flow.

*Resolved since first draft:* IPAM registry format (→ Postgres ledger, not
file-based); how ad-hoc applies reconcile with git (→ there are no ad-hoc
applies; everything executes via dispatched workflows, and no per-env records
are committed).
