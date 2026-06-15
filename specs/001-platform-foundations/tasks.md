# Tasks: Platform Foundations

**Input**: Design documents from `/specs/001-platform-foundations/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Not requested as separate test tasks; each user story phase ends with its
quickstart validation scenario(s) as acceptance checks.

**Organization**: Tasks grouped by user story (US1 backend bootstrap, US2 conventions,
US3 CI rails, US4 fresh clone) so each story is an independently testable increment.

**✅ SEED backend identifiers received & verified live (2026-06-11)** — this is the
owner's personal TF state account that *deploys* PDP (holds only the foundations
stack's state, key `pdp/foundations`). It is **not** PDP's backend — the foundations
stack creates PDP's own state account greenfield (see contracts/state-backend.md).

| Field | Value |
|---|---|
| Subscription ID | `8bd05b2f-62c5-4def-9869-f0617ebb3970` |
| Seed resource group | `RG-TF` (eastus2 — owner-managed, NOT PDP-tagged/managed) |
| Seed storage account | `cmhtfstatesa` (StorageV2, Standard_LRS) |
| Seed container | `tfstate` |

**Verified seed config** (via `az`, read-only): `allowSharedKeyAccess: false` ✅
(Entra-only — `use_azuread_auth` works out of the box), TLS 1.2 ✅, blob public access
disabled ✅, versioning ✅, 7-day soft delete (accepted as-is for the one foundations
blob — PDP performs **zero** remediation on the seed). The new PDP state account is
created with the full 30-day contract config.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: US1–US4 per spec.md

---

## Phase 1: Setup (repo skeleton & pins)

**Purpose**: The repository layout (FR-001) and version pins (FR-005) everything else
lands on.

- [X] T001 Create repository layout per plan.md: `infra/foundations/`,
      `infra/fabrics/.gitkeep`, `infra/modules/.gitkeep`, `archetypes/.gitkeep`,
      `src/.gitkeep`, `.github/workflows/`, `scripts/`
- [X] T002 [P] Write `.opentofu-version` (pin OpenTofu 1.11.6) at repo root
- [X] T003 [P] Write `global.json` pinning .NET 10 SDK (`rollForward: latestFeature`) at
      repo root
- [X] T004 [P] Add repo-root `README.md` section documenting the layout map (what lives
      where, which specs own which directories) per FR-001

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: Stack scaffolding and the owner inputs every user story depends on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T005 Scaffold the foundations stack in `infra/foundations/`: `versions.tf` with
      `required_version = "~> 1.11.0"`, `azurerm ~> 4.77.0` provider block
      (minor-pinned against registry latest, verified 2026-06-11), provider
      `features {}` + `storage_use_azuread = true` per research.md §8
- [X] T006 [P] Create `infra/foundations/variables.tf` with region/tag/naming locals per
      data-model.md §1–2 (seed identifiers are backend config, not variables — they
      live only in `backend.tf`)
- [X] T007 [P] Create `infra/foundations/outputs.tf` exposing the PDP backend
      identifiers (RG, account name, container) and CI identity client ID — committed
      as documented stubs until the resources land in Phase 3 (keeps `tofu validate`
      green)
- [X] T008 Write `infra/foundations/backend.tf` with the verified seed identifiers
      (table at top of this file): backend azurerm → `RG-TF` / `cmhtfstatesa` /
      `tfstate`, key `pdp/foundations`, `use_azuread_auth = true` — verified working
      Entra-only 2026-06-11

**Checkpoint**: Stack scaffolding ready; owner identifiers in hand.

---

## Phase 3: User Story 1 — PDP state backend bootstrapped from the seed (P1) 🎯 MVP

**Goal**: Foundations state anchored in the seed backend; PDP's own state backend
created greenfield with full contract config and the Article IV protection carve-out.

**Independent Test**: quickstart.md Scenario 1 — bootstrap completes ≤ 30 min, re-run
plan is a no-op, PDP account config verified via `az storage account show`.

- [X] T009 [US1] Smoke-validate `Azure/avm-res-storage-storageaccount/azurerm` (pinned
      exact version) under OpenTofu 1.11.x: init/plan/apply/destroy in a scratch RG;
      record result in the module-adoption note in `infra/foundations/README.md`
      (Article V; fallback to plain `azurerm_storage_account` if it fails —
      research.md §5)
- [X] T010 [US1] Define the PDP state backend in `infra/foundations/main.tf`:
      `rg-pdp-eastus2-foundations` (universal tags per data-model.md §1), storage
      account `stpdpeus2state<suffix>` via the AVM module (versioning on,
      blob+container soft delete 30d, `shared_access_key_enabled = false`, TLS 1.2
      min, no public blob access, LRS), `tfstate` container, with
      `lifecycle { prevent_destroy = true }` on account + container (research.md §3–4)
- [X] T011 [US1] Add `CanNotDelete` management lock `lock-pdp-eastus2-foundations` on
      the PDP state RG in `infra/foundations/main.tf` (FR-004)
- [X] T012 [US1] Grant data-plane RBAC in `infra/foundations/main.tf`: owner `Storage
      Blob Data Contributor` on the PDP state container (CI identity roles arrive with
      US3/T020). Owner's seed-container data access was verified 2026-06-11 via
      `az storage container list --account-name cmhtfstatesa --auth-mode login`
      (succeeded); re-verify with the same command and document in the README that seed
      RBAC is owner-managed/out-of-band
- [X] T013 [US1] Write `infra/foundations/README.md`: bootstrap procedure (init against
      seed → review plan: creates only → apply), seed-backend external-dependency note
      (identifiers, zero-remediation rule, container-RBAC requirement), partial-failure
      restart guidance, both failure domains (seed lost / PDP backend lost), protected
      -resource enumeration, AVM module-adoption note (research.md §1, §4, §5)
- [X] T014 [US1] Execute bootstrap: `tofu init` (seed backend), review plan (creates
      only — zero changes to `RG-TF`/`cmhtfstatesa`), `tofu apply`; verify
      `pdp/foundations` blob in the seed container and the PDP account's contract
      config (`allowSharedKeyAccess: false`, versioning, 30d soft delete)
- [X] T015 [P] [US1] Commit `infra/foundations/.terraform.lock.hcl` (FR-006 pin
      mechanics)
- [X] T016 [US1] Validate quickstart.md Scenario 1: re-run `tofu plan` → no changes
      (idempotency); seed-dependency + failure domains documented in README (SC-001)

**Checkpoint**: PDP backend live and protected; foundations anchored in the seed — MVP
delivered.

---

## Phase 4: User Story 2 — Conventions finalized: naming & tags (P2)

**Goal**: The naming convention and tag schema published as the binding conventions doc;
all PDP resources conform (the seed backend is out of naming/tagging scope by design).

**Independent Test**: quickstart.md Scenario 2 — Resource Graph inventory returns the
state RG with universal tags; created resources pass the naming check.

- [X] T017 [P] [US2] Write `docs/conventions.md` publishing the naming convention
      (pattern, pinned CAF abbreviation subset, region-short table, constrained-name
      exception, worked examples) and the tag schema (per-tag values, scope
      applicability, omission rule) from data-model.md §1–2 (FR-007, FR-008)
- [X] T018 [P] [US2] Add "conventions" cross-references: link `docs/conventions.md` from
      `docs/architecture.md` (Naming + Inventory sections) and repo-root `README.md`
- [X] T019 [US2] Validate quickstart.md Scenario 2: run the Resource Graph query
      (`tags['pdp-managed'] == 'true'`), confirm `rg-pdp-eastus2-foundations` returns
      with `pdp-managed` + `pdp-deployed-by` and no scope tags, and confirm the seed RG
      (`RG-TF`) is **not** returned; verify created resource names against
      `docs/conventions.md` (SC-004)

**Checkpoint**: Conventions are published contracts; inventory finds everything.

---

## Phase 5: User Story 3 — CI rails: plan on PR, apply on merge, no secrets (P2)

**Goal**: OIDC-authenticated CI delivering fmt/validate + plan on PR, re-plan + apply on
merge, fail-red/fix-forward, with branch protection making PRs the only path.

**Independent Test**: quickstart.md Scenario 3 — PR shows the plan, direct push
rejected, merge applies, `gh secret list` shows zero cloud credentials.

- [ ] T020 [US3] Add CI identity to `infra/foundations/main.tf`: UAMI
      `id-pdp-eastus2-github-ci`, federated credentials for subjects
      `repo:<owner>/<repo>:pull_request` and `repo:<owner>/<repo>:ref:refs/heads/main`,
      role assignments (Contributor on platform subscription; Storage Blob Data
      Contributor on **both** the seed `tfstate` container — foundations state — and
      the PDP state container) per research.md §2, §6
- [ ] T021 [US3] Apply T020 via the bootstrap-era local flow one last time (`tofu plan`
      review → `apply`), then record UAMI client ID output
- [ ] T022 [US3] Set GitHub **variables** (not secrets) via `gh variable set`:
      `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (FR-012)
