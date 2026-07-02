# Workload Archetypes

Versioned, parameterized OpenTofu modules the platform stamps into vended spokes —
deploying an archetype produces a **workload** (see `docs/glossary.md`). This directory
is the **archetype catalog's source of truth**: `catalog.json` declares what is
deployable, and each `archetypes/<name>/` directory is the module a catalog version
points at. Changes land **only by PR** — chat and the CLI can never alter deployable
truth (constitution Article II, clarified 2026-07-02).

Spec/contract references: `specs/008-workload-archetypes/contracts/archetype-catalog.md`
(catalog format + sync semantics) and `contracts/execution-plane.md` (module input
contract, state keys, workflows).

## Layout

```text
archetypes/
├── catalog.json          # the declarative catalog — baked into the api image,
│                         # projected into registry Postgres at startup (sole writer: api)
└── <archetype-name>/     # one OpenTofu module per archetype
    ├── main.tf
    ├── variables.tf
    ├── outputs.tf
    ├── backend.tf        # partial backend — key supplied at init by the workflow
    └── README.md         # what it deploys + any raw-resource (non-AVM) justifications
```

## How execution works (read this before authoring)

- Workloads deploy through the `workload-deploy.yml` / `workload-destroy.yml`
  dispatched workflows — never locally, never at runtime. The workflow checks out the
  **pinned git tag** for the stamped version and runs `tofu -chdir=archetypes/<name>`.
- One state per workload: `workloads/<subscription-id>/<spoke-name>/<workload-name>`
  (partial backend config, same storage account as every stack).
- Deployed workloads keep the version they were stamped with; destroy checks out the
  **stamped** tag, not the newest one. That is why versions must be immutable.

## Authoring conventions

**Module inputs.** The workflow supplies the platform-level variables — `region`,
`target_subscription_id`, `platform_subscription_id`, `spoke_name`, `workload_name`,
`pdp_env`, and `parameters` (a single object variable holding the schema-validated
caller payload). Caller-tunable knobs live *inside* `parameters`; give each a default
on the OpenTofu side (defaults are applied by the module, **not** injected by the
control plane — the stored parameters are exactly what the caller sent).

**Consume, don't create, network fabric.** Archetypes carve no address space and own
no egress: consume the spoke by remote state (`spokes/<sub>/<spoke>` — shared ACA
environment ID, subnets, RG name) and shared platform services (Log Analytics,
private DNS zones) by data source. Private by default: no public endpoint unless a
parameter explicitly opts in.

**AVM-first.** Prefer Azure Verified Modules, smoke-validated under the pinned
OpenTofu version before first adoption (Article V). Any hand-rolled `azurerm`/`azapi`
resource needs a recorded justification in the archetype's README.

**Tags.** The workload resource group (and every taggable resource) carries
`pdp-managed=true`, `pdp-deployed-by=github-actions`, `pdp-workload=<workload_name>`,
`pdp-env=<pdp_env>` — this is what makes `ListWorkloadEnvironments` light up with no
inventory code change.

**Observability.** Every resource that supports diagnostic settings ships logs +
metrics to the shared Log Analytics workspace (Article XI).

## Parameter-schema conventions

Each catalog version embeds a JSON Schema (draft 2020-12) for its parameters, per
`contracts/archetype-catalog.md`:

- Root `type: object` with **`additionalProperties: false`** — unknown parameters are
  rejections, not passthroughs.
- Every property carries a `description` (surfaced in CLI/MCP help and validation
  errors) and, where safe, a `default` (informational — the module's variable default
  is what actually applies).
- Property names are camelCase; the module maps them to snake_case variables.
- Validation runs in the control plane (JsonSchema.Net, `OutputFormat.List`) **before**
  any intent row or dispatch — a schema violation never reaches the execution plane.

## Release runbook — new archetype or new version

Versions are **append-only and content-immutable**: the sync hashes
`(modulePath, parameterSchema)` per version and fails loudly if an existing
`(name, version)` ever presents different content. Never edit a released version —
append a new one.

One PR + one tag per release:

1. **Edit the module** under `archetypes/<name>/` (or create the directory for a new
   archetype).
2. **Edit `catalog.json`** in the same PR: add the archetype entry (new archetype) or
   append a `versions` entry (new version). `name` must match `^[a-z0-9-]{1,32}$`;
   `version` is SemVer with a `v` prefix (e.g. `v1.1.0`); `modulePath` is the
   repo-relative module directory; `parameterSchema` is the inline draft 2020-12
   schema.
3. **Create the git tag** `archetype/<name>/<version>` (e.g.
   `archetype/container-app-sql/v1.1.0`) on the merge commit as part of the same
   release step. The tag MUST exist and MUST contain `modulePath` — deploys check out
   this exact ref.
4. **Merge → it ships itself**: the `controlplane-host-images.yml` path filter picks up
   `catalog.json`, rebuilds the api image, and the startup `CatalogSyncService`
   projects the new definition into the registry. No manual registration step exists,
   by design.

**Retiring an archetype** is a PR flipping `status` to `retired` — new deploys are
refused, destroys of already-stamped workloads still work. Never delete entries or
versions from the file: deployed workloads reference stamped versions, and retirement
is the only supported off-ramp.
