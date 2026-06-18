# Quickstart — Validating the Action Layer (Control Plane)

Runnable validation scenarios proving spec 006 end to end, each mapped to a Success Criterion. The
control plane runs **under the owner's context** (reaching the private platform Postgres + GitHub) —
no ACA, no public ingress (Clarifications Q1). Details live in `contracts/` and `data-model.md`; this
is the run/validate guide, not implementation.

## Prerequisites

- .NET 10 SDK (pinned via `global.json`); `dotnet build` clean.
- `az login` as the owner (read access across the platform + target subscriptions, for inventory).
- Network line-of-sight to the **private platform Postgres** (the IPAM-ledger flexible server) — VPN /
  private endpoint / jump context. (This is the reachability CI lacks — the Gate-G1 premise.)
- The **`pdp-orchestrator` GitHub App** installed on the platform repo; its private key + app/
  installation ids available to the owner context (the only non-Azure secret).
- Live platform from specs 002–005: the **`westus3` fabric** deployed; the IPAM ledger seeded.
- For the MVP, an `APPLICATIONINSIGHTS_CONNECTION_STRING` pointing at a **dev** App Insights (or unset
  → console/OTLP exporter). No App Insights resource is provisioned by this spec (FR-O1).

## One-time setup

```powershell
# Apply the registry schema to the existing platform Postgres (new `registry` schema only).
dotnet ef database update --project src/Pdp.ControlPlane.Registry

# (Optional) F5 local dev: Postgres container + API (webhook handler + reconciler) + YARP ingress.
dotnet run --project src/Pdp.AppHost      # .NET Aspire dashboard
```

---

## Scenario 1 — One-command spoke vend, end to end (US1 → SC-001, SC-002, SC-012)

```powershell
pdp spoke create --subscription <target-sub> --region westus3 --name app3 --size 24
#   plan phase dispatched → CLI prints allocated CIDR (from the ledger), target, plan summary, run URL
#   confirm (or pass --yes) → apply phase dispatched → tracked to success
pdp spoke create --subscription <target-sub> --region westus3 --name app3 --json   # machine-readable
```

**Expect**: no `--cidr` was supplied; the block was **allocated live by size** from the IPAM ledger
(verify `pdp ipam query --region westus3` shows the new allocation row — SC-002). Dispatch was
acknowledged within seconds (SC-012). After the run completes, the spoke is discoverable in inventory
(Scenario 5). **SC-001** met: one command, no hand-fitted CIDR, no hand-run workflow.

## Scenario 2 — Plan before apply, confirm before destroy (US2 → SC-004)

```powershell
pdp spoke create --subscription <target-sub> --region westus3 --name app4 --size 24
#   verify a PLAN run is dispatched and its summary printed BEFORE any apply (Article VIII)

pdp spoke destroy --subscription <target-sub> --name app4
#   verify it refuses to proceed without --confirm
pdp spoke destroy --subscription <target-sub> --name app4 --confirm app4
#   destroy plan shown, confirmation accepted, destroy dispatched
```

**Expect**: create surfaces a plan before apply; destroy **cannot** dispatch without `--confirm <name>`
(no `--yes` bypass). **SC-004** met (attempt-to-bypass is rejected).

## Scenario 3 — Clean teardown releases the allocation (US2 → SC-003, SC-010)

```powershell
pdp ipam query --region westus3                 # note app3's allocation block
pdp spoke destroy --subscription <target-sub> --name app3 --confirm app3
#   on successful destroy run:
pdp ipam query --region westus3                 # app3's block is GONE (released) and reusable
pdp spoke create --subscription <target-sub> --region westus3 --name app5 --size 24
#   app5 may reuse the freed space
```

**Expect**: after a successful destroy, **zero** leaked allocation rows; the block is reusable
(**SC-003**). The environment record shows `Destroyed` (`pdp run list --env …`).

