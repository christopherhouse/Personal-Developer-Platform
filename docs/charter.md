# Product Charter — Personal Developer Platform (PDP)

## Vision

A personal, AI-operated developer platform for Azure. PDP lays down regional
hub-and-spoke network fabrics and deploys workloads into them — driven by
conversation. The end state is being able to say:

> "Deploy x, y, and z into a spoke VNet in East US 2"
> "What environments do I have deployed?"

…and have the platform do it correctly, safely, and visibly.

## Who it's for

- **Primary:** the platform owner (a single developer/operator). One human,
  full RBAC trust, no internal multi-tenancy.
- **Secondary (future):** other developers who clone PDP as a template for
  their own personal platform.

## Core capabilities

1. **Regional network fabrics** — stand up a complete hub-and-spoke fabric in
   any Azure region (hub VNet, egress/firewall, DNS, bastion) with one action.
2. **Spoke vending** — create spoke VNets peered into a regional hub, in *any
   subscription the owner can write to*, with addressing, routing, and NSGs
   handled automatically.
3. **Workload deployment** — deploy solutions ("archetypes") into spokes from
   a catalog of parameterized templates.
4. **Conversational operations (chatops)** — all of the above is operable
   through natural language via Claude + an MCP server exposing platform verbs.
5. **Inventory** — the platform can always answer "what do I have deployed,
   where, and what does it cost?" from live Azure state.

## Explicit non-goals

- **Not multi-tenant / not a SaaS.** One owner, one trust boundary.
- **Not a full Azure Landing Zones implementation.** PDP borrows ALZ ideas
  (hub-and-spoke, policy, tagging) at personal scale; it does not replicate
  enterprise management-group hierarchies, identity vending, or compliance
  regimes.
- **Not multi-cloud.** Azure only.
- **No GUI/portal in v1.** The interfaces are chat (MCP) and a CLI. A web UI
  may come later as another consumer of the same action layer.
- **No production SLAs.** This is a personal platform; cost control and clean
  teardown beat high availability.

## Success criteria

- A new regional fabric deploys hands-off in under ~30 minutes from a single
  chat request or CLI command.
- "Deploy \<archetype\> into a spoke in \<region\>" works end-to-end from a
  Claude conversation, including spoke creation if none exists.
- "What environments do I have deployed?" is answered accurately in one chat
  turn from live Azure state — never from stale local records.
- Every fabric, spoke, and workload tears down cleanly: no orphaned resources,
  no leaked address space, no dangling peerings.
- Nothing PDP manages exists outside its inventory (tag-complete, state-tracked).

## Related documents

- [Architecture & tech stack](architecture.md)
- [Domain glossary](glossary.md)
- [Constitution (draft principles)](constitution.md)
- [Spec backlog](spec-backlog.md)
