# Contract — Inventory Interfaces

The boundaries `Pdp.ControlPlane.Inventory` exposes (its read surface, consumed by spec 006/007) and
the seam it depends on (the ARG reader, the only Azure boundary). Signatures are indicative C#; the
binding intent is the behavior and the FR mapping.

---

## 1. `IInventoryService` — the read surface (consumed by specs 006/007)

The typed, structured surface the verb/CLI/MCP layers will wrap. All methods are read-only, always
succeed in the face of drift (FR-016), and accept a `CancellationToken`.

```csharp
public interface IInventoryService
{
    // Full live sweep: discover subscriptions → query ARG → classify → group → drift. (US1, FR-001..006)
    Task<InventorySnapshot> GetSnapshotAsync(CancellationToken ct = default);

    // Headline-question conveniences, each a projection over a fresh-or-supplied snapshot. (US2)
    Task<IReadOnlyList<EnvironmentView>> GetEnvironmentsAsync(CancellationToken ct = default);      // FR-005/007
    Task<IReadOnlyList<SpokeItem>>       GetSpokesAsync(CancellationToken ct = default);            // FR-007
    Task<EnvironmentView?>               GetEnvironmentAsync(string name, CancellationToken ct = default); // FR-008 (null/empty if none)
}
```

**Contract notes**

- `GetSnapshotAsync` MUST derive everything from ARG (FR-002); it MUST NOT read local records, the
  IPAM ledger, or OpenTofu state.
- The result MUST be a typed object graph (FR-010) — consumers never parse formatted text.
- Subscriptions the credential cannot read MUST appear in `Snapshot.Coverage` as `Inaccessible`,
  never be silently dropped (FR-011 / SC-008).
- Drift findings MUST be present in `Snapshot.Drift`; the call MUST NOT fail because drift exists
  (FR-016).
- The owner-managed seed backend MUST NOT appear in any taxonomy list or drift finding (FR-018).
- A freshly vended spoke/fabric/workload MUST appear with no code change (FR-009) — the surface is
  purely tag/Graph-driven.

---

## 2. `IResourceGraphReader` — the Azure seam (testability boundary)

The single Azure-touching abstraction. The production adapter wraps
`Azure.ResourceManager.ResourceGraph`; tests substitute it with NSubstitute to drive the pure engine
with fabricated rows (no Azure).

```csharp
public interface IResourceGraphReader
{
    // Subscriptions the injected credential can enumerate (FR-001) + their coverage status (FR-011).
    Task<IReadOnlyList<SubscriptionCoverage>> DiscoverSubscriptionsAsync(CancellationToken ct = default);

    // Managed RG rows (pdp-managed == 'true') across the given subscription scope, fully paged. (FR-003)
    Task<IReadOnlyList<ResourceGroupRow>> QueryManagedResourceGroupsAsync(
        IReadOnlyList<string> subscriptionIds, CancellationToken ct = default);

    // rg-pdp-* named RGs lacking pdp-managed == 'true' — invisible-drift candidates. (FR-014)
    Task<IReadOnlyList<ResourceGroupRow>> QueryLooksManagedUntaggedAsync(
        IReadOnlyList<string> subscriptionIds, CancellationToken ct = default);
}
```

**Contract notes**

- Implementations MUST page to completion via `SkipToken` (no silent 1000-row truncation;
  research §2). A bounded result MUST be logged, never silently capped.
- `DiscoverSubscriptionsAsync` MUST mark unreadable subscriptions `Inaccessible` rather than throw
  (coverage honesty); `allowPartialScopes` keeps a single bad scope from failing the sweep.
- The reader MUST receive its `TokenCredential`/`ArmClient` by injection (FR-019); it MUST NOT
  construct a credential or hardcode subscriptions.

---

## 3. Credential contract (FR-019)

- The component depends only on `Azure.Core.TokenCredential`, supplied by the caller.
- Demo harness (`Pdp.Inventory.Demo`) injects `DefaultAzureCredential` (owner's `az login`).
- Spec 006 control plane injects its managed identity — **no source change** in the component.
- This spec provisions **no** Azure identity or RBAC (SC-009).

---

## 4. Rendering contract (FR-010 / SC-006)

The demo harness renders one `InventorySnapshot` two ways from the same object:

- **Human**: grouped tables (environments, spokes-by-subscription/region, fabrics, drift summary).
- **`--json`**: `System.Text.Json` serialization of the snapshot — the machine-readable proof that
  the result is structured, not display-only.

Both renderings MUST derive from the identical typed graph; neither re-queries ARG.
