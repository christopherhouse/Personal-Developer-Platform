# Quickstart — Environment Inventory (live validation)

Runnable validation that proves the feature end-to-end against the **live platform** (the deployed
`westus3` fabric from spec 003 and the spec-004 spoke pattern). Each scenario maps to a success
criterion. This is a **read-only** exercise: it creates nothing in Azure and provisions no identity.

## Prerequisites

- .NET 10 SDK (`global.json` pins `10.0.100`).
- `az login` as the platform owner, with **read** access to the platform subscription and the
  target subscription(s) (e.g., `chhouse-1`). Resource Graph reads are permitted under local `az`
  context (CLAUDE.md); **no** identity/RBAC is provisioned by this spec.
- The `westus3` fabric deployed and at least one spoke vended (spec 004), so there is something to
  classify.

## Build & unit tests (offline — no Azure)

```powershell
dotnet build Pdp.sln
dotnet test tests/Pdp.ControlPlane.Inventory.Tests/Pdp.ControlPlane.Inventory.Tests.csproj
```

Expected: build clean (warnings-as-errors), all classification / drift / grouping / orchestration
tests green. These cover the pure engine over fabricated ARG rows — **no** Azure calls.

## Run the inventory harness (live)

```powershell
# human-readable
dotnet run --project src/Pdp.Inventory.Demo
# machine-readable (structured proof)
dotnet run --project src/Pdp.Inventory.Demo -- --json
```

---

## Scenario → Success-criterion map

| # | Scenario | Steps | Expected (SC) |
|---|---|---|---|
| 1 | **Full classified inventory** | Run the harness with no args | Every `pdp-managed` RG across all accessible subscriptions appears, classified as fabric/spoke/workload, each with subscription + region (**SC-001**) |
| 2 | **Headline — environments** | Read the Environments section | "What environments do I have deployed?" answered; workloads grouped by `pdp-env` (**SC-004**) |
| 3 | **Headline — spokes** | Read the Spokes section | Each spoke listed with name + subscription + region (**SC-004**) |
| 4 | **Headline — environment X** | Filter/inspect one environment | Exactly that environment's RGs; an unknown name yields an empty result, not an error (**SC-004**) |
| 5 | **New resource, no code change** | Vend a new spoke (spec 004), re-run the harness | The new spoke appears, correctly classified, with **zero** code/config change (**SC-002**) |
| 6 | **Drift — mis-tag** | Temporarily set a bad tag on a test RG (e.g., `pdp-deployed-by=bogus`, or drop `pdp-env` on a workload RG), re-run | Flagged as **conformance** drift naming the offending tag; correctly tagged RGs and `RG-TF`/`cmhtfstatesa` are **not** flagged (**SC-003**) |
| 7 | **Drift — orphan / ambiguous** | Tag a test RG `pdp-managed=true` with no scope tag (orphan) and another with two scope tags (ambiguous) | Each flagged with the right category, neither dropped (**SC-003**) |
| 8 | **Drift — invisible** | Create an `rg-pdp-*`-named RG **without** `pdp-managed` | Flagged as **invisible** drift (the Article III bug) (**SC-003**) |
| 9 | **Structured output** | Run with `--json`; pipe to a consumer / assert with `jq` | Typed structure exposes taxonomy + per-item sub/region + environment grouping + drift, no text scraping (**SC-006**) |
| 10 | **Coverage honesty** | Ensure one subscription is unreadable by the credential | It appears as `Inaccessible` in coverage; the sweep still completes (**SC-008**) |
| 11 | **Latency** | Time the full sweep and a scoped query | Full sweep **P95 < 5 s**; scoped query **< 2 s** (**SC-005**) |
| 12 | **Read-only** | Inspect the Azure **activity log** over the run window | **Zero** write/delete operations; no resource or tag changed (**SC-007**) |

## Teardown

Nothing to tear down — the feature creates no Azure resources and provisions no identity/RBAC
(**SC-009**). Remember to **revert** any deliberate mis-tags created for scenarios 6–8 (they are test
fixtures on existing/throwaway RGs, not artifacts of this feature).

> Detailed type shapes are in [`data-model.md`](data-model.md); the consumed/exposed boundaries are
> in [`contracts/inventory-interfaces.md`](contracts/inventory-interfaces.md). Implementation lands
> in `tasks.md` (via `/speckit-tasks`).
