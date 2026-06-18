# Contract — Hosting Topology (`infra/control-plane-host`)

The binding shape of the in-Azure hosting. Resources are AVM/OpenTofu, dispatched via CI (Article I).
See `research.md` §1–§4, §7, §8, §10, §11 for module choices and rationale.

## Network & ingress

```
                         Public internet
                                │
                                ▼
        ┌───────────────────────────────────────────────┐
        │  ca-pdp-westus3-ingress   (YARP, EXTERNAL)      │  ← the ONLY public app
        │  routes:                                        │
        │    POST /webhooks/github            → api       │
        │    ANY  /mcp                        → mcp       │
        │    GET  /.well-known/oauth-         → mcp       │
        │           protected-resource                   │
        └───────────────┬─────────────────────┬──────────┘
                        ▼                     ▼
        ┌───────────────────────┐  ┌───────────────────────┐
        │ ca-pdp-westus3-api     │  │ ca-pdp-westus3-mcp     │   (both INTERNAL ingress)
        │ webhook handler +      │  │ MCP server (hosts the  │
        │ reconciler + saga      │  │ verb layer in-process) │
        │ min replicas = 1       │  │ min replicas = 0       │
        └───────────┬───────────┘  └───────────┬───────────┘
                    │  Entra-token (UAMI)       │
                    └─────────────┬─────────────┘
                                  ▼
        ┌──────────────────────────────────────────────────┐
        │ PRIVATE Postgres flexible server (existing,        │
        │ VNet-injected, Entra-only) — ipam/registry/wolverine│
        └──────────────────────────────────────────────────┘
```

- ACA **environment** = workload-profiles, **External** (public IP required for the webhook), VNet-injected
  via `infrastructure_subnet_id = <ACA subnet>` (`10.0.0.32/27`, same VNet as Postgres).
- `ingress`: `ingress.external = true`, min 1. `api`: `ingress.external = false`, min 1. `mcp`:
  `ingress.external = false`, min 0 (scale-to-zero; ACA wakes it on inbound via YARP).
- YARP carries **no business logic** and does **not** validate the Entra JWT (the MCP server does). It
  passes the `Authorization` header through and supports streamable-HTTP passthrough for `/mcp`.

## Stack boundary & teardown

- **New stack** `infra/control-plane-host`, backend key `platform/control-plane-host`, own RG.
- **Consumes by reference** (data sources): ACA subnet id, Postgres FQDN, platform-dns RG. **Creates no
  resource in the protected ledger RG.**
- The only edit to `infra/control-plane`: declare `snet-pdp-westus3-aca 10.0.0.32/27` (delegation
  `Microsoft.App/environments`) in the AVM VNet module's `subnets` map + output its id.
- **Teardown** = a gated `controlplane-host-destroy.yml` dispatching `tofu destroy` over this stack
  (Article VIII typed confirmation). It removes every resource in §1 of `data-model.md` and **never**
  touches the `prevent_destroy`+`CanNotDelete` ledger RG. The one manual cleanup: drop the two `pgaadauth`
  principals + revoke (documented in the stack README; SC-010 "single reviewed manual step").

## Secrets & identity (cross-ref `identity-and-auth.md`)

- **Zero stored cloud secret.** GitHub App private key + webhook HMAC secret live in **Key Vault**,
  surfaced as ACA Key Vault-backed secrets resolved by the app UAMI (`Key Vault Secrets User`). No secret
  value in OpenTofu state or app config.
- Image pull = UAMI `AcrPull` on ACR Basic (no admin user, no anonymous pull).
- App→Azure (Postgres token, ARG) = the app's UAMI. **No standing cloud write credential**; infra writes
  happen only in OIDC CI.

## CI

- `controlplane-host-images.yml` (NEW): on change, build the `api`/`ingress`/`mcp` images and push to ACR
  over **OIDC** (no registry secret).
- `iac-plan.yml` / `iac-apply.yml` (existing rails): plan on PR, apply on merge for `infra/control-plane-host`.
- `controlplane-host-destroy.yml` (NEW): manual, gated `tofu destroy` (Article VIII).