- [ ] T023 [P] [US3] Create `.github/workflows/iac-plan.yml` per
      contracts/ci-workflows.md: `pull_request` on `infra/**`, jobs: fmt-check
      (repo-wide), validate + plan per stack, plan to job summary + PR comment,
      `permissions: id-token: write`, `azure/login` OIDC, setup-opentofu reading
      `.opentofu-version`
- [ ] T024 [P] [US3] Create `.github/workflows/iac-apply.yml` per
      contracts/ci-workflows.md: `push` to `main` on `infra/**`, re-plan fresh + apply,
      `concurrency: tofu-foundations` (no cancel-in-progress), failed-state guard step
      (FR-016: red run blocks subsequent applies until resolved), run links merged
      commit
- [ ] T025 [P] [US3] Create `.github/workflows/foundations-destroy.yml`:
      `workflow_dispatch` with required `destroy-confirm` input matching the stack name,
      `tofu plan -destroy` then destroy (Article VIII typed confirmation)
- [ ] T026 [US3] Write idempotent `scripts/setup-branch-protection.ps1` using `gh api`:
      ruleset on `main` — require PR, require `iac-plan` status checks, block force
      pushes, no admin bypass (FR-014, research.md §9); run it
- [ ] T027 [US3] Validate quickstart.md Scenario 3: PR with a tag-value change → plan
      visible + merge-blocked until green; direct push rejected; merge → apply lands the
      change; `gh secret list` audit = zero cloud secrets (SC-002, SC-003, SC-005)
