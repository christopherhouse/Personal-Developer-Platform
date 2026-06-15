# Foundations Stack

Creates the **PDP state backend** (resource group, storage account, `tfstate`
container), destroy protection, and (from US3) the CI identity. This stack's own state
lives in the **seed backend** — see the two-backend contract:
[contracts/state-backend.md](../../specs/001-platform-foundations/contracts/state-backend.md).

## Seed backend (external dependency)

| Field | Value |
|---|---|
| Subscription | `8bd05b2f-62c5-4def-9869-f0617ebb3970` |
| Resource group | `RG-TF` |
| Storage account | `cmhtfstatesa` |
| Container / key | `tfstate` / `pdp/foundations` |

The seed is **owner-managed and outside PDP scope**: never imported, tagged,
remediated, or destroyed by anything in this repo. PDP needs only
`Storage Blob Data Contributor` on its `tfstate` container (owner access is granted
out-of-band and was verified 2026-06-11 via
`az storage container list --account-name cmhtfstatesa --auth-mode login`). Seed
protections (Entra-only auth, versioning, 7-day soft delete) are the owner's settings,
accepted as-is for this single state blob.

## Bootstrap procedure (one-time, owner-run)

Prereqs: `az login` (owner), pinned OpenTofu per `.opentofu-version`.

```powershell
cd infra/foundations
tofu init          # backend = seed; no resources touched
tofu plan          # review: CREATES ONLY — zero changes to RG-TF/cmhtfstatesa
tofu apply         # creates the PDP backend
```

This is the **bootstrap-era exception** to "applies run only in CI" — CI cannot exist
before the backend it depends on. Once US3 lands (CI identity + workflows + branch
protection), every subsequent change to this stack rides PR → plan → merge → apply, and
local applies end.

**Partial failure**: plain re-run semantics — `tofu apply` converges from any
interruption (e.g., RG created but storage account failed → re-apply creates the rest).

**Idempotency check**: a second `tofu plan` after apply must show no changes.

## Failure domains

- **Seed lost/unreachable**: only this stack's state is affected; every other unit's
  state is in the PDP backend. Recover by restoring seed access, or re-anchor: point
  `backend.tf` at a new location and re-import the small foundations resource set
  (RG, storage account, container, lock, role assignment).
- **PDP backend lost**: this stack's state is safe in the seed — re-run `tofu apply`
  to recreate the backend. Other units' states are gone; they re-bootstrap (accepted
  LRS posture, spec clarification Q2).

## Protected resources (Article IV carve-out — FR-004)

| Resource | Protection |
|---|---|
| `rg-pdp-eastus2-foundations` | `CanNotDelete` management lock |
| `stpdpeus2state<suffix>` | lock (inherited) |
| `tfstate` container | lock (inherited) + `prevent_destroy` |
| `lock-pdp-eastus2-foundations` | removable only via reviewed PR |

`tofu destroy` fails at plan time on `prevent_destroy`; the ARM lock additionally
blocks deletes from the portal/CLI. **Removing protection** = a PR that deletes the
lock resource and the `prevent_destroy` lifecycle flag (reviewed, planned), then a
confirmed destroy. Everything else (CI identity, role assignments) tears down cleanly.

## Module adoption note (Article V)

`Azure/avm-res-storage-storageaccount/azurerm` **0.7.2** (pinned exact) — smoke
validation under OpenTofu 1.11.6, 2026-06-12:

- init + plan: ✅ clean (module schema resolves; azapi-based implementation;
  `parent_id` interface).
- Findings encoded above: `account_replication_type` is deprecated/ignored — use
  `account_sku_name`; secure-by-default sets `publicNetworkAccess: Disabled` +
  network ACL Deny, overridden deliberately per the Article IX exception.
- apply + destroy: run as part of the owner-executed smoke procedure (scratch config
  in a temp directory; creates and destroys `rg-pdp-eastus2-avmsmoke`).

RG, container, lock, identity, and role-assignment resources are plain `azurerm`
primitives — no AVM composition exists for single resources (justification per
Article V).
