# Domain Glossary — Personal Developer Platform (PDP)

Use these terms consistently in every spec. If a spec needs a new term, add it
here first.

| Term | Definition |
|---|---|
| **Fabric** | A complete regional hub-and-spoke network deployment: the hub VNet and its shared services (egress, DNS, bastion) for exactly one Azure region. Identified by region (e.g., "the East US 2 fabric"). |
| **Hub** | The central VNet of a fabric, in the platform subscription. Owns all egress, shared DNS, and inbound management access for its region. |
| **Spoke** | A VNet peered to a regional hub, living in any writable subscription. The unit of network isolation that workloads deploy into. Spokes never peer to each other or define their own egress. |
| **Spoke vending** | The automated act of creating a spoke: address allocation, VNet creation, peering, route table, NSGs, DNS link — as one atomic operation. |
| **Workload** | A deployed instance of a solution running inside a spoke (e.g., "the demo API in spoke `app1` in East US 2"). |
| **Workload archetype** | A parameterized, reusable template for a class of solution (e.g., container app + database). Deploying an archetype into a spoke produces a workload. |
| **Environment** | A named workload instance grouping (e.g., `dev`, `demo`). Carried by the `pdp-env` tag; "what environments do I have deployed?" is answered by inventory grouped on this. |
| **Platform subscription** | The single subscription hosting hubs, state backend, IPAM registry, and shared services. |
| **Target subscription** | Any subscription the owner's identity can write to; valid destination for spokes and workloads. Discovered at runtime, never hardcoded. |
| **Verb / action layer** | The typed, deterministic operations the platform exposes (`fabric create`, `spoke create`, `workload deploy`, `env list`, …). Implemented once, consumed by both the `pdp` CLI and the MCP server. |
| **Inventory** | The live, queryable answer to "what does PDP manage?" — derived from Azure Resource Graph over the mandatory tag schema, never from local records alone. |
| **IPAM registry** | The versioned record of address-space allocations (supernets per region, hub carve-outs, spoke blocks). The only authority for assigning CIDR ranges. |
| **AVM** | Azure Verified Modules — Microsoft-maintained IaC modules. PDP's default building blocks (Terraform flavor, run under OpenTofu). |
| **Chatops** | Operating the platform through natural-language conversation with Claude, which calls platform verbs via the `pdp-mcp` MCP server. |