- [ ] T028 [US3] Validate quickstart.md Scenario 5 (failure contract): force one failing
      apply, confirm red run + blocked follow-up + fix-forward recovery (FR-016)

**Checkpoint**: All IaC changes ride the rails; zero stored secrets verified.

---

## Phase 6: User Story 4 — Fresh clone is productive immediately (P3)

**Goal**: A fresh clone reaches passing local fmt/validate with documented, enforced
tool versions matching CI.

**Independent Test**: quickstart.md Scenario 4 — clean machine, documented setup, local
results match CI for the same commit.

- [ ] T029 [P] [US4] Add "Local development" section to repo-root `README.md`: baseline
      tools (git, az CLI, tenv/OpenTofu), how pins are enforced (`.opentofu-version`,
      `global.json`, lockfiles), how to run `tofu fmt -check` / `tofu validate` exactly
      as CI does (FR-013)
- [ ] T030 [US4] Validate quickstart.md Scenario 4: fresh clone on a clean environment →
      fmt/validate pass with no undocumented steps; compare against CI results for HEAD
      (SC-006)

**Checkpoint**: All four stories independently validated.

---

## Phase 7: Polish & Cross-Cutting

**Purpose**: Protection verification, cleanup, and rails-ready confirmation.

- [ ] T031 Validate quickstart.md Scenario 6 (protection): run `foundations-destroy`
      without removing protection → destroy blocked by `prevent_destroy` + lock; blocked
      list matches the README enumeration exactly (SC-007, FR-004)
- [ ] T032 Post-bootstrap PR: replace the `<stpdpeus2state-suffix>` placeholder with
      the actual generated PDP account name in
      `specs/001-platform-foundations/contracts/state-backend.md` and
      `infra/foundations/README.md` — rides the CI rails as its own PR (also exercises
      the full PR→plan→apply loop end-to-end)
- [ ] T033 Validate quickstart.md Scenario 7 (rails-ready): scaffold `infra/scratch/`
      with backend key `platform/scratch`, `tofu init` against the backend, then delete
      the scaffold — no layout/convention/workflow changes needed (SC-008)
- [ ] T034 [P] Update `docs/architecture.md` "Open questions": mark pin mechanics and
      naming convention as resolved (→ this spec / docs/conventions.md)

---

## Dependencies & Execution Order

- **Setup (P1)** → **Foundational (P2)** → user stories. Seed identifiers are in hand
  (verified 2026-06-11), so nothing is owner-blocked.
- **US1 (Phase 3)** blocks US2/US3 validation tasks (they query/apply against the PDP
  backend). T009 (AVM smoke validation) can run in parallel with T005–T008.
- **US2 (Phase 4)**: T017–T018 can start any time after Setup (docs-only, [P]); T019
  needs US1 applied.
- **US3 (Phase 5)**: needs US1 (backend + stack); T023–T025 ([P], independent files) can
  be written while T020–T022 are in flight; T026 before T027.
- **US4 (Phase 6)**: T029 any time; T030 needs US3 (CI parity comparison).
- **Polish (Phase 7)**: T031–T033 need US3 rails; T034 needs US2 doc.

### Parallel opportunities

- Phase 1: T002, T003, T004 together after T001.
- Phase 2: T006, T007 together after T005.
- US1: T015 alongside T016.
- US2: T017, T018 while US1 finishes.
- US3: T023, T024, T025 together.
- Docs: T029, T034 anytime after their inputs exist.

---

## Implementation Strategy

**MVP = Phase 1 + 2 + User Story 1**: the PDP backend created and protected, with the
foundations state anchored in the seed. Stop, validate Scenario 1, and the platform has
trustworthy state — the single hardest prerequisite for every later spec.

**Increment 2**: US2 + US3 together (conventions + rails) — after this, every further
change to the platform rides reviewed plans, including this feature's own cleanup
(T032).

**Increment 3**: US4 + Polish — clone ergonomics, protection drill, rails-ready check.

Commit after each task or logical group; from T026 onward, all changes go through PRs
(the rails enforce themselves).
