namespace Pdp.Mcp.Auth;

/// <summary>
/// Configuration for the MCP endpoint's OAuth 2.1 protected-resource auth (data-model §3). Bound from the
/// <c>AzureAd</c> section; the deployed container app supplies the values from the host stack (tenant id,
/// the MCP app-registration audience, the single owner <c>oid</c>).
/// </summary>
public sealed class McpAuthOptions
{
    /// <summary>Configuration section name (<c>AzureAd</c>).</summary>
    public const string SectionName = "AzureAd";

    /// <summary>
    /// The Entra tenant id whose v2.0 endpoint is the authorization-server issuer the JWT is validated
    /// against (<c>https://login.microsoftonline.com/{TenantId}/v2.0</c>). Single-tenant — never
    /// <c>common</c>/<c>organizations</c>.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The protected-resource audience — the MCP app registration (<c>api://pdp-mcp</c> or its client id).
    /// <c>ValidateAudience</c> is always on; a token minted for any other audience is rejected.
    /// </summary>
    public string Audience { get; set; } = "api://pdp-mcp";

    /// <summary>
    /// The single allow-listed owner object id — the only <c>oid</c> the <c>OwnerOnly</c> policy admits.
    /// Identity is read only from the validated JWT, never from tool arguments (data-model §3/§4).
    /// </summary>
    public string OwnerOid { get; set; } = string.Empty;
}
