# Spec Backlog — Personal Developer Platform

Candidate feature specs in proposed build order. Each row becomes a
`/speckit.specify` run. Order matters: each spec consumes the ones above it.

| # | Spec | Scope (one line) | Depends on |
|---|---|---|---|
| 1 | **platform-foundations** | Repo layout, OpenTofu scaffolding + pinned versions, state backend deployment, tag schema, naming convention, CI skeleton (plan on PR / apply on merge). | — |
| 2 | **ipam-ledger** | Control-plane Postgres deployment + IPAM schema: pools/allocations with native `cidr` types, GiST exclusion non-overlap, advisory-lock allocator, supernet-per-region scheme, hub carve-outs, allocation/release operations. | 1 |
| 3 | **regional-hub-fabric** | Deploy/destroy a complete regional hub: hub VNet, egress (firewall decision lives here), private DNS, bastion/management access. | 1, 2 |
| 4 | **spoke-vending** | Atomic spoke creation into any writable subscription: allocation, VNet, cross-sub peering, routes, NSGs, DNS link — and clean teardown. | 2, 3 |
| 5 | **environment-inventory** | Resource Graph-backed inventory across all accessible subscriptions: list fabrics/spokes/workloads/environments, subscription discovery, drift surface. | 1 |
| 6 | **action-layer** | The .NET 10 verb implementation (control plane): typed verbs wrapping specs 2–5, GitHub Actions dispatch (GitHub App, `workflow_dispatch`, `env_id` correlation) + `workflow_run` webhook status tracking, plan/confirm flow, `pdp` CLI front-end with structured output (human + JSON). | 2–5 |
| 7 | **mcp-chatops** | `pdp-mcp`: ASP.NET Core MCP server (streamable HTTP) exposing the verbs as MCP tools, hosted on Azure Container Apps (no APIM), Entra auth, destructive-op confirmation flow, conversational inventory answers. | 6 |
| 8 | **workload-archetypes** | Archetype catalog in the control-plane DB (module path, git tag, parameter JSON schema) + workload template repo, plus the first archetype (e.g., container app + database) deployable into a spoke. | 4, 6 |
| 9 | **multi-region** | Second-region rollout ergonomics, hub↔hub connectivity (if any), region-aware verb behavior. | 3, 4 |
| 10 | **observability-guardrails** | Diagnostics/log routing, Azure Policy for tag/egress enforcement, cost visibility per environment. | 3–5 |

## Status

- **Spec 1 (platform-foundations)** — merged.
- **Spec 2 (ipam-ledger)** — merged.
- **Spec 3 (regional-hub-fabric)** — merged; the live hub fabric runs in **westus3** (migrated from
  eastus2 for Postgres capacity). Two stacks (`infra/fabric` → `fabrics/<region>`,
  `infra/platform-dns` → `platform/dns`) on the spec-001 CI rails.
- **Spec 4 (spoke-vending)** — **complete; US1–US4 all verified live** (2026-06-17). One new
  parameterized stack (`infra/spoke` → `spokes/<sub-id>/<spoke-name>`) + `spoke-vend`/`spoke-destroy`
  dispatch workflows. Vended `app1`+`app2` into a cross-subscription target (peered both sides,
  hub-egressing, NSG'd, DNS-linked, Resource-Graph discoverable), proved idempotent re-vend (FR-009),
  destroyed both cleanly (no dangling hub peering), and proved cross-sub parameterization by plan.
  **Cross-spec fix:** the fabric RG `CanNotDelete` lock blocked spoke teardown's hub-peering delete —
  re-scoped to firewall+bastion (PR #17, spec 003 stack). **Gate-G1 deferral**: `spoke_cidr` is a
  typed input fitted to the region `/16`; live by-size IPAM allocation (ledger write/release) is
  deferred to the **spec-006** control plane (CI can't reach
  the private ledger).
- **Spec 5 (environment-inventory)** — merged (PR #19); live read-only inventory from Azure Resource
  Graph, reused by the spec-006 verb layer.
- **Spec 6 (action-layer)** — **merged** (PR #22, 2026-06-18); build + tests green in CI. The `pdp` CLI
  verb surface, GitHub-App dispatch + `env_id`-correlated tracking (webhook + polling reconcile), the
  Postgres intent registry / run-audit trail, the two-phase plan→confirm / confirm-before-destroy gate,
  and **Gate-G1 closed** (spoke CIDR allocated live by size from the ledger at vend, released on
  destroy). **Live acceptance (quickstart T071/T072) DEFERRED**: the live ledger Postgres is private
  VNet-injected + Entra-only and unreachable from the owner's laptop without VNet access (no `/32`
  firewall possible on a VNet-injected server). It is naturally unblocked by **spec 7**, which hosts the
  control plane in-VNet (ACA + managed identity).

## Notes

- Specs 1–4 are the critical path to "deploy me a spoke in East US 2."
- Spec 7 (chatops) intentionally lands *after* the action layer: the MCP
  server is a thin adapter over verbs that must already exist and be
  trustworthy.
- The list will grow; add candidates here before spinning up a spec so
  dependencies stay visible.
