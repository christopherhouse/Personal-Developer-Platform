# Pdp.ControlPlane.Api

Control plane (spec 006). The internal ASP.NET Core host: the GitHub `workflow_run` webhook handler
(`Octokit.Webhooks.AspNetCore`, HMAC-validated), the Wolverine durable outbox/inbox, the polling
`RunReconciler` schedule, and Azure Monitor OpenTelemetry (`env_id`-correlated). Built **host-ready**;
the durable reconciler lives here (not in the CLI).

> Production ACA hosting + ingress + the App Insights resource = **spec 007**. The single public
> surface is the webhook, fronted by `Pdp.ControlPlane.Ingress` (the Article IX-sanctioned exception).
