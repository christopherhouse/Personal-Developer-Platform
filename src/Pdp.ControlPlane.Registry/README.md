# Pdp.ControlPlane.Registry

Control plane (spec 006). The Postgres **environment registry** (intent/owner/status) and
**provisioning-run audit trail**, plus the Wolverine `EnvironmentSaga` lifecycle state. Owns the
`registry` schema (snake_case via `EFCore.NamingConventions`); the natural key `(kind, subscription,
name)` enforces idempotent convergence; `env_id` (UUIDv7) is the correlation surrogate.

Division of truth: this records **intent**; *what's deployed* comes from Azure Resource Graph via
`Pdp.ControlPlane.Inventory`. Teardown = `dotnet ef database update 0` drops the schema (no Azure
footprint).

> Production ACA hosting + ingress + the App Insights resource = **spec 007**.
