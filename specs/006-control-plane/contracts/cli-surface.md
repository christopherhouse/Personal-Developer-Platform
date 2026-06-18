# Contract — `pdp` CLI Surface (`Pdp.Cli`)

The owner-facing front end (FR-018), built on **System.CommandLine 2.0 GA**. Every command calls the
**same verb layer** the MCP will later wrap (FR-001) and renders the **same typed result** two ways:
human-readable tables (default) or **`--json`** (`System.Text.Json`; SC-008). This is **not** a second
implementation — the CLI is a thin adapter.

---

## 1. Command tree

```text
pdp
├── fabric
│   ├── create   --region <r> --region-index <n> [--yes]
│   └── destroy  --region <r> --confirm <r>
├── spoke
│   ├── create   --subscription <sub> --region <r> --name <n> [--size 24] [--yes]
│   │            #  NOTE: no --cidr — allocated live from the IPAM ledger (Gate-G1)
│   └── destroy  --subscription <sub> --name <n> --confirm <n>
├── ipam
│   ├── allocate --region <r> --name <n> [--size 24]
│   ├── release  --region <r> --name <n>
│   └── query    [--region <r>]            # one region, or all
├── inventory                              # full snapshot (reuses spec 005)
├── env
│   ├── list                               # environments deployed (ARG)
│   └── show     <name>                     # contents of one environment
└── run
    ├── list     --env <ref>                # provisioning-run audit trail
    └── show     <run-id>

global options:  --json   --verbose
```

## 2. Plan / confirm UX (Article VIII; FR-006/FR-007)

- **Create** (`fabric create`, `spoke create`): runs the **plan phase** first and prints the
  `PlanResult` (allocated CIDR for spokes, target sub/region, proposed inputs, plan summary, run URL).
  The CLI then prompts to proceed; `--yes` pre-confirms (acceptable for create).
- **Destroy** (`*destroy`): runs the destroy **plan**, prints what will be removed, and requires
  **`--confirm <target>`** (restating the spoke name / region) — there is **no `--yes` bypass** for
  destroy (FR-007). A missing/mismatched `--confirm` exits non-zero without dispatching.

## 3. Output contract (SC-008)

- **Default**: grouped human tables (e.g. `spoke create` → a status line + run URL; `env show` → the
  managed RGs; `ipam query` → utilization).
- **`--json`**: the same `VerbResult` / `InventorySnapshot` / `RegionView` / `ProvisioningRun[]`
  serialized — consumable without parsing human text. Both render from the **identical typed object**;
  neither re-queries.
- **Exit codes**: `0` success; non-zero for validation failure, `OperationInProgressException`
  (single-flight, FR-022a), missing confirmation, or a failed/`Failed` run outcome — so scripts and CI
  can branch.

## 4. Execution model (MVP)

- The CLI hosts the verb layer **in-process** (research §1), constructing it against the platform
  Postgres (IPAM ledger + `registry` schema) and GitHub App credential under the **owner's context**.
- **Tracking model (resolves analysis A1)**: a mutating CLI command **dispatches**, then **awaits the
  recorded terminal outcome by polling** the registry (and GitHub run status via the same correlation)
  on a short interval until terminal or a `--no-wait` flag is given (in which case it prints the
  `env_id`/run URL and returns immediately, leaving completion to be recorded by the long-running Api
  host's reconciler/webhook). The CLI does **not** need the Api host running to *dispatch*; the
  durable reconciler that closes the loop continuously lives in the **Api host** (spec-007-hosted),
  while a foreground CLI invocation polls for its own result. `pdp run show <env_id>` reports the
  latest tracked state at any time.
- The owner's `TokenCredential` (`DefaultAzureCredential`) is injected into the spec-005 inventory
  component for read verbs (FR-013) and used for any ARG reads.
- The same verb layer is later hosted behind the spec-007 API/MCP with **no CLI change** required to
  the verb contracts (the CLI may switch to calling the hosted API, but the verbs are unchanged).
