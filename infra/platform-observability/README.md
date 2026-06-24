# infra/platform-observability

The platform-shared **telemetry resources**: the single **Log Analytics workspace** every PDP resource
ships diagnostics to, and the workspace-based **Application Insights** the apps export env_id-correlated
traces/metrics to. Both halves of the telemetry sink live together here. One workspace, one pane of glass.

## Why a dedicated foundational stack

"All resources send logs to Log Analytics" only works if the workspace exists **before** the resources
that log to it and is **referenceable from every stack**. So this is its own stack with state key
`platform/observability`, applied in **phase 1** of `iac-apply` alongside `foundations` and `platform-dns`
— before `control-plane`, `fabric`, `control-plane-host`, and spec-008 workloads.

It owns the **workspace only**. Each stack attaches its own resources' `diagnostic_settings` to this
workspace (via the AVM modules' `diagnostic_settings` input), resolving it by data source:

```hcl
data "azurerm_log_analytics_workspace" "platform" {
  name                = "log-pdp-westus3-platform"
  resource_group_name = "rg-pdp-westus3-observability"
}
```

This is the same loose-coupling pattern `infra/platform-dns` uses for the shared private DNS zones — no
`terraform_remote_state`, no tight cross-stack coupling, no apply-ordering fragility once phase 1 has run.

## Network posture

Log Analytics has no private path in this design, and the owner queries it from outside any VNet — so
**both ingestion and query are public** (`internet_ingestion_enabled` / `internet_query_enabled` = true),
pinned explicitly so a tightening policy can't silently break ingestion or query.

## Sizing

`PerGB2018`, 30-day retention, 1 GB/day cap. The platform is low-throughput; the cap bounds cost and is not
expected to bite. Raise `log_analytics_workspace_daily_quota_gb` if a chatty resource ever caps it out.

## Teardown

Destroyable by design (Article IV): the RG carries no lock. Like `infra/platform-dns`, this shared
foundational stack has **no dedicated destroy workflow** — it's platform infrastructure that the other
stacks depend on, not torn down independently. Teardown is a manual dispatched `tofu destroy` over this
stack, and only **after** every consumer's `diagnostic_settings` (which reference this workspace by data
source) has been removed — otherwise their plans fail on the missing workspace.
