# Phase 7 — Validation Note (IPAM Ledger)

Records the cross-cutting validation for spec 002 (tasks.md T031). The ledger-behavior
scenarios (quickstart §2–6) run as xUnit integration tests against a **real** Postgres 17
via Testcontainers (FR-016 — the GiST exclusion constraint and the advisory-lock allocator
cannot be exercised in-memory). The infrastructure scenario (§1) rides the spec-001 CI
rails and was validated in T017.

## Test run

- **Command**: `dotnet test Pdp.sln -c Release` (the same invocation `dotnet.yml` runs in CI).
- **Result**: **26 passed, 0 failed, 0 skipped.**
- **Engine**: `postgres:17-alpine` (pinned), schema applied via the production EF Core
  migration (extension + exclusion constraints + bootstrap seed); Respawn reset between tests.

## Scenario → success-criterion coverage

| Quickstart scenario | Tests | Criterion | Status |
|---|---|---|---|
| §2 Non-overlap is impossible | `NonOverlapTests` (1) | SC-002 | ✅ |
| §3 Concurrency safety (≥100 concurrent allocate) | `ConcurrencyTests` (1) | SC-001 | ✅ |
| §4 Allocate / release / idempotency | `AllocateReleaseTests` (8) | SC-003, SC-004 | ✅ |
| §5 Regional scheme & hub carve-out | `RegionRegistrationTests` (8) | US3 acceptance | ✅ |
| §6 Allocation visibility (`query`/`query_all`) | `QueryTests` (5) | SC-005 | ✅ |
| Harness (real-Postgres schema/seed comes up) | `HarnessSmokeTests` (3) | FR-016 enabler | ✅ |

SC-001…SC-005 are all green against real Postgres.

## SC-006 / SC-007 (Scenario §1, US1 — infrastructure)

Verified in **T017** via a local smoke `tofu plan`: the planned `psql-pdp-eastus2-controlplane`
has no public endpoint, Entra-only auth (no admin login/password — zero DB secrets, SC-006),
30-day LRS backup, `BTREE_GIST` allow-listed, and conformant tags/names; the RG
`prevent_destroy` + `CanNotDelete` lock gate teardown (SC-007). The live PR→merge→apply and
the destroy-protection drill execute through the CI rails on merge — applies are CI-only by
constitution and cannot run pre-merge.

## SC-008 (rails ready for specs 003/004)

Confirmed by construction, not a runtime test: `IIpamLedger`
(`src/Pdp.ControlPlane.Ipam/IIpamLedger.cs`) exposes the full operation surface the
regional-hub-fabric (003) and spoke-vending (004) specs consume — `RegisterRegionAsync`,
`AllocateAsync` (hub + spokes), `ReleaseAsync`, and `QueryAsync`/`QueryAllAsync` — matching
`contracts/ipam-operations.md` with **no schema or addressing-scheme change required**
(FR-017). The handoff (research §13): the live Azure ledger becomes *operable* in spec 006
when an in-VNet runtime applies the migrations; until then the schema and allocator are
proven against Testcontainers and the empty private server is deployed with `BTREE_GIST`
allow-listed.

## Branch protection (T029)

`scripts/setup-branch-protection.ps1` now requires `fmt`, `plan (foundations)`,
`plan (control-plane)`, and the `dotnet` gate on `main`; `dotnet.yml` gained an
always-running `dotnet` gate job so the path-filtered build/test suite can be a required
check without leaving non-.NET PRs permanently pending. The ruleset was applied (owner-run,
as it mutates live `main` branch protection); the `pdp-main-protection` ruleset now requires
`fmt`, `plan (foundations)`, `plan (control-plane)`, and `dotnet` — verified via the GitHub
rulesets API.
