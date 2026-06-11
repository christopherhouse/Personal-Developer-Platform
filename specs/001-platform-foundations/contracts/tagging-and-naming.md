# Contract: Tagging & Naming

The conventions contract consumed by every resource-creating spec. Authoritative tables
live in [data-model.md](../data-model.md) §1–2; this contract states the rules.

## Tagging rules

1. Every PDP-managed **resource group** carries `pdp-managed = "true"` and
   `pdp-deployed-by ∈ {github-actions, control-plane, owner}`.
2. Scope tags (`pdp-fabric`, `pdp-spoke`, `pdp-workload`, `pdp-env`) are present exactly
   when the scope applies, with values matching the patterns in data-model.md §1 —
   inapplicable tags are **omitted**, never sentinel-valued.
3. Inventory (Article III) queries Resource Graph filtering `tags['pdp-managed'] == 'true'`;
   anything failing that filter is invisible to PDP by definition.
4. Tags are set by IaC only. Hand-edited tags are drift (Article I) — the next plan
   reverts them.

## Naming rules

1. Default pattern: `<type>-pdp-<region>-<name>` (lowercase, hyphens).
2. `<type>` comes from the pinned CAF abbreviation subset (data-model.md §2); new types
   are added to the table by PR before first use.
3. Constrained-name resources use the documented exception pattern
   `<type>pdp<region-short><name><suffix>` with the pinned region-short table.
4. Names are immutable post-creation; a rename is a destroy/recreate decision.

## Conformance

- CI may include an automated naming/tag check (SC-004 measures 100% conformance).
- Azure Policy enforcement of this contract arrives in spec 10; until then, conformance
  is by code review + the plan output.
