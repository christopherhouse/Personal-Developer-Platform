# Phase 0 Research: Platform Foundations

Sources verified live (2026-06-11) via Microsoft Learn MCP and OpenTofu docs (context7):
azurerm backend auth options, state locking semantics, GitHub OIDC federation patterns.

## 1. Bootstrap: two backends — seed anchor + PDP-owned (FR-003)

**Context (owner decision, refined 2026-06-11)**: the owner's existing personal TF
state account (`cmhtfstatesa` in `RG-TF`, sub `8bd05b2f-…`, container `tfstate`,
eastus2, shared keys already disabled — verified live) is the account *used to deploy
PDP*, not PDP's backend. PDP gets its **own** state storage account.

**Decision**: **Seed-anchor bootstrap.**
- The foundations stack's `backend.tf` points at the **seed backend** with key
  `pdp/foundations` (namespaced — the seed account holds the owner's other personal
  states too). The seed is an **external dependency**: never imported, tagged,
  remediated, or destroyed by PDP; PDP needs only `Storage Blob Data Contributor` on
  its `tfstate` container.
- The stack **creates the PDP state backend greenfield**: `rg-pdp-eastus2-foundations`,
  storage account `stpdpeus2state<suffix>` (AVM module — see §5), container `tfstate`,
  full contract config (Entra-only / shared keys disabled, versioning, 30-day blob +
  container soft delete, TLS 1.2, LRS, tags, naming convention).
- **Every other deployable unit** (fabrics, spokes, workloads, future platform stacks)
  stores state in the **PDP backend** per the key registry.

The chicken-and-egg dissolves: the bootstrap state is anchored *outside* the system it
creates, so there is no local-state phase, no migration, and no import. `tofu init`
works against the seed from the very first run.

**Idempotency / partial-failure convergence**: plain plan/apply semantics — re-running
converges (RG created but SA failed → next apply creates the SA). Restart point is
always "re-run plan/apply."

**Failure domains** (why this design is robust): seed lost → only foundations state is
affected (small, re-importable resource set); PDP backend lost → foundations state is
safe in the seed, so re-running foundations recreates the PDP backend (other units'
states are gone — that is the accepted LRS/re-bootstrap posture from clarification Q2).

**Alternatives considered**:
- *Adopt the seed account as THE platform backend (import-based)*: conflates the
  owner's personal account with platform infrastructure — PDP would manage, tag, and
  destroy-protect a resource it doesn't own, and platform state would be hostage to a
  personal account's lifecycle. Rejected (this was the prior draft; corrected by owner).
- *Single new backend with local-state-then-migrate*: workable, but leaves the
  foundations state inside the system it creates and adds a migration step. The seed
  account already exists and is hardened — using it is strictly simpler. Rejected.
- *Leave foundations state local/committed*: secret-leak + single-machine fragility;
  violates FR-003. Rejected.

## 2. State backend authentication: no keys, anywhere

**Decision**: Entra ID data-plane auth only. The storage account disables shared-key
access entirely (`shared_access_key_enabled = false`). The backend block sets
`use_azuread_auth = true`; CI additionally sets `use_oidc = true` (OpenTofu picks up
GitHub's `ACTIONS_ID_TOKEN_REQUEST_*` env vars natively — verified against OpenTofu
azurerm backend docs). Locally, the owner's `az login` context satisfies the backend.
RBAC: CI identity and owner get **Storage Blob Data Contributor** on the state container
scope; CI identity gets **Contributor** on the platform subscription for applies.

**Rationale**: zero stored secrets (FR-012) is a hard requirement and extends to the
data plane; disabling shared keys makes the no-secrets posture structural rather than
behavioral.

**Both backends are Entra-only**: the seed account already has shared-key access
disabled (verified live 2026-06-11 — no remediation needed, and none would be performed
by PDP anyway), and the new PDP account is created with it disabled. RBAC needed:
`Storage Blob Data Contributor` for CI UAMI + owner on the seed's `tfstate` container
(foundations state) and on the PDP account (everything else).

**Alternatives considered**: access key in GitHub secret (violates FR-012); SAS tokens
(rotating secrets, same problem). Rejected.

## 3. State storage configuration (FR-002, FR-015 + Article IX)

**Decision**:
- StorageV2, **Standard LRS**, TLS 1.2 minimum, no anonymous/blob-public access.
- **Blob versioning ON + blob soft delete (30 days) + container soft delete (30 days)**
  — satisfies FR-015's point-in-time recovery window.
- State locking: native blob leases (automatic in the azurerm backend; verified via
  MS Learn) — no extra infrastructure (no DynamoDB-equivalent needed).
- **Public network access ENABLED — explicit Article IX exception.** GitHub-hosted
  runners have no stable egress range and the control plane doesn't exist yet; a private
  endpoint would require self-hosted runners (cost + ops burden, anti-Article IX).
  Compensating controls: Entra-only auth (no keys to leak), no anonymous access, TLS
  1.2+. Revisit when spec 10 (observability-guardrails) or a self-hosted-runner decision
  lands. This is the "spec explicitly requires one" path Article IX provides.

**Alternatives considered**: GRS (rejected in clarification Q2 — re-bootstrap covers
regional loss); storage firewall with GitHub IP allowlist (GitHub publishes ranges but
they churn; brittle, false security). Rejected.

## 4. Destroy protection: the Article IV carve-out (FR-004)

**Decision**: Two layers, both visible in code:
1. **Azure management lock** (`CanNotDelete`) on the state resource group — blocks any
   deletion attempt regardless of tooling.
