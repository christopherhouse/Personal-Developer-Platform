# Spec Backlog — Personal Developer Platform

Candidate feature specs in proposed build order. Each row becomes a
`/speckit.specify` run. Order matters: each spec consumes the ones above it.

| # | Spec | Scope (one line) | Depends on |
|---|---|---|---|
| 1 | **platform-foundations** | Repo layout, OpenTofu scaffolding + pinned versions, state backend deployment, tag schema, naming convention, CI skeleton (plan on PR / apply on merge). | — |
| 2 | **ipam-registry** | Address-space registry: supernet-per-region scheme, hub carve-outs, spoke allocation/release, CI validation against live state. | 1 |
| 3 | **regional-hub-fabric** | Deploy/destroy a complete regional hub: hub VNet, egress (firewall decision lives here), private DNS, bastion/management access. | 1, 2 |
| 4 | **spoke-vending** | Atomic spoke creation into any writable subscription: allocation, VNet, cross-sub peering, routes, NSGs, DNS link — and clean teardown. | 2, 3 |
| 5 | **environment-inventory** | Resource Graph-backed inventory across all accessible subscriptions: list fabrics/spokes/workloads/environments, subscription discovery, drift surface. | 1 |
| 6 | **pdp-cli** | The `pdp` action layer: typed verbs wrapping specs 2–5, plan/confirm flow, structured output (human + JSON). | 2–5 |
| 7 | **mcp-chatops** | `pdp-mcp` server exposing the verbs as MCP tools, destructive-op confirmation flow, conversational inventory answers. | 6 |
| 8 | **workload-archetypes** | Archetype catalog format + parameter contract, plus the first archetype (e.g., container app + database) deployable into a spoke. | 4, 6 |
| 9 | **multi-region** | Second-region rollout ergonomics, hub↔hub connectivity (if any), region-aware verb behavior. | 3, 4 |
| 10 | **observability-guardrails** | Diagnostics/log routing, Azure Policy for tag/egress enforcement, cost visibility per environment. | 3–5 |

## Notes

- Specs 1–4 are the critical path to "deploy me a spoke in East US 2."
- Spec 7 (chatops) intentionally lands *after* the CLI: the MCP server is a
  thin adapter over verbs that must already exist and be trustworthy.
- The list will grow; add candidates here before spinning up a spec so
  dependencies stay visible.
