# Implementation Plan: Platform Foundations

**Branch**: `001-platform-foundations` | **Date**: 2026-06-11 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-platform-foundations/spec.md`

## Summary

Bootstrap the platform repo with a **two-backend design**: the `foundations` OpenTofu
stack keeps its own state in the owner's existing **seed backend** (`cmhtfstatesa` /
`RG-TF` — external anchor, never PDP-managed) and **creates PDP's own state backend
greenfield** (new RG + storage account per naming convention, AVM module, Entra-ID-only
auth — shared keys disabled, versioning + soft delete 30d) for every other deployable
unit. Repository layout and version-pin mechanics are established, the naming convention
and `pdp-*` tag schema are finalized as published contracts, and GitHub Actions CI
delivers fmt/validate + plan on PR and re-plan + apply on merge via OIDC federated
credentials on a user-assigned managed identity. Zero stored secrets is a hard
requirement end to end. Branch protection makes the PR path the only path.

## Technical Context

**Language/Version**: OpenTofu 1.11.x (pinned via `.opentofu-version`); .NET 10 pin
(`global.json`) laid down now for later specs — no .NET code in this feature

**Primary Dependencies**: `azurerm` ~> 4.x, `azapi` ~> 2.x (escape hatch, unused here),
AVM module `Azure/avm-res-storage-storageaccount/azurerm` for the new PDP state account
(smoke-validated under pinned OpenTofu per Article V), GitHub Actions (`azure/login`
with OIDC). Seed backend is consumed as configuration only — no resources for it

**Storage**: Two Azure Storage backends — **seed** (`cmhtfstatesa`, owner-managed,
consumed for the single foundations state blob) and **PDP** (created by this feature:
versioning + 30d soft delete, LRS, shared-key auth disabled, Entra ID data-plane auth
only; holds all other unit states)

**Testing**: `tofu fmt -check`, `tofu validate` (local + CI parity); AVM smoke validation;
quickstart.md acceptance walkthrough mapping to SC-001…SC-008

**Target Platform**: Azure (platform subscription, East US 2 initial region); GitHub
(repo, Actions, branch protection)

**Project Type**: Infrastructure monorepo bootstrap (IaC stacks + CI; no application code)

**Performance Goals**: Bootstrap ≤ 30 min owner time (SC-001); plan visible on every IaC
PR (SC-002)

**Constraints**: No stored cloud secrets anywhere (FR-012); state backend protected from
accidental destroy (FR-004); state data recoverable via versioning + soft delete (FR-015);
fail-red/fix-forward apply contract (FR-016)

**Scale/Scope**: Single owner, one platform subscription, one region initially; rails
consumed by specs 2–10

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Article | Gate | Status |
|---|---|---|
| I — Infrastructure is code | All PDP resources created via OpenTofu. The seed backend is an external dependency (owner-managed, pre-PDP) — PDP only stores a state blob in it, analogous to GitHub itself: consumed, not managed | ✅ PASS |
| II — AI calls verbs | No runtime AI surface in this feature | ✅ N/A |
| III — Tagged, tracked | Tag schema finalized here (FR-008); every foundation RG tagged and Resource-Graph-discoverable (FR-009) | ✅ PASS |
| IV — Destroyable by design | Clean teardown for all foundation resources **except** the enumerated state-backend carve-out, which the spec itself mandates (FR-004) and the plan documents (see research.md §4) | ✅ PASS (spec-sanctioned carve-out, not a violation) |
| V — AVM-first | New PDP state account via `Azure/avm-res-storage-storageaccount/azurerm` (greenfield), smoke-validated under pinned OpenTofu first; RG/lock/UAMI/role primitives are plain `azurerm` (no AVM composition fits — justification inline) | ✅ PASS |
| VI — IPAM allocation | No networks created in this feature | ✅ N/A |
| VII — Hub owns egress | No spokes/egress in this feature | ✅ N/A |
| VIII — Plan before apply, confirm destroy | Plan on every PR (FR-010); apply only after merge of a reviewed PR (FR-014); foundations destroy is a manual, confirmed workflow — never automatic | ✅ PASS |
| IX — Secure and cheap | Shared-key auth disabled, Entra-only data plane, TLS 1.2+, LRS, Standard tier. **Explicit public-endpoint requirement**: the state storage endpoint must be reachable by GitHub-hosted runners; recorded as a deliberate, justified exception with compensating controls (Entra-only auth, no anonymous access) — see research.md §3 | ✅ PASS (exception explicitly required & justified) |
| X — Specs before code | This plan derives from the ratified spec | ✅ PASS |

**Post-design re-check (after Phase 1)**: no design artifact introduced new gates or
violations. The two deliberate exceptions (Article IV carve-out, Article IX public
endpoint) are both explicit, enumerated, and carry compensating controls. PASS.

## Project Structure

### Documentation (this feature)

```text
specs/001-platform-foundations/
├── plan.md              # This file
├── research.md          # Phase 0: bootstrap approach, auth model, protection design
├── data-model.md        # Phase 1: tag schema, naming convention, state keys, pins
├── quickstart.md        # Phase 1: validation walkthrough (maps to SC-001…SC-008)
├── contracts/
│   ├── state-backend.md       # Backend config + state key contract
│   ├── tagging-and-naming.md  # Tag schema + naming convention contract
│   └── ci-workflows.md        # CI trigger/check/concurrency contract
└── tasks.md             # Phase 2 (/speckit-tasks — not created by this command)
```

### Source Code (repository root)

This feature *defines* the repository layout (FR-001). The delivered structure:

```text
infra/                       # All platform-plane IaC stacks (one state each)
├── foundations/             # THIS SPEC: PDP state backend, CI identity
│   ├── main.tf              # New PDP state RG/SA/container (AVM), lock, UAMI
│   ├── backend.tf           # azurerm backend → SEED account (cmhtfstatesa), key pdp/foundations
│   ├── variables.tf
│   ├── outputs.tf
│   └── README.md            # Bootstrap procedure, seed-backend dependency, protected list
├── fabrics/                 # Regional hub stacks (spec 3) — placeholder .gitkeep
└── modules/                 # Hand-rolled shared modules (each needs justification README)

archetypes/                  # Workload archetype modules (spec 8) — placeholder

src/                         # Control plane / CLI / MCP server (specs 6–7) — placeholder

.github/
└── workflows/
    ├── iac-plan.yml         # PR: fmt-check, validate, plan → PR comment/job summary
    ├── iac-apply.yml        # main: re-plan + apply (concurrency-guarded)
    └── (provisioning workflows arrive with spec 6)

docs/                        # Charter, architecture, glossary, conventions
.opentofu-version            # OpenTofu pin (1.11.x)
global.json                  # .NET 10 pin (consumed from spec 6 onward)
```

**Structure Decision**: A single platform monorepo (per architecture.md "platform repo").
`infra/` holds per-stack roots with isolated state; `src/` and `archetypes/` are
placeholder homes so later specs add content without restructuring (FR-001). Workload
app repos are separate (stamped from a template repo, spec 8) and never hold fabric IaC.

## Complexity Tracking

No constitution violations requiring justification. The two deliberate exceptions
(Article IV destroy-protection carve-out; Article IX reachable state endpoint) are
spec-mandated or explicitly required and documented above and in research.md — they are
recorded decisions, not unjustified complexity.
