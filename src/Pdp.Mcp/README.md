# Pdp.Mcp — the `pdp-mcp` chatops MCP server (spec 007)

The conversational front-end to the platform. An ASP.NET Core **MCP server** (MCP C# SDK, **stateless
streamable HTTP**) that **hosts the spec-006 verb layer in-process** — exactly as the `pdp` CLI does
(a direct `ProjectReference` to `Pdp.ControlPlane.Verbs`). It is a **thin adapter**: each MCP tool calls
an existing typed verb 1:1. **No reimplementation, no new verb** (FR-010, SC-003).

- **Transport**: streamable HTTP, **stateless** (fits ACA scale-to-zero).
- **Auth**: Entra **OAuth 2.1 protected resource** — the server validates the bearer JWT and authorizes a
  single allow-listed owner `oid` (no APIM). Reached publicly only via the YARP ingress (`/mcp`).
- **Article VIII gate**: mutating tools surface a plan; destroy tools require a confirmation token **and**
  a verbatim restatement of the target name — a chat turn can never destroy without it.
- **Identity**: runs under its own user-assigned managed identity (`uami-mcp`) — Entra-token auth to the
  private Postgres, Resource Graph reads, `AcrPull`, and Key Vault for the GitHub App key.
- **Tracking**: the polling reconciler runs **only on `Pdp.ControlPlane.Api`**, not on this node
  (research §13) — this node dispatches + records intent + reads.

Hosting (Azure Container Apps, VNet-integrated) is the `infra/control-plane-host` OpenTofu stack.

> Spec 007 status: **Phase 1 (Setup)** scaffolds the project. Bootstrap + tools land in Phase 2 (US2/US3).
