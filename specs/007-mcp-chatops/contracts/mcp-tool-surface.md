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
        ClaimsPrincipal caller, CancellationToken ct = default) { /* EnsureOwner; tokens.Validate; verb.ApplyDestroy */ }
}
```

- Registered with `AddMcpServer().WithTools<SpokeTools>().WithTools<FabricTools>()…` (explicit, not
  assembly-scan).
- `ClaimsPrincipal` + `CancellationToken` are injected by the SDK and excluded from the JSON schema.
- Returns are JSON-serializable verb result types; errors throw `McpException`.

## Tools (1:1 with verbs — data-model §4)

**Read** (execute and return): `QueryIpam`, `WhatsDeployed`/`ListEnvironments` (ARG inventory — division
of truth), `ShowEnvironment`, `RunHistory`, `RunStatus`.

**Mutate** (two-tool plan→confirm): `PlanSpokeVend`→`ApplySpokeVend`, `PlanFabricCreate`→`ApplyFabricCreate`.

**Destroy** (two-tool, token + verbatim target): `PlanSpokeDestroy`→`DestroySpoke`,
`PlanFabricDestroy`→`DestroyFabric`.

## Plan/confirm gate (Article VIII — FR-012/FR-013, SC-002)

1. `Plan<Op>` → calls the verb-layer plan, returns `{ planText, confirmationToken }`. No mutation.
2. `confirmationToken` is signed/opaque, single-use, ~5-min TTL, binds `{operation, targetName}`.
3. `Apply<Op>` / `Destroy<Op>` requires `confirmationToken` **and** a `target`/name param. It is rejected
   (`McpException`) unless: token valid + unused + unexpired, operation matches, and `target` == the
   token's `targetName` **verbatim**.
4. The model cannot fabricate a valid token; there is no single-call destroy and no `confirm=true` flag.
   Tool descriptions state the requirement and never call a destroy "safe."

## Invariants
- Same verb implementation as the CLI (`ProjectReference` to `Pdp.ControlPlane.Verbs`); no reimplementation.
- Read answers preserve division of truth: "what's deployed" ⇒ inventory/ARG; "what I asked/what happened"
  ⇒ registry/audit.
- Every tool calls `EnsureOwner(caller)` (or the `OwnerOnly` policy) before any verb call.
