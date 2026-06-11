# Contract: State Backends

Two backends, distinct roles. Breaking this contract is an architecture change.

## Roles

| Backend | Account | Holds | Managed by |
|---|---|---|---|
| **Seed** | `cmhtfstatesa` / `RG-TF` / sub `8bd05b2f-62c5-4def-9869-f0617ebb3970` | Exactly one PDP state: `pdp/foundations` | The owner (external dependency — PDP never imports, tags, remediates, or destroys it) |
| **PDP** | `stpdpeus2state<suffix>` / `rg-pdp-eastus2-foundations` (created by the foundations stack) | State for every other deployable unit per the key registry (data-model.md §3) | The foundations stack (protected per the Article IV carve-out) |

## Foundations stack backend block (seed)

```hcl
terraform {
  backend "azurerm" {
    resource_group_name  = "RG-TF"
    storage_account_name = "cmhtfstatesa"
    container_name       = "tfstate"
    key                  = "pdp/foundations"
    use_azuread_auth     = true   # seed already has shared keys disabled
  }
}
```

## Every other stack's backend block (PDP)

```hcl
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-eastus2-foundations"
    storage_account_name = "<stpdpeus2state-suffix>"   # output of foundations stack
    container_name       = "tfstate"
    key                  = "<state key per registry>"  # see data-model.md §3
    use_azuread_auth     = true                        # ALWAYS — shared keys disabled
    # CI additionally provides: use_oidc = true + ARM_CLIENT_ID/ARM_TENANT_ID/
    # ARM_SUBSCRIPTION_ID env vars (GitHub OIDC). Locally: az login context.
  }
}
```

## Guarantees provided (PDP backend)

- **Isolation**: one state blob per deployable unit via the key registry; operations on
  different units never contend.
- **Locking**: native blob-lease locking on every write; concurrent writers to the same
  key serialize or fail cleanly — callers do not implement their own locking.
- **Recovery**: any state version recoverable for 30 days (blob versioning + soft
  delete); container soft delete 30 days.
- **Auth**: Entra ID only. Shared-key access disabled at the account; tooling that
  needs an access key is incompatible by design.
- **Durability**: LRS. Regional loss = re-run foundations to recreate (clarification Q2).

## Seed backend expectations (external dependency)

- PDP requires only `Storage Blob Data Contributor` on the seed's `tfstate` container
  (granted to owner + CI UAMI). No management-plane rights, ever.
- Seed config is the owner's business; verified at bootstrap (2026-06-11): Entra-only,
  versioning on, 7-day soft delete — accepted as-is for the single foundations blob.
- If the seed becomes unavailable, only the foundations stack is affected (edge case in
  spec.md); every other unit's state is in the PDP backend.

## Requirements on consumers

- Use a registered key from the registry (data-model.md §3); new unit types register a
  key pattern there first. Only the foundations stack may target the seed.
- Identities need `Storage Blob Data Contributor` on the PDP container (granted by the
  foundations stack).
- Never disable `use_azuread_auth`; never request account keys.
