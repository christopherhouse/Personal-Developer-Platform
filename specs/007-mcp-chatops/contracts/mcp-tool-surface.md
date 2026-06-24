# Contract — MCP Tool Surface

The `pdp-mcp` server exposes the spec-006 verbs as MCP tools. It is a **thin adapter**: each tool method
constructor-injects a verb interface and calls it 1:1 — **no domain logic, no new capability** (FR-010,
SC-003). The Article VIII gate is **surfaced**, not reimplemented (it lives in the verb layer).

## Tool class shape

```csharp
[McpServerToolType]
public sealed class SpokeTools(ISpokeVerbs spoke, IConfirmationTokens tokens)
{
    [McpServerTool, Description("Plan a spoke vend. Returns the plan and a confirmation token. " +
        "Does NOT create anything. Call ApplySpokeVend with the token to proceed.")]
    public Task<SpokePlanResult> PlanSpokeVend(string region, string spokeName, string size,
        ClaimsPrincipal caller, CancellationToken ct = default) { /* EnsureOwner; verb.PlanCreate; issue token */ }

    [McpServerTool, Description("DANGER: destroys a spoke. Requires the confirmation token from " +
        "PlanSpokeDestroy AND the exact spoke name. Rejected if the name does not match the token.")]
    public Task<SpokeDestroyResult> DestroySpoke(string confirmationToken, string spokeName,
        ClaimsPrincipal caller, CancellationToken ct = default)
        { /* EnsureOwner; match token (operation+verbatim target); verb.ApplyDestroy (pure read+dispatch,
             no reconcile); consume the token ONLY on a successful gated dispatch — preserve it on
             "plan not ready"/"plan failed"/mismatch */ }
}
```

- Registered with `AddMcpServer().WithTools<SpokeTools>().WithTools<FabricTools>()…` (explicit, not
  assembly-scan).
- `ClaimsPrincipal` + `CancellationToken` are injected by the SDK and excluded from the JSON schema.
- Returns are JSON-serializable verb result types; errors throw `McpException`.

## Tools (1:1 with verbs — data-model §4)

**Read** (execute and return): `QueryIpam`, `WhatsDeployed`/`ListEnvironments` (ARG inventory — division
of truth), `ShowEnvironment`, `RunHistory`, `RunStatus`. The **status-check reads** `ShowEnvironment` and
`RunStatus` additionally **reconcile the run on demand** before reading (see the gate below); the others
do not.

**Mutate** (two-tool plan→confirm): `PlanSpokeVend`→`ApplySpokeVend`, `PlanFabricCreate`→`ApplyFabricCreate`.

**Destroy** (two-tool, token + verbatim target): `PlanSpokeDestroy`→`DestroySpoke`,
`PlanFabricDestroy`→`DestroyFabric`.

## Plan/confirm gate (Article VIII — FR-012/FR-013/FR-018/FR-019/FR-020, SC-002/SC-012)

**Asynchronous, non-blocking, owner-driven** (clarify 2026-06-24). **No tool call ever blocks** waiting for
a GitHub Actions workflow to finish — `Plan*` and `Apply*`/`Destroy*` are dispatch-and-return; only the
status-read tools advance run state.

1. `Plan<Op>` → **dispatches** the verb-layer plan run and returns immediately `{ envId, runHandle,
   confirmationToken }`. The plan run has **not finished** yet, so there is **no plan output** in this
   result, and **nothing is mutated**. The call does not poll or block.
2. The owner reviews the plan via a **status read** (`ShowEnvironment` / `RunStatus`), which **reconciles
   the run on demand** — correlate by run-name → poll GitHub Actions → idempotently record the terminal
   outcome (first-terminal-wins) → surface the **captured plan output + run link**. Reconciliation lives
   **only** in these status reads; the always-on Api node's reconciler/webhook is a background safety net,
   not a dependency of the chat flow.
3. `confirmationToken` is opaque, single-use, **~15-min TTL** (issued at plan dispatch, so the window
   covers plan queue + run + human review — clarify 2026-06-24), binds `{operation, targetName}`.
4. `Apply<Op>` / `Destroy<Op>` requires `confirmationToken` **and** a `target`/name param. It is a **pure
   registry read + dispatch** — it does **not** reconcile or poll. It dispatches the gated mutation **only
   when the plan run is recorded `Succeeded`** (which a prior status read recorded — that read is the
   Article VIII review), and returns the tracked run handle immediately. Rejected (`McpException`) unless:
   token valid + unused + unexpired, operation matches, and `target` == the token's `targetName`
   **verbatim**.
5. If the plan run has **not succeeded**, `Apply`/`Destroy` returns a **distinct, retryable** response —
   *"plan hasn't finished yet — check its status and retry"* (in flight) or *"the plan failed; nothing to
   apply"* (failed) — **kept separate** from the single-flight rejection (a real concurrent mutating
   operation already in flight). The token is **preserved** (not consumed) in these cases and on any
   operation/target mismatch; it is consumed **only** when a gated mutation is actually dispatched.
6. The model cannot fabricate a valid token; there is no single-call destroy and no `confirm=true` flag.
   Tool `[Description]`s state the confirmation requirement, make the **plan → check status → apply**
   sequence explicit (so the agent doesn't skip straight to apply), and never call a destroy "safe."

## Invariants
- Same verb implementation as the CLI (`ProjectReference` to `Pdp.ControlPlane.Verbs`); no reimplementation.
  The async/non-blocking flow and on-demand reconcile **reuse** the spec-006 run-tracking/reconcile logic
  (`IRunTracker.ReconcileInFlightAsync`) — **no new verb**, no change to the verb-layer single-flight
  invariant.
- **Reconciliation lives only in the status-read tools** (`ShowEnvironment`/`RunStatus`). `Plan*`,
  `Apply*`, and `Destroy*` never reconcile or poll.
- No tool call blocks on a workflow; every mutation is dispatch-and-track.
- Read answers preserve division of truth: "what's deployed" ⇒ inventory/ARG; "what I asked/what happened"
  ⇒ registry/audit.
- Every tool calls `EnsureOwner(caller)` (or the `OwnerOnly` policy) before any verb call.
