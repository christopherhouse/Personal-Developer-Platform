# Pdp.ControlPlane.Inventory

The reusable **inventory** component (spec 005): the live, read-only answer to *"what does PDP
manage, and where?"* It mirrors the `Pdp.ControlPlane.Ipam` shape and is consumed later by the
spec-006/007 verb / CLI / MCP layers.

## What it does

Given an injected credential, it discovers every accessible subscription, queries **Azure Resource
Graph** for resource groups tagged `pdp-managed == 'true'`, classifies each into the taxonomy, groups
workloads into environments, and surfaces tag-side drift — returning one typed `InventorySnapshot`.

- **Taxonomy** (one scope tag per RG): **fabric** (`pdp-fabric`), **platform-shared**
  (`pdp-platform` — foundations / DNS / control plane), **spoke** (`pdp-spoke`), **workload**
  (`pdp-workload`). Each item reports its subscription and region (region from the RG location;
  spokes carry no region tag).
- **Environments**: workloads grouped by `pdp-env`.
- **Coverage**: every discovered subscription marked `Queried` or `Inaccessible` — never silently
  dropped.
- **Drift** (informational, never fails the call): `Orphan` (no scope tag), `Ambiguous` (>1 scope
  tag), `Conformance` (bad/missing tag value), `Invisible` (`rg-pdp-*`-named but not
  `pdp-managed`).

## Article III stance — ARG is the only source of truth

Inventory is derived **solely** from Azure Resource Graph over the `pdp-*` tag schema
(`docs/conventions.md` §2). It never reads local records, the IPAM ledger, or OpenTofu state;
re-running recomputes from scratch. The owner-managed seed backend (`RG-TF` / `cmhtfstatesa`) is
excluded everywhere.

## Read-only & pluggable credential

The component is **read-only** — it performs no Azure mutations and provisions no identity or RBAC.
It depends only on `Azure.Core.TokenCredential`, supplied by the caller via `ArmClient`:

```csharp
var armClient = new ArmClient(credential); // DefaultAzureCredential locally; managed identity in spec 006
IInventoryService inventory = new InventoryService(new ResourceGraphReader(armClient));
InventorySnapshot snapshot = await inventory.GetSnapshotAsync();
string json = InventoryJson.Serialize(snapshot); // stable, camelCase, string enums
```

The `IResourceGraphReader` seam is the **only** Azure boundary: the classification / grouping / drift
engine is pure and unit-tested over fabricated rows (no Azure, no Testcontainers) — see
`tests/Pdp.ControlPlane.Inventory.Tests`.

## Key types

| Type | Role |
|---|---|
| `IInventoryService` / `InventoryService` | The read surface: `GetSnapshotAsync` + headline projections. |
| `IResourceGraphReader` / `ResourceGraphReader` | The ARG adapter (subscription discovery + paged queries). |
| `Classification/*` | `TagSchema`, `ResourceGroupClassifier`, `DriftDetector` — the pure engine. |
| `Model/*` | The immutable result graph (`InventorySnapshot` and its items). |
| `InventoryJson` | The canonical `System.Text.Json` serialization contract. |
