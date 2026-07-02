# Contract — Archetype Catalog Definition & Sync

## The definition file: `archetypes/catalog.json`

Repo-managed (PR-only — clarify 2026-07-02), baked into the api image, synced to the
`registry` schema at startup by `CatalogSyncService` (api app = sole writer; mcp reads
the projected tables).

```json
{
  "$schemaVersion": 1,
  "archetypes": [
    {
      "name": "container-app-sql",
      "description": "Containerized app on the spoke's shared ACA environment + Azure SQL serverless (private).",
      "status": "active",
      "versions": [
        {
          "version": "v1.0.0",
          "modulePath": "archetypes/container-app-sql",
          "parameterSchema": { "...": "JSON Schema draft 2020-12, inline" }
        }
      ]
    }
  ]
}
```

Rules:

- `name`: `^[a-z0-9-]{1,32}$`; `status`: `active` | `retired`.
- `version`: SemVer with `v` prefix. The git tag `archetype/<name>/<version>` MUST
  exist on the platform repo and MUST contain `modulePath` — tag creation is part of
  the same PR/release step that edits this file (documented in `archetypes/README`).
- Versions are **append-only and content-immutable**: sync computes
  SHA-256(`modulePath` + canonical `parameterSchema`) and fails the whole sync (loud:
  log + telemetry + `catalog_syncs` row `outcome=rejected`) if an existing
  `(name, version)` presents a different hash.
- Removing a version/archetype from the file does **not** delete registry rows (deployed
  workloads reference stamped versions); retirement is the only supported off-ramp.

## Sync semantics (startup, idempotent)

1. Compute whole-file hash; equal to last `applied`/`no_change` sync → record
   `no_change`, stop.
2. Validate the file (shape + every `parameterSchema` parses as a valid 2020-12
   schema). Invalid file → `rejected`, **keep serving the previous projection**
   (control plane still boots; deploys use last good catalog).
3. Upsert archetypes (insert new, apply status changes), insert new versions, enforce
   immutability. All-or-nothing transaction.
4. Record `catalog_syncs` audit row with per-entry summary (FR-006 = git history of
   the file + this table).

## Parameter schema conventions (per archetype version)

- Draft 2020-12, root `type: object`, **`additionalProperties: false`** (unknown
  parameters are rejections, not passthroughs).
- Every property carries `description` (surfaces in CLI/MCP help and schema-derived
  errors) and, where safe, `default`. Defaults are applied by the OpenTofu module
  (variables with defaults), NOT injected by the control plane — the stored
  `parameters` jsonb is exactly what the caller sent.
- Names are camelCase; the module maps them to snake_case variables.

## First archetype schema: `container-app-sql` v1.0.0

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["containerImage"],
  "properties": {
    "containerImage": {
      "type": "string",
      "minLength": 1,
      "description": "Any resolvable public image reference; references to the platform registry get automatic managed-identity pull (no credentials)."
    },
    "publicEndpoint": {
      "type": "boolean",
      "default": false,
      "description": "Explicit opt-in for an externally reachable endpoint. Default: internal-only ingress."
    },
    "targetPort": { "type": "integer", "minimum": 1, "maximum": 65535, "default": 8080 },
    "cpu":        { "type": "number",  "enum": [0.25, 0.5, 1.0], "default": 0.25 },
    "memory":     { "type": "string",  "enum": ["0.5Gi", "1Gi", "2Gi"], "default": "0.5Gi" },
    "sqlAutoPauseDelayMinutes": { "type": "integer", "minimum": 15, "maximum": 10080, "default": 60 },
    "sqlMaxSizeGb": { "type": "integer", "minimum": 1, "maximum": 32, "default": 2 }
  }
}
```

(`pdp-env`, workload name, spoke, subscription are **verb-level fields**, not archetype
parameters — they exist for every archetype and are validated by FluentValidation.)

## Lifecycle walkthroughs (traceability)

- **Register new archetype / version**: PR adds catalog entry + module + tag → merge →
  image build (`controlplane-host-images.yml`, path filter includes
  `archetypes/catalog.json`) → deploy → startup sync inserts → deployable.
- **Retire**: PR flips `status: retired` → sync updates → `PlanWorkloadDeploy` refuses
  with "retired" (US4-AS2) while destroys of stamped workloads proceed (US4-AS3).
- **Upgrade story**: new version appended; existing workloads keep
  `workloads.archetype_version` (FR-005); next deploy resolves the new version
  (US4-AS1). Redeploy-at-new-version = destroy → deploy (assumption, unchanged).
