# Quickstart: Validating Platform Foundations

End-to-end validation walkthrough. Each scenario maps to a success criterion (SC) from
[spec.md](spec.md). Run in order — later scenarios assume earlier ones passed.

## Prerequisites

- Azure platform subscription with Owner RBAC; `az login` completed.
- Seed backend reachable: `cmhtfstatesa` / `RG-TF` / container `tfstate` (verified
  2026-06-11 — Entra-only, versioning on); owner has `Storage Blob Data Contributor`
  on the container.
- GitHub repo admin rights; `gh` CLI authenticated.
- Pinned OpenTofu installed (version from `.opentofu-version`; `tenv install` or manual).
- No existing PDP resources in the subscription (for the clean-slate run).

## Scenario 1 — Bootstrap from the seed (SC-001, US1)

1. Start a timer. Follow `infra/foundations/README.md`: `tofu init` (backend = seed,
   key `pdp/foundations`) → `tofu plan` (review: new PDP state RG, storage account via
   AVM, container, lock, tags — creates only, no imports) → `tofu apply`.
2. **Expect**: completes ≤ 30 min; `pdp/foundations` blob exists in the **seed**
   container; the new PDP account exists with `allowSharedKeyAccess: false`, versioning
   on, 30-day blob + container soft delete (`az storage account show` /
   `blob-service-properties show` on `stpdpeus2state<suffix>`).
3. Re-run `tofu plan`. **Expect**: no changes (idempotent — re-runnable after partial
   failure too).
4. Confirm the README documents the seed-backend dependency and both failure domains
   (seed lost vs. PDP backend lost) — documentation check only.

## Scenario 2 — Conventions conformance (SC-004, US2)

1. Run the inventory query (Resource Graph):
   `az graph query -q "ResourceContainers | where tags['pdp-managed'] == 'true'"`
2. **Expect**: `rg-pdp-eastus2-foundations` returned, carrying `pdp-managed` and
   `pdp-deployed-by`; no scope tags present (platform scope). The seed RG (`RG-TF`)
   is **not** returned — it is not PDP-managed, by design.
3. Check every created resource name against [contracts/tagging-and-naming.md](contracts/tagging-and-naming.md).
   **Expect**: 100% conformance, storage account matching the constrained-name pattern.

## Scenario 3 — CI rails (SC-002, SC-003, US3)

1. Open a PR changing a tag value in `infra/foundations`.
   **Expect**: `iac-plan` runs; fmt/validate pass; the plan appears on the PR showing
   exactly the one tag change; merge is blocked until checks pass.
2. Try a direct push to `main`. **Expect**: rejected by the ruleset (SC-002's "0 direct
   pushes possible").
3. Merge the PR. **Expect**: `iac-apply` re-plans and applies; the run links the merged
   PR; `az tag list` on the RG confirms the change landed (SC-003).
4. Audit: `gh secret list` and repo settings. **Expect**: zero Azure credentials
   (SC-005) — only plain variables for client/tenant/subscription IDs.

## Scenario 4 — Fresh-clone parity (SC-006, US4)

1. Fresh `git clone` on a machine with only git + tenv/OpenTofu + az CLI.
2. Follow the README setup; run `tofu fmt -check` and `tofu validate` in
   `infra/foundations`.
3. **Expect**: passes with no undocumented steps; results match the CI run for HEAD.

## Scenario 5 — Failure contract (FR-016)

1. Merge a PR with an apply that must fail (e.g., reference a quota-violating SKU in a
   scratch resource, or simulate via a deliberately broken role assignment).
2. **Expect**: red run, owner notified; a follow-up merge to the same stack is blocked
   by the guard; a fix-forward PR (or manual re-run after fixing) clears it.
3. Revert the scratch change.

## Scenario 6 — Teardown & protection (SC-007, US1.4)

1. Run `foundations-destroy` via `workflow_dispatch` **without** removing protection.
   **Expect**: destroy plan fails on `prevent_destroy` / the management lock — the
   protected list in `infra/foundations/README.md` is exactly what blocks.
2. (Full-teardown test, optional/destructive): merge the protection-removal PR, re-run
   destroy with the typed confirmation. **Expect**: clean teardown of all PDP foundation
   resources, nothing orphaned, seed backend untouched (foundations state blob remains);
   restore by re-running Scenario 1 (destroying the PDP backend deletes all other units'
   state — only do this knowingly).

## Scenario 7 — Rails ready for spec 2 (SC-008)

1. Dry-check: scaffold a dummy stack `infra/scratch/` with backend key
   `platform/scratch`, run `tofu init` against the backend, then delete the scaffold.
2. **Expect**: init succeeds with no changes to layout, conventions, or workflows —
   the ipam-ledger spec can start on these rails as-is.