2. **`lifecycle { prevent_destroy = true }`** on the storage account and state container
   resources — makes `tofu destroy` fail loudly inside the plan itself.

**Protected-resource enumeration** (published in `infra/foundations/README.md` and
data-model.md): the state resource group, the state storage account, the `tfstate`
container, and the management lock itself. Everything else in foundations (CI identity,
role assignments, federated credentials) tears down cleanly.

**Removing protection** = a PR that deletes the lock resource + lifecycle flag (reviewed,
planned, visible), then a confirmed destroy. Satisfies "deliberate, reviewable step".

## 5. Module choice for the PDP state account (Article V)

**Decision**: `Azure/avm-res-storage-storageaccount/azurerm` (pinned exact version) for
the **new, greenfield** PDP state account — AVM-first applies cleanly now that nothing
is imported. First step is the constitution-mandated **smoke validation under OpenTofu
1.11.x** (init/plan/apply/destroy in a scratch RG), recorded in the module-adoption
note in `infra/foundations/README.md`.

The resource group, management lock, UAMI, federated credentials, and role assignments
are plain `azurerm` resources (single-resource primitives, not compositions — no AVM
module fits; justification inline).

**Fallback**: if smoke validation fails under pinned OpenTofu, fall back to plain
`azurerm_storage_account` with the failure recorded as the Article V justification.

## 6. CI identity: user-assigned managed identity + federated credentials

**Decision**: A **user-assigned managed identity** (`id-pdp-eastus2-github-ci`) with two
federated identity credentials, subjects verified against the GitHub OIDC claim format:
- `repo:<owner>/<repo>:pull_request` → plan-only context
- `repo:<owner>/<repo>:ref:refs/heads/main` → apply context

`azure/login` consumes client-id/tenant-id/subscription-id as **plain variables** (none
are secrets). Workflow permission `id-token: write` enables token issuance.

**Rationale**: UAMI over app registration — no client-secret surface at all, no Entra app
to manage, and MS Learn documents both as equivalent for `azure/login` OIDC. The GitHub
App (`pdp-orchestrator`) arrives in spec 6 for dispatch — out of scope here.

**Alternatives considered**: Entra app registration + federated credential (equivalent,
slightly more management surface); PAT/secret auth (violates FR-012). Rejected.

## 7. CI workflow shape (FR-010, FR-011, FR-016)

**Decision**: Two workflows, matrix-ready but single-stack for now:
- **`iac-plan.yml`** — trigger `pull_request` on `infra/**` paths: `tofu fmt -check`
  (whole repo), `tofu validate`, `tofu plan -no-color` per changed stack; plan output
  posted to the PR (job summary + sticky comment). Required status check.
- **`iac-apply.yml`** — trigger `push` to `main` on `infra/**` paths: re-plan fresh and
  `tofu apply` the result (clarification Q3), with the applied plan in the run log.
  `concurrency: { group: tofu-<stack>, cancel-in-progress: false }` serializes applies
  per state (FR-016's "block subsequent applies" — queued runs also fail fast if a prior
  apply failed, via a guard step). Failure → red run + GitHub notification to the owner;
  resolution is fix-forward PR or manual `workflow_dispatch` re-run (FR-016).

**Foundations destroy** is intentionally *not* an automatic workflow: a manual
`workflow_dispatch` job with a typed confirmation input (`destroy-confirm: <stack name>`),
satisfying Article VIII.

## 8. Version-pin mechanics (FR-005, FR-006)

**Decision**:
- `.opentofu-version` at repo root (consumed by `opentofu/setup-opentofu` in CI and by
  local tooling like `tenv`); `global.json` pins .NET 10 SDK.
- Each stack/module declares `required_version` (`~> 1.11.0`) and provider constraints
  (`~> 4.x` azurerm pinned to minor per module); **`.terraform.lock.hcl` committed** per
  stack — provider bumps are lockfile diffs in PRs (FR-006's "explicit file diff").
- AVM module references pin **exact versions** (`version = "X.Y.Z"`, no ranges).
- Renovate-friendly layout noted; actual Renovate config deferred (not required by spec).

## 9. Branch protection (FR-014)

**Decision**: GitHub **ruleset** on `main`: require PR before merge, require status
checks (`iac-plan` jobs) to pass, block force pushes, no direct pushes (applies to the
owner/admin too — "do not bypass" enabled). Applied via a documented one-time `gh api`
script checked into `scripts/` (idempotent), since repo-level GitHub config via an
OpenTofu github provider would drag a new provider + state into scope for one resource.
Trade-off recorded: this is repo *configuration*, not Azure infrastructure; Article I
governs Azure resources, so script-managed GitHub settings don't violate it. Revisit if
GitHub-side config grows.

## 10. Naming + tagging finalization inputs

Decisions themselves live in data-model.md (they are the deliverable). Research notes:
- CAF abbreviation list is the source for `<type>` prefixes (`rg`, `st`, `id`, `vnet`…).
- Storage account names: no hyphens, ≤ 24 chars, globally unique → documented exception
  pattern `st pdp <region-short> <name> <4-char suffix>` (e.g., `stpdpeus2state8d2k`),
  with region-short table (`eastus2` → `eus2`) in the naming contract.
- Tag values: `pdp-managed` boolean-as-string, `pdp-deployed-by` enumerated
  (clarification Q5); platform-plane RGs omit inapplicable scope tags entirely rather
  than carrying placeholder values (cleaner Resource Graph queries than `none` sentinels).
