# Pdp.Inventory.Demo

A thin, demonstrable console harness for the spec-005 inventory component. It proves the success
criteria live against the deployed `westus3` platform.

> **This is a demo harness, not the `pdp` CLI.** The real CLI (and the MCP server) arrive in spec
> 006 wrapping the same `Pdp.ControlPlane.Inventory` component. This project exists only to
> demonstrate the component end-to-end; don't build features on it.

## What it does

Builds a `DefaultAzureCredential` (the owner's `az login`), constructs an `ArmClient`, and runs one
`InventoryService.GetSnapshotAsync()` — then renders the single snapshot two ways.

## Usage

```powershell
# human-readable: coverage, environments, spokes-by-subscription/region, fabrics, platform, drift
dotnet run --project src/Pdp.Inventory.Demo

# machine-readable: the same snapshot serialized as JSON (no re-query) — pure JSON to stdout
dotnet run --project src/Pdp.Inventory.Demo -- --json
```

`--json` emits the canonical structured shape (`InventoryJson`): camelCase property names, enums as
strings — consumable with `jq` or any JSON reader, no text scraping.

## Prerequisites

- .NET 10 SDK (pinned by `global.json`).
- `az login` as the platform owner with **read** access to the platform and target subscription(s).

## Read-only

The harness issues only Azure Resource Graph reads — no mutations, no provisioned identity/RBAC,
nothing to tear down (Article III / SC-007 / SC-009). It is the only place a concrete credential is
constructed; the component itself takes an injected `TokenCredential`.
