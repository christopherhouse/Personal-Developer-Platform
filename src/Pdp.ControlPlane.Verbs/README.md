# Pdp.ControlPlane.Verbs

Control plane (spec 006). The **single typed verb layer** — the implementation the `pdp` CLI (and a
future MCP server, spec 007) wrap. Each mutating verb: validate (FluentValidation) → allocate address
space from the IPAM ledger (`Pdp.ControlPlane.Ipam`, Gate-G1) → record intent
(`Pdp.ControlPlane.Registry`) → dispatch a workflow (`Pdp.ControlPlane.Dispatch`) → track to a
recorded outcome, all correlated by `env_id`. Plan-before-apply and confirm-before-destroy (Article
VIII) live here as the two-phase `PlanConfirm` orchestration over the `EnvironmentSaga`. Inventory/env
verbs delegate to `Pdp.ControlPlane.Inventory` (ARG) with the injected `TokenCredential` — no
reimplementation.

Host-agnostic by design (no ASP.NET dependency) so the console CLI can consume it directly.

> Production ACA hosting + ingress + the App Insights resource = **spec 007**.