## Scenario 4 — fabric create/destroy via the new dispatch path (US3 → SC-005, FR-012a)

```powershell
pdp fabric create  --region westus3 --region-index 1   # dispatches the NEW fabric-vend.yml (plan→apply)
pdp fabric destroy --region westus3 --confirm westus3   # dispatches fabric-destroy.yml (confirm-gated)
```

**Expect**: `fabric create` dispatches the new `fabric-vend.yml` (no GitOps merge needed); **every**
mutation ran only in a dispatched GitHub Actions workflow (OIDC, no stored cloud secrets) — verify
**zero** in-process `tofu` from logs (**SC-005**).

## Scenario 5 — Full verb surface + structured output (US3 → SC-008, SC-009)

```powershell
pdp inventory                 # reuses the spec-005 component (credential injected) — what's DEPLOYED (ARG)
pdp env list                  # environments deployed
pdp env show demo
pdp ipam query                # all regions
pdp inventory --json | jq .   # structured output, consumable without parsing human text
```

**Expect**: every verb works from one CLI in human + `--json` form (**SC-008**); inventory answers come
from **ARG**, not the registry (division of truth, FR-016); inventory logic is **not** duplicated
(**SC-009**).

## Scenario 6 — Registry intent vs. ARG truth (US4 → SC-007)

```powershell
pdp run list --env spoke:<target-sub>:app5     # audit trail: dispatch inputs, GitHub run id/url, outcome
pdp run show <run-id>
```

**Expect**: the registry answers "what did I ask for and what happened?" (intent + run history), while
"what's deployed?" comes from `pdp inventory` (ARG). Both agree for a healthy environment (**SC-007**).

## Scenario 7 — Resilient tracking: missed webhook still reconciles (US5 → SC-006)

Integration test (WireMock.Net fake GitHub): dispatch a run, **suppress** the `workflow_run` webhook
delivery, and assert the **polling reconciler** still drives the run to recorded terminal status
within the bound (sweep ≤60 s, settle ~2 min). Then deliver a **duplicate** webhook and assert the
recorded outcome is unchanged (idempotent).

**Expect**: no run left "in flight"; **SC-006** met by polling even with the webhook missed.

## Scenario 8 — Observability correlated by `env_id` (FR-O1 → SC-013)

```powershell
pdp spoke create --subscription <target-sub> --region westus3 --name app6 --size 24
#   then, in the dev App Insights (or console/OTLP exporter):
#   filter traces by env_id → see verb invocation → dispatch → run-state transitions as one trace
```

**Expect**: a single `env_id` traces the whole operation end to end (**SC-013**); no new Azure
resource was provisioned by this spec (App Insights ships with spec-007 hosting).

---

## Teardown (Article IV — SC-010)

```powershell
# Environments: destroy any spokes/fabric created above (Scenarios 2–4) — allocations released.
pdp spoke destroy  --subscription <target-sub> --name app5 --confirm app5
pdp spoke destroy  --subscription <target-sub> --name app6 --confirm app6

# The spec's own footprint = the registry schema only (no new Azure resources; hosting is spec 007):
dotnet ef database update 0 --project src/Pdp.ControlPlane.Registry   # drops the `registry` schema
```

**Expect**: every created environment tears down cleanly (no orphaned resources, no leaked
allocations, no dangling peerings — spec 004 behavior, now with control-plane allocation release); the
`registry` schema drops cleanly; **no Azure resource** remains to remove (the App Insights resource +
ACA host belong to spec 007). **SC-010** met.

---

## Success-criteria coverage

| Scenario | Success Criteria |
|---|---|
| 1 | SC-001, SC-002, SC-012 |
| 2 | SC-004 |
| 3 | SC-003, SC-010 |
| 4 | SC-005 |
| 5 | SC-008, SC-009 |
| 6 | SC-007 |
| 7 | SC-006 |
| 8 | SC-013 |
| Teardown | SC-010, SC-011 |
