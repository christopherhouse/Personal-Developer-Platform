using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Pdp.Mcp.Auth;

/// <summary>
/// Wires the MCP endpoint as an Entra OAuth 2.1 protected resource (contracts/identity-and-auth.md §B,
/// data-model §3): JWT bearer validation against the single-tenant v2.0 issuer, the MCP SDK's
/// protected-resource-metadata publisher (the <c>/.well-known/oauth-protected-resource</c> document and
/// the <c>WWW-Authenticate</c> challenge that lets the client discover the flow), and the single-owner
/// <c>oid</c> authorization policy. YARP only forwards the <c>Authorization</c> header — THIS server is the
/// enforcement point.
/// </summary>
public static class OwnerAuthorization
{
    /// <summary>The authorization policy admitting only the owner <c>oid</c>; applied to <c>MapMcp()</c>.</summary>
    public const string PolicyName = "OwnerOnly";

    /// <summary>
    /// Registers authentication (MCP challenge + JWT bearer + PRM) and the <c>OwnerOnly</c> policy.
    /// </summary>
    public static IServiceCollection AddOwnerAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var auth = configuration.GetSection(McpAuthOptions.SectionName).Get<McpAuthOptions>()
                   ?? new McpAuthOptions();

        // Expose the bound options for DI so the tools' owner check (OwnerTool.EnsureOwner) reads the same
        // single allow-listed oid the OwnerOnly policy enforces.
        services.Configure<McpAuthOptions>(configuration.GetSection(McpAuthOptions.SectionName));

        // Single-tenant v2.0 issuer = authorization server = the only valid token issuer.
        var authority = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0";

        services.AddAuthentication(options =>
            {
                // An unauthenticated call is challenged by the MCP scheme, which emits
                // `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`
                // so the client can discover the Entra flow; tokens are authenticated by JWT bearer.
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                // Keep the short "oid" claim (Entra would otherwise remap it to a long URI).
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = auth.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                };
            })
            // Publishes the protected-resource metadata (RFC 9728) advertising the issuer + scope.
            .AddMcp(options =>
            {
                options.ResourceMetadata = new()
                {
                    AuthorizationServers = { authority },
                    ScopesSupported = ["mcp:tools"],
                };
            });

        services.AddAuthorization(options => options.AddPolicy(PolicyName, policy =>
            policy.RequireAuthenticatedUser().RequireClaim("oid", auth.OwnerOid)));

        return services;
    }
}
