# Pdp.ControlPlane.Dispatch

Control plane (spec 006). The **execution-plane boundary** (constitution Article II): the control
plane *dispatches*, it never runs OpenTofu in-process. This is the only GitHub-touching code —
GitHub App auth (`GitHubJwt` JWT → installation token), `workflow_dispatch`, `run-name ↔ env_id`
correlation, the idempotent terminal-status `RunTracker`, and the Wolverine-scheduled `RunReconciler`
(≤60 s sweep for missed webhooks).

`IWorkflowDispatcher` / `IRunTracker` are seams (mirroring spec 005's `IResourceGraphReader`) so the
version-sensitive Octokit/webhook surface is exercised by WireMock.Net in tests.

> Production ACA hosting + ingress + the App Insights resource = **spec 007**.
