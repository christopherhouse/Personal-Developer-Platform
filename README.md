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

A fresh clone reaches passing local `fmt`/`validate` with the **same tool versions CI
uses** and no undocumented steps (spec 001, US4 / FR-013). The goal is parity: what
passes here passes in `iac-plan`, for the same commit.

### Baseline tools

| Tool | Why | Notes |
|---|---|---|
| `git` | clone the repo | — |
| **OpenTofu** | the only IaC engine | install the exact version in [`.opentofu-version`](.opentofu-version) (currently `1.11.6`). [`tenv`](https://github.com/tofuutils/tenv) reads that file and auto-selects it (`tenv tofu install`); a manual install of the same version works too. |
| **Azure CLI** (`az`) | backend auth + read-only queries | `az login` (owner context). Needed only for commands that touch the backend (`tofu init` against the seed, `tofu plan`) — **not** for `fmt` or offline `validate`. |
| **GitHub CLI** (`gh`) | PRs, repo variables, branch protection | optional for IaC checks; used by the setup scripts. |

> .NET 10 (pinned in [`global.json`](global.json)) is only needed from spec 006 onward
> (control plane / CLI / MCP) — not for the foundations IaC checks below.

### How pins are enforced

- **OpenTofu engine** → [`.opentofu-version`](.opentofu-version). CI's `setup-opentofu`
  reads this same file (`tofu_version_file: .opentofu-version`), so local and CI run
  byte-identical engines. Use `tenv` locally to honor it automatically.
- **Providers & modules** → committed `.terraform.lock.hcl` per stack (e.g.
  [`infra/foundations/.terraform.lock.hcl`](infra/foundations/.terraform.lock.hcl)).
  `tofu init` installs the exact locked builds; version bumps are deliberate, reviewable
  PR diffs.
- **.NET SDK** → [`global.json`](global.json) (`rollForward: latestFeature`), consumed
  from spec 006.

### Run the checks exactly as CI does

CI (`.github/workflows/iac-plan.yml`) runs two gates. Reproduce them locally:

```powershell
# 1. Format check — repo-wide, offline, no cloud auth (mirrors the CI `fmt` job)
tofu fmt -check -recursive

# 2. Validate — per stack (mirrors the CI `plan` job's init + validate steps)
cd infra/foundations
tofu init -backend=false -input=false   # install locked providers/modules; no cloud access
tofu validate -no-color
```

`-backend=false` lets `validate` run without backend credentials and yields the same
result CI produces after its full OIDC-authenticated `init`. To go further and see a
plan (as the PR does), run `az login` first, then `tofu init` (no `-backend=false`)
against the seed backend followed by `tofu plan` — see
[`infra/foundations/README.md`](infra/foundations/README.md). `tofu apply` never runs
locally outside the one-time bootstrap; all applies ride the CI rails.
