# notify-sre-agent

Composite action that POSTs failure context for the current workflow run to an
[Azure SRE Agent HTTP trigger](https://learn.microsoft.com/en-us/azure/sre-agent/http-triggers),
so the agent starts investigating a red run without an owner in the loop.

**Best-effort by design**: an unset variable, a failed Azure login, an invalid
payload, or an unreachable/erroring trigger endpoint only produce `::warning::`
annotations — the action never fails the calling workflow.

## Payload

The JSON body becomes part of the agent's prompt (per the Learn doc), so it carries
everything useful for triage:

| Field | Content |
|---|---|
| `summary` | One-sentence human/agent-readable statement of what failed where. |
| `repository`, `workflow`, `run_id`, `run_attempt`, `run_url`, `display_title` | Run identity. `display_title` carries the control plane's `pdp <mode> <env_id>` correlation surrogate on dispatched verb workflows. |
| `event`, `branch`, `sha`, `actor`, `commit_message` | What triggered the run. |
| `failed_jobs[]` | Per failed job (cap 5): name, HTML URL, **failed steps**, **error annotations** (`::error::` lines and `Process completed with exit code N`), and a ~60-line ANSI-stripped **log tail** — the closest thing the API offers to a failure reason. |
| `dispatch_inputs` | `workflow_dispatch` inputs (env_id / mode / region / spoke_name on the verb workflows). |
| `pull_request` | Number/title/URL when the run belongs to a PR. |
| `extra` | Whatever JSON object the call site passed via `extra-context`. |

## Usage (repo convention)

Every workflow appends a `notify-sre-agent` job that `needs` all of its jobs:

```yaml
  notify-sre-agent:
    needs: [<every job in this workflow>]
    if: ${{ failure() && vars.SRE_AGENT_TRIGGER_URL != '' }}
    runs-on: ubuntu-latest
    permissions:
      contents: read   # checkout (this local composite action)
      id-token: write  # azure/login OIDC → bearer token for the trigger
      actions: read    # this run's jobs + logs (failure context)
      checks: read     # per-job error annotations
    steps:
      - uses: actions/checkout@v5
      - uses: ./.github/actions/notify-sre-agent
        with:
          trigger-url: ${{ vars.SRE_AGENT_TRIGGER_URL }}
          azure-client-id: ${{ vars.AZURE_CLIENT_ID }}
          azure-tenant-id: ${{ vars.AZURE_TENANT_ID }}
          azure-subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
```

`failure()` at job level fires when **any** job in the `needs` chain failed (even if
others were skipped); the whole job is skipped when the run is green, cancelled, or
the `SRE_AGENT_TRIGGER_URL` repo variable is unset.

## Prerequisites

1. **Repo variable `SRE_AGENT_TRIGGER_URL`** — the trigger's webhook URL
   (`https://<agent>.azuresre.ai/api/v1/httptriggers/trigger/<trigger-id>`), plain
   variable, not a secret (it's useless without a token — matches FR-012's
   zero-stored-secrets posture).
2. **RBAC on the agent resource** — the trigger endpoint authenticates with an Entra
   bearer token; the caller needs `Microsoft.App/agents/threads/write` on the SRE
   Agent resource. Grant the CI app registration (`AZURE_CLIENT_ID`) a role carrying
   that action on the agent (one-time, out-of-band — the agent itself is not
   PDP-managed infrastructure).
3. **Token audience** — defaults to the SRE Agent first-party app ID
   (`59f0a04a-b322-4310-adc9-39ac41e9631e`) per the Learn troubleshooting guidance;
   on a 401 the action retries once with an ARM-audience token (the doc's body
   describes the header as an ARM token). Override via the `token-resource` input if
   the service settles on something else.
