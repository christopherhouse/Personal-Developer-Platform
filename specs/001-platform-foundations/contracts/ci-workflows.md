# Contract: CI Workflows (platform IaC)

The behavior contract for the rails every IaC change rides (FR-010, FR-011, FR-014,
FR-016). Workflow file names may evolve; triggers, checks, and guarantees may not.

## iac-plan (pull_request)

| Aspect | Contract |
|---|---|
| Trigger | `pull_request` touching `infra/**` (path-filtered) |
| Checks | `tofu fmt -check` (repo-wide), `tofu validate` + `tofu plan` per affected stack |
| Output | Plan rendered on the PR (job summary + comment) — human-readable before merge |
| Gate | All jobs are **required status checks**; failure blocks merge (FR-010) |
| Auth | OIDC via UAMI federated credential, subject `repo:<owner>/<repo>:pull_request` — plan-scope only |
| Secrets | None. `client-id`/`tenant-id`/`subscription-id` are plain variables |

## iac-apply (push to main)

| Aspect | Contract |
|---|---|
| Trigger | `push` to `main` touching `infra/**` |
| Behavior | **Re-plan fresh, then apply that plan** (PR plan is advisory — clarification Q3); applied plan recorded in the run log |
| Concurrency | `concurrency.group = tofu-<stack>`, `cancel-in-progress: false` — applies per state are serialized |
| Failure | Red run + owner notification; subsequent applies for that stack are blocked by a guard until resolved; resolution = fix-forward PR or manual re-run (FR-016). No auto-retry, no auto-revert |
| Auth | OIDC, subject `repo:<owner>/<repo>:ref:refs/heads/main` — apply scope |
| Traceability | Run links to the merged commit/PR (SC-003) |

## foundations-destroy (workflow_dispatch only)

| Aspect | Contract |
|---|---|
| Trigger | Manual `workflow_dispatch` with required input `destroy-confirm` = stack name (Article VIII typed confirmation) |
| Behavior | `tofu plan -destroy` shown, then destroy. Fails on protected resources unless the protection-removal PR has merged first (FR-004) |

## Branch protection (ruleset on main)

- PR required for every change; direct pushes blocked (including admins) — FR-014.
- `iac-plan` checks required to pass.
- Force pushes blocked.
- Configured by the idempotent `scripts/` setup script (research §9).
