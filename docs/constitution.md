# Constitution (Draft) — Personal Developer Platform

Draft principles to feed into `/speckit.constitution` after `specify init`.
These are non-negotiables; specs that violate them are wrong by definition.

## Article I — Infrastructure is code

All PDP-managed infrastructure is created, changed, and destroyed through
OpenTofu executed by platform verbs. Portal mutations to managed resources are
forbidden; anything changed by hand is treated as drift to be reverted.

## Article II — The AI calls verbs, never improvises

Chatops works exclusively through the typed action layer. The AI never
generates, edits, or applies IaC at runtime, and never runs raw `tofu` or `az`
mutations. New capability = new verb = new spec.

## Article III — Tagged, tracked, or it doesn't exist

Every managed resource group carries the mandatory tag schema. Inventory is
answered from live Azure state (Resource Graph), never from memory or local
records. A resource PDP cannot find in inventory is a bug.

## Article IV — Destroyable by design

Every fabric, spoke, and workload must tear down cleanly with a single verb:
no orphaned resources, no leaked address allocations, no dangling peerings.
"Can it be destroyed?" is part of every spec's acceptance criteria.

## Article V — AVM-first modules

Use Azure Verified Modules where viable; fall back to `azurerm`/`azapi` only
with a recorded justification. Hand-rolled modules are the exception and carry
their own maintenance burden visibly.

## Article VI — No address space without allocation

CIDR ranges come only from the IPAM registry. No VNet or subnet is created
with an unregistered range. Allocation precedes peering, always.

## Article VII — Hub owns egress

Spokes never define their own internet egress or cross-spoke routing. All
egress flows through the regional hub. A spoke that can bypass the hub is
misconfigured.

## Article VIII — Plan before apply, confirm before destroy

Every mutation shows its plan before applying. Destructive operations
(destroy, address reassignment, peering removal) additionally require explicit
human confirmation — including, and especially, when initiated through chat.

## Article IX — Secure and cheap by default

Private by default: no public endpoints unless a spec explicitly requires one.
Cost-conscious by default: prefer the smallest viable SKU; anything expensive
to keep running must be easy to tear down and recreate (see Article IV).

## Article X — Specs before code

Features follow the Spec Kit flow: specify → plan → tasks → implement. The
charter, architecture doc, and glossary are binding context for every spec.
