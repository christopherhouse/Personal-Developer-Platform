# Contract — Identity & Auth

Two distinct planes: (A) how the **hosted apps authenticate to Azure** (per-app UAMI, secret-free), and
(B) how **callers authenticate to the MCP endpoint** (Entra OAuth 2.1 protected resource, single owner).

## A. Per-app managed identity (Azure-facing)

One **user-assigned managed identity per app**, least privilege (data-model §2):

| UAMI | Postgres principal | AcrPull | Subscription Reader (ARG) | Key Vault Secrets User |
|---|---|---|---|---|
| `uami-api` | yes (`ipam`+`registry`) | yes | yes | GitHub App key + webhook secret |
| `uami-mcp` | yes (`ipam`+`registry`) | yes | yes | GitHub App key |
| `uami-ingress` | no | yes | no | no |

- **Postgres**: `ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId("<client-id>"))`,
  scope `https://ossrdbms-aad.database.windows.net/.default`, via Npgsql
  `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider` (~55-min refresh; no dedicated package —
  `Microsoft.Azure.PostgreSQL.Auth` does not exist, research §5). Username = the UAMI display name
  (matched by object id).
- **Bootstrap (one-time, per Postgres UAMI, by the owner/Entra-admin)**:
  `SELECT * FROM pgaadauth_create_principal_with_oid('<uami-name>','<uami-oid>','service',false,false);`
  then `GRANT USAGE/SELECT/INSERT/UPDATE/DELETE` on the `ipam` and `registry` schemas as needed. OpenTofu
  outputs the exact command. Idempotent on re-run.
- **No standing cloud write credential**: UAMIs grant reads + secret-get + image-pull only; all infra
  writes happen in OIDC-authenticated CI.

## B. MCP endpoint auth (caller-facing) — OAuth 2.1 protected resource

```csharp
builder.Services.AddAuthentication(o =>
{
    o.DefaultChallengeScheme   = McpAuthenticationDefaults.AuthenticationScheme; // WWW-Authenticate → PRM
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(o =>
{
    o.Authority        = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    o.MapInboundClaims = false;                       // keep short "oid"
    o.TokenValidationParameters = new()
    {
        ValidateIssuer = true,  ValidIssuer   = $"https://login.microsoftonline.com/{tenantId}/v2.0",
        ValidateAudience = true, ValidAudience = "api://pdp-mcp",   // or the app-reg client id
        ValidateLifetime = true, ValidateIssuerSigningKey = true,
    };
})
.AddMcp(o => o.ResourceMetadata = new()                // publishes /.well-known/oauth-protected-resource
{
    Resource = new Uri("https://<public-host>/"),
    AuthorizationServers = { new Uri($"https://login.microsoftonline.com/{tenantId}/v2.0") },
    ScopesSupported = ["mcp:tools"],
});

builder.Services.AddAuthorization(o => o.AddPolicy("OwnerOnly", p =>
    p.RequireAuthenticatedUser().RequireClaim("oid", ownerOid)));

app.MapMcp().RequireAuthorization("OwnerOnly");
```

**Invariants (testable — SC-002, SC-006)**:
- Unauthenticated `initialize` ⇒ **401** with `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`.
- A valid token whose `oid` ≠ owner ⇒ **rejected**; zero verbs execute.
- A valid owner token ⇒ **200**; tools callable.
- `ValidateAudience`/`ValidateIssuer`/`ValidateLifetime` are always on; never `ValidateAudience = false`,
  never `common`/`organizations` issuer for this single-tenant resource.
- Identity is read **only** from the validated JWT (`ClaimsPrincipal.oid`), never from tool arguments.
- YARP does not validate the token — it forwards; the MCP server is the enforcement point.
