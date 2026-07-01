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

                // Behind the YARP ingress + ACA, Request.Scheme/Host are this app's INTERNAL http FQDN, so
                // the SDK would advertise an internal, externally-unreachable PRM URL in the WWW-Authenticate
                // challenge and the metadata document. When the host stack supplies the PUBLIC absolute URL
                // (Mcp:ResourceMetadataUri = the external ingress FQDN's /.well-known/oauth-protected-resource
                // /<resource-path>), the handler returns it verbatim instead of deriving it from the request.
                // Empty (local dev / tests) → the SDK falls back to the request-derived relative URL.
                var publicPrmUri = configuration["Mcp:ResourceMetadataUri"];
                if (!string.IsNullOrWhiteSpace(publicPrmUri))
                {
                    var prmUri = new Uri(publicPrmUri, UriKind.Absolute);
                    options.ResourceMetadataUri = prmUri;

                    // Setting a custom ResourceMetadataUri turns OFF the SDK's request-derivation of the
                    // RFC 9728 `resource` identifier: on the configured-endpoint path
                    // McpAuthenticationHandler serves the document with derivedResource=null, so
                    // ResourceMetadata.Resource stays null and it throws
                    // ("ResourceMetadata.Resource could not be determined") → HTTP 500. That is the latent
                    // second half of issue #44 — once UseForwardedHeaders makes Host/Scheme match the
                    // configured URI (so the handler stops 404ing and actually serves), this null would 500.
                    // Supply it explicitly: the canonical URL of the protected resource is the PRM URL with
                    // the well-known prefix stripped — exactly what the SDK derives from the request on the
                    // default path (…/.well-known/oauth-protected-resource/mcp → …/mcp).
                    const string wellKnownPrefix = "/.well-known/oauth-protected-resource";
                    var resourcePath = prmUri.AbsolutePath.StartsWith(wellKnownPrefix, StringComparison.Ordinal)
                        ? prmUri.AbsolutePath[wellKnownPrefix.Length..]
                        : prmUri.AbsolutePath;
                    options.ResourceMetadata.Resource = $"{prmUri.GetLeftPart(UriPartial.Authority)}{resourcePath}";
                }
            });

        services.AddAuthorization(options => options.AddPolicy(PolicyName, policy =>
            policy.RequireAuthenticatedUser().RequireClaim("oid", auth.OwnerOid)));

        return services;
    }
}
