# Contract — Verb Surface (`Pdp.ControlPlane.Verbs`)

The **single implementation** behind the `pdp` CLI now and the spec-007 MCP later (FR-001). Every
mutating verb follows the spine **validate → (allocate) → record intent → plan → confirm → apply →
track** (Articles II/VI/VIII). Signatures are indicative C#; the binding intent is the behavior + FR
mapping. All methods take a `CancellationToken`.

---

## 1. Fabric verbs (spec 003 wrapped)

```csharp
public interface IFabricVerbs
{
    Task<PlanResult>  PlanCreateAsync(FabricCreateRequest req, CancellationToken ct = default);   // dispatch mode=plan, surface plan (FR-006)
    Task<VerbResult>  CreateAsync(FabricCreateRequest req, Confirmation c, CancellationToken ct = default); // dispatch mode=apply after confirm
    Task<PlanResult>  PlanDestroyAsync(EnvRef env, CancellationToken ct = default);               // dispatch mode=plan (destroy preview)
    Task<VerbResult>  DestroyAsync(EnvRef env, Confirmation c, CancellationToken ct = default);    // REQUIRES explicit confirmation (FR-007)
}
```

- `CreateAsync` registers the region in the IPAM ledger if needed (`RegisterRegionAsync`) and
  dispatches **`fabric-vend.yml`** (the new workflow, FR-012a).
- `DestroyAsync` dispatches `fabric-destroy.yml`; confirmation is **mandatory** (Article VIII).

## 2. Spoke verbs (spec 004 wrapped; Gate-G1 closed)

```csharp
public interface ISpokeVerbs
{
    Task<PlanResult>  PlanCreateAsync(SpokeCreateRequest req, CancellationToken ct = default);
    Task<VerbResult>  CreateAsync(SpokeCreateRequest req, Confirmation c, CancellationToken ct = default);
    Task<PlanResult>  PlanDestroyAsync(EnvRef env, CancellationToken ct = default);
    Task<VerbResult>  DestroyAsync(EnvRef env, Confirmation c, CancellationToken ct = default);
}

// NOTE: SpokeCreateRequest has NO Cidr field — the block is allocated live by Size (FR-008).
public sealed record SpokeCreateRequest(string Subscription, string Region, string Name,
                                        int Size = 24, string? Owner = null);
```

**Contract notes**

- `PlanCreateAsync`/`CreateAsync` MUST allocate via `IIpamLedger.AllocateAsync(region, name, Size)`
  **before** dispatch and pass `allocation.Network` as the `spoke_cidr` input (FR-008). The
  allocate + intent-write + dispatch-enqueue are **one atomic outbox transaction** (research §6).
- `DestroyAsync` MUST, on a **successful** destroy run, call `IIpamLedger.ReleaseAsync(region, name)`
  (FR-009); a failed/partial destroy MUST NOT release (FR-011).
- Re-create of an existing `(Subscription, Name)` converges (FR-022); reusing a name for a *different*
  environment is rejected.

## 3. IPAM verbs (spec 002 wrapped)

```csharp
public interface IIpamVerbs
{
    Task<Allocation>          AllocateAsync(string region, string name, int size, CancellationToken ct = default); // FR-010
    Task                      ReleaseAsync(string region, string name, CancellationToken ct = default);
    Task<RegionView>          QueryAsync(string region, CancellationToken ct = default);
    Task<IReadOnlyList<RegionView>> QueryAllAsync(CancellationToken ct = default);
}
```

- Thin pass-through to `IIpamLedger` (read verbs always available, even during in-flight runs).
- Every CIDR the control plane hands to a workflow MUST originate here (Article VI; FR-010).

## 4. Inventory / environment verbs (spec 005 reused — FR-013)

```csharp
public interface IInventoryVerbs   // delegates to Pdp.ControlPlane.Inventory (spec 005)
{
    Task<InventorySnapshot>              GetSnapshotAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EnvironmentView>> GetEnvironmentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SpokeItem>>       GetSpokesAsync(CancellationToken ct = default);
}
```

- MUST construct `Pdp.ControlPlane.Inventory.IInventoryService` with the **control plane's
  `TokenCredential`** injected into the spec-005 pluggable seam — **no inventory logic duplicated**
  (FR-013). "What's deployed?" routes here (ARG), never to the registry (FR-016).

## 5. Run verbs (registry/audit reads — FR-015)

```csharp
public interface IRunVerbs
{
    Task<EnvironmentRecord?>            GetEnvironmentAsync(EnvRef env, CancellationToken ct = default); // intent + status
    Task<IReadOnlyList<ProvisioningRun>> GetRunsAsync(EnvRef env, CancellationToken ct = default);       // audit trail
}
```

## 6. Cross-cutting contracts (all mutating verbs)

- **Plan before apply (FR-006)**: `Create`/`Destroy` MUST be preceded by a `Plan*` whose `PlanResult`
  is surfaced; the verb layer MUST NOT dispatch `apply`/`destroy` without a corresponding confirmed
  plan.
- **Confirm before destroy (FR-007)**: `DestroyAsync` MUST require a non-bypassable `Confirmation`
  that restates the target; a missing/mismatched confirmation MUST throw before any dispatch.
- **Single-flight (FR-022a)**: a mutating verb on an environment whose status is non-terminal MUST
  throw `OperationInProgressException` (clear error) and dispatch nothing. Read verbs (IPAM query,
  inventory, run) MUST remain available.
- **Fail-fast (FR-023)**: invalid region / no fabric / non-writable subscription / exhausted address
  space / unreachable Postgres / missing GitHub App credential MUST throw **before** intent-write or
  dispatch, leaking no allocation and no orphan row.
- **Validation**: FluentValidation validates request shape before the verb runs (no prohibited deps).
- **Telemetry (FR-O1)**: every verb invocation opens an `Activity` stamped with `env_id` (research §11).
