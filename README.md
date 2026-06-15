# Personal Developer Platform (PDP)

A personal, AI-operated developer platform for Azure: regional hub-and-spoke network
fabrics, spoke vending, and workload deployment — driven through typed platform verbs
consumed by the `pdp` CLI and the `pdp-mcp` MCP server (chatops via Claude).

See [docs/charter.md](docs/charter.md) for the vision and
[.specify/memory/constitution.md](.specify/memory/constitution.md) for the ten
non-negotiable principles every change must satisfy.

## Repository layout

| Path | What lives here | Owned by spec |
|---|---|---|
| `infra/foundations/` | PDP state backend, CI identity (the platform's roots) | 001-platform-foundations |
| `infra/fabrics/` | Regional hub fabric stacks (one per region) | 003-regional-hub-fabric |
| `infra/modules/` | Hand-rolled shared OpenTofu modules — each carries a justification README (constitution Article V) | as needed |
| `archetypes/` | Workload archetype modules (parameterized solution templates) | 008-workload-archetypes |
| `src/` | Control plane, `pdp` CLI, `pdp-mcp` server (.NET 10) | 006-action-layer, 007-mcp-chatops |
| `.github/workflows/` | CI (plan on PR / apply on merge) + provisioning workflows (the execution plane) | 001, 006 |
| `scripts/` | One-time/idempotent repo setup scripts (e.g., branch protection) | 001-platform-foundations |
| `docs/` | Charter, architecture, tech stack, glossary, [conventions](docs/conventions.md) | — |
| `specs/` | Spec Kit feature specs (spec → plan → tasks per feature) | — |

Every PDP-managed resource follows the naming convention and tag schema published in
[docs/conventions.md](docs/conventions.md) (`<type>-pdp-<region>-<name>`; mandatory
`pdp-*` tags). The seed backend is owner-managed and out of that scope.

## Versioning & pins

- **OpenTofu**: pinned in [`.opentofu-version`](.opentofu-version) — the only IaC
  engine (no Terraform, no Bicep). Enforced by CI and local tooling (e.g., `tenv`).
- **.NET SDK**: pinned in [`global.json`](global.json) (consumed from spec 006 onward).
- **Providers & modules**: constrained per stack, exact builds locked via committed
  `.terraform.lock.hcl` files — version bumps are deliberate, reviewable PR diffs.

## State backends

Two backends, two roles (see
[specs/001-platform-foundations/contracts/state-backend.md](specs/001-platform-foundations/contracts/state-backend.md)):
the owner's **seed** account holds only the foundations stack's state; the **PDP**
backend (created by `infra/foundations/`) holds the state of every other deployable
unit. Both are Entra ID-only — shared-key auth is disabled everywhere, and no cloud
secrets are stored anywhere in this repo or its CI.

## Local development

Documented as part of spec 001 (US4): required tooling, pin enforcement, and how to run
`tofu fmt -check` / `tofu validate` exactly as CI does. See
`infra/foundations/README.md` once the foundations stack lands.
