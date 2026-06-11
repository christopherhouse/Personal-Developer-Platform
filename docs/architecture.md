# Architecture & Tech Stack — Personal Developer Platform (PDP)

Decisions recorded here are inherited by every spec. Changing one of these is
an architecture change, not a feature change.

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
  (DNS zones, IPAM registry, etc.).
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
  blocks from the remainder. Allocations live in a versioned registry
  (file-based in-repo for v1, validated in CI) — no address space is ever
  assigned ad hoc. Exact scheme is defined in the IPAM spec.

## Action layer (the platform's API)

- A single **`pdp` CLI** is the canonical interface: typed, deterministic
  verbs (`fabric create`, `spoke create`, `workload deploy`, `env list`,
  `… destroy`, etc.) that orchestrate OpenTofu plan/apply and Azure queries.
- **Language:** Python — Typer for the CLI, official MCP Python SDK for the
  server, Azure SDK (Resource Graph, ARM) for queries, `tofu` invoked as a
  subprocess.
- The **MCP server is a thin adapter** over the same verbs the CLI uses. The
  AI never generates or applies IaC directly; it only calls platform verbs.
  One implementation, two front-ends (CLI, chat).

## AI / chatops

- **v1 surface:** MCP server (`pdp-mcp`) + Claude (Claude Code / Claude
  desktop) as the chat client. Runs locally under the owner's credentials.
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
  `pdp-env`, `pdp-deployed-by`. Untagged = unmanaged = invisible to PDP.

## Identity & execution

- Local execution (CLI, MCP server) uses the owner's `az login` context via
  `DefaultAzureCredential`.
- CI (GitHub Actions) uses OIDC federated credentials — no stored secrets.
- CI runs plan on PR and apply on merge for fabric-level changes; spoke and
  workload operations may also run interactively via CLI/MCP, but always
  through the same verbs, state backend, and tag schema.

## Naming

- Cloud Adoption Framework-style abbreviations:
  `<type>-pdp-<region>-<name>` (e.g., `vnet-pdp-eastus2-hub`,
  `rg-pdp-eastus2-fabric`). Exact convention finalized in the
  platform-foundations spec.

## Open questions (to resolve in specs)

- Firewall tier in the hub: Azure Firewall (cost!) vs. NVA vs. NAT Gateway +
  NSGs for a personal-scale "egress through hub" posture.
- IPAM registry format and the supernet-per-region scheme.
- Workload archetype catalog format and parameter contract.
- How ad-hoc (CLI/MCP-initiated) applies reconcile with the git history —
  e.g., auto-commit of vending records.
