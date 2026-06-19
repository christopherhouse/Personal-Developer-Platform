# Quickstart — Live Validation (Spec 007)

Runnable scenarios proving the feature works end to end, mapped to Success Criteria. These run against the
**live `westus3` platform** (specs 002–006 merged). No implementation code here — see `contracts/` and
`data-model.md`. Destroy is the final, mandatory step (Article IV).

## Prerequisites

- Specs 002–006 merged & live: IPAM ledger + `westus3` fabric + `app1`/`app2` spokes + the control plane;
  the pdp-orchestrator GitHub App is set up (private key + webhook secret available to seed into Key Vault).
- The owner is the Postgres Entra admin (existing) and can reach the private server in-VNet for the
  one-time principal bootstrap (jump path / VNet access).
- An Entra **app registration** for `pdp-mcp` (audience), and the owner's `oid`.
- OpenTofu 1.11.x; CI dispatch rights; `az login` (read-only) for verification queries.

## Scenario 0 — Deploy the host stack (US1; SC-001, SC-011)

1. Add the ACA subnet to `infra/control-plane` (VNet stack); plan-on-PR / apply-on-merge.
2. Seed Key Vault inputs (GitHub App private key + webhook secret) via the secure pipeline path (never in
   tofu vars/state).
3. Merge `infra/control-plane-host` → `iac-apply` dispatches `tofu apply` (OIDC). Build/push images
   (`controlplane-host-images.yml`, OIDC to ACR).
4. Run the **one-time bootstrap** the stack outputs: `pgaadauth_create_principal_with_oid(...)` + `GRANT`
   for `uami-api` and `uami-mcp` (research §6).
- **Expect**: 3 container apps running; `ingress` external, `api`/`mcp` internal; **zero** portal/`az`
  mutations and **zero** in-process `tofu` (verify from run logs) → **SC-011**.

## Scenario 1 — Hosted control plane reaches the private ledger via UAMI (US1; SC-001)

- Exec/log into the `api` app; confirm it authenticated to Postgres with `uami-api` (Entra token, **no
  password**) and read the registry/ledger. Repeat for `mcp` with `uami-mcp`.
- **Expect**: successful Entra-token DB connection from in-VNet — the thing the laptop could not do.

## Scenario 2 — MCP auth gate (US2; SC-005, SC-006)

- Hit `https://<public-host>/mcp` unauthenticated → **401** + `WWW-Authenticate` pointing at
  `/.well-known/oauth-protected-resource`.
- Call with a valid token whose `oid` ≠ owner → **rejected**, no verb runs.
- Call with the owner token → **200**, `tools/list` returns the verbs.
- Probe `api`/`mcp` internal FQDNs from outside the environment → **not reachable**; only YARP is public.

## Scenario 3 — Vend a spoke through chat, with the plan gate (US2; SC-002, SC-003, SC-008/009 via verbs)

- From an MCP client (Claude) as the owner: ask to vend a spoke. Tool flow: `PlanSpokeVend` returns the
  plan + a confirmation token (no mutation); after explicit go-ahead, `ApplySpokeVend` dispatches.
- **Expect**: the plan is surfaced **before** apply; the run dispatches and is tracked to terminal by
  `env_id` (the `api` reconciler/webhook); the new spoke appears via `WhatsDeployed`.

## Scenario 4 — Confirm-before-destroy is unbypassable (US2; SC-002)

- Ask to destroy the spoke. `PlanSpokeDestroy` returns plan + token. Call `DestroySpoke` **without** the
  token, or with a **wrong** spoke name → **rejected**. Supply token + the **verbatim** name → proceeds.
- **Expect**: no chat phrasing destroys without the token + restated target; the IPAM allocation is
  released and the env marked `destroyed` on success.

## Scenario 5 — Conversational reads & division of truth (US3; SC-004)

- Ask "what's deployed?" (→ inventory/ARG), "allocations in westus3?" (→ IPAM), "what did I ask for /
  what happened?" (→ registry/audit).
- **Expect**: deployed-state from ARG; intent/history from Postgres; the two are distinct.

## Scenario 6 — Telemetry by env_id (US4; SC-008)

- Take an `env_id` from Scenario 3/4 and query Application Insights.
- **Expect**: the verb invocation → dispatch → run-state transitions → terminal outcome are correlated and
  traceable from that single `env_id`.

## Scenario 7 — Spec-006 live acceptance unblocked (US1; SC-009)

- Run the deferred spec-006 quickstart **T071/T072** against the hosted control plane (live spoke
  vend/destroy on `westus3`, tracked to terminal).
- **Expect**: runnable and passing — the deferral closed because the control plane is now in-VNet next to
  the ledger.

## Scenario 8 — Teardown leaves zero residual footprint (US5; SC-010)

- Dispatch `controlplane-host-destroy.yml` (typed confirmation) → `tofu destroy` over
  `infra/control-plane-host`. Drop the two `pgaadauth` principals (the one documented manual step).
- **Expect (verify via Resource Graph)**: **zero** spec-7 resources remain (ACA env+apps, 3 UAMIs, ACR,
  Log Analytics, App Insights, Key Vault); the `prevent_destroy`+`CanNotDelete` **ledger RG is untouched**;
  no dangling role grants or orphaned Entra registrations.

---

### Success-criteria coverage map

| SC | Scenario(s) |
|---|---|
| SC-001 hosted, in-VNet, UAMI→Postgres | 0, 1 |
| SC-002 vend→destroy via chat + gate | 3, 4 |
| SC-003 same verb impl, no duplication | 3 (+ build: `ProjectReference`) |
| SC-004 division of truth in chat reads | 5 |
| SC-005 single public surface | 0, 2 |
| SC-006 non-owner rejected | 2 |
| SC-007 no stored cloud secret | 0 (KV + UAMI; verify config) |
| SC-008 env_id telemetry | 6 |
| SC-009 T071/T072 unblocked | 7 |
| SC-010 isolated teardown | 8 |
| SC-011 OpenTofu-dispatched, AVM, smallest SKU | 0 |
