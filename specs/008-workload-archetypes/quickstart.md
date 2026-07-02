# Quickstart — Validating Workload Archetypes (spec 008)

Runnable scenarios proving the feature end to end. Contracts:
[workload-verbs](./contracts/workload-verbs.md) ·
[archetype-catalog](./contracts/archetype-catalog.md) ·
[execution-plane](./contracts/execution-plane.md). Data model:
[data-model.md](./data-model.md).

## Prerequisites

- Live platform (westus3): fabric + control-plane host (spec 007) healthy; owner
  `az login`; MCP chat surface reachable (Claude with `pdp-mcp`).
- A vended spoke on the **updated** `infra/spoke` stack (Scenario 3) in a writable
  target subscription.
- Storage-account public access re-enabled if the overnight policy disabled it
  (known environment quirk — CI `tofu init` 403s otherwise).
- For CLI scenarios: the transient-ACA-job path to the private Postgres (R11) — the
  laptop cannot reach the ledger by design.

## Scenario 1 — Build, tests, migration

```powershell
dotnet build; dotnet test
```

Expected: green, including new Testcontainers suites — catalog sync (upsert, retire,
immutability rejection keeps previous projection), workload natural-key idempotency,
FR-021 guard, schema-violation mapping. `dotnet ef migrations list` shows the new
registry migration; `pdp migrate` applies cleanly.

## Scenario 2 — AVM smoke validation (Article V, gate for the archetype)

One-off `tofu init/plan/apply/destroy` of a minimal `avm-res-sql-server` 0.2.1
serverless config under OpenTofu 1.11.x (throwaway RG, platform sub). Expected: clean
apply AND destroy; record the result in `archetypes/container-app-sql/README.md`.

## Scenario 3 — Stack updates land

Merge infra edits → `iac-plan`/`iac-apply` (dns, fabric) green; re-vend the test spoke
via chat (spec-007 flow). Expected: spoke has `aca` /27 delegated subnet, shared ACA
env wired to the shared Log Analytics workspace, `privatelink.database.windows.net`
linked to the spoke VNet (auto via fabric `shared_dns_zone_ids`).

## Scenario 4 — Invalid parameters rejected before dispatch (SC-003)

Via chat: `PlanWorkloadDeploy` with `parameters = {"containerImage": "", "cpu": 3}`.
Expected: schema-derived violations naming `containerImage` (minLength) and `cpu`
(enum); **no confirmation token, no registry intent row, no GitHub run**. Repeat with
unknown archetype and with a retired archetype (after Scenario 8): distinct
"unknown" vs "retired" refusals.

## Scenario 5 — Deploy via chat (SC-001)

`PlanWorkloadDeploy` (archetype `container-app-sql`, env `dev`, a public sample image,
defaults otherwise) → plan names spoke, archetype **and resolved version**, parameters
→ `ApplyWorkloadDeploy` with token + verbatim name → run tracked to `Active`.
Expected: RG `rg-pdp-<region>-workload-<name>` with full tags; container app running
(internal ingress) on the spoke env; SQL serverless private; state at
`workloads/<sub>/<spoke>/<name>`. If the apply fails on image pull, check hub firewall
ACA FQDN allowances (R10 contingency → fabric policy PR) and re-run.

## Scenario 6 — Deploy via CLI (SC-002)

Second workload, same spoke, via `pdp workload deploy … --env dev` (transient ACA
job). Expected: identical plan→confirm behavior, exit 0. Also verify `--param cpu=3`
exits 2 with the schema message.

## Scenario 7 — Inventory lights up (SC-004)

Chat: "what workload environments do I have deployed?" (`ListWorkloadEnvironments`).
Expected: non-empty, `dev` grouping both workloads with spoke + subscription, straight
from ARG. `ShowEnvironment` resolves the workload by `(workload, sub, name)`.

## Scenario 8 — Catalog lifecycle + pinned tag (SC-008)

PR: append `v1.1.0` (tag + entry) → image redeploy → sync. Expected:
`workloads.archetype_version` of Scenario 5/6 workloads still `v1.0.0` (chat
`ShowEnvironment`/plan restates it); a fresh plan resolves `v1.1.0`. Then PR
`status: retired`: new deploys refused, destroys still work. `catalog_syncs` rows
audit each step.

## Scenario 9 — Destroy + guards (SC-007, FR-021)

1. Spoke destroy attempt while workloads exist → refused, **survivor names listed**.
2. `PlanWorkloadDestroy` → `DestroyWorkload` with wrong restated name → refused, token
   intact; with verbatim name → destroy run (stamped-tag checkout) → `Destroyed`.
   Expected: workload RG gone, state object gone, spoke untouched (`tofu plan` on the
   spoke shows no changes), workload absent from `ListWorkloadEnvironments`.
3. Destroy the second workload; spoke destroy now proceeds (or spoke retained for
   spec 9 — either way the guard no longer refuses).

## Scenario 10 — Diagnostics (SC-006, Article XI)

In the shared Log Analytics workspace: ACA env logs (spoke env), SQL diagnostics
(server/db categories) present; `env_id`-correlated verb telemetry in App Insights for
the deploy/destroy runs.

## Scenario 11 — Template repo (SC-009)

Stamp `pdp-workload-template` → new repo; one-time bootstrap (federated credential +
`AcrPush` + `AZURE_*` variables per template README); push. Expected: CI green on
first push via `reusable-container-build.yml`, image in platform ACR. Optionally
redeploy Scenario 5's workload with that image reference (managed-identity pull —
no credentials).

## Teardown (Article IV)

Scenario 9 already proves workload teardown. Full cleanup: destroy both workloads,
then the spoke (chat flow), then delete the Scenario 2 smoke RG. Expected: no orphaned
resources (`WhatsDeployed` clean), no leaked state objects.
