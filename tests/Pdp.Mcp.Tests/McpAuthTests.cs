using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pdp.ControlPlane.TestSupport;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// T028 — the MCP endpoint is an OAuth 2.1 protected resource gated to a single owner (SC-005/SC-006;
/// contracts/identity-and-auth.md §B). The three boundary invariants:
/// unauthenticated ⇒ <b>401</b> with the <c>WWW-Authenticate</c> PRM challenge; a non-owner <c>oid</c> ⇒
/// <b>403</b> (zero verbs run); the owner <c>oid</c> ⇒ <b>200</b>, tools callable.
/// </summary>
/// <remarks>
/// Real Entra tokens cannot be minted in a unit test, so the JWT bearer scheme is replaced as the
/// <i>default authenticate</i> scheme by <see cref="HeaderOidAuthHandler"/> (it reads the owner <c>oid</c>
/// from a header). The <c>OwnerOnly</c> policy and the MCP <i>challenge</i> scheme (the PRM 401) are the real
/// production wiring — this exercises the policy + challenge, which is the testable, meaningful coverage.
/// The host boots fully on the Testcontainers Postgres (Wolverine durability), but no tool is invoked, so no
/// verb touches the database.
/// </remarks>
[Collection(McpTestCollection.Name)]
public sealed class McpAuthTests(ControlPlanePostgresFixture fixture)
{
    private const string OwnerOid = "owner-oid-7f3a";
    private const string McpPath = "/mcp";

    private McpAuthFactory NewFactory() => new(fixture.ConnectionString, OwnerOid);

    [Fact]
    public async Task Unauthenticated_request_is_challenged_with_401_and_the_PRM_metadata()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(InitializeRequest(oid: null));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // RFC 9728 discovery: the WWW-Authenticate challenge points the client at the PRM document.
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("resource_metadata", Case.Insensitive);
    }

    [Fact]
    public async Task The_PRM_challenge_advertises_the_configured_public_metadata_uri()
    {
        // Behind the ingress the request host is internal; the host stack supplies the PUBLIC absolute URL so
        // the challenge advertises a reachable endpoint. When set, it is returned verbatim (not request-derived).
        const string publicUri = "https://ingress.example.com/.well-known/oauth-protected-resource/mcp";
        using var factory = new McpAuthFactory(fixture.ConnectionString, OwnerOid, publicUri);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(InitializeRequest(oid: null));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain(publicUri);
    }

    [Fact]
    public async Task A_non_owner_oid_is_rejected_and_no_tool_runs()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(InitializeRequest(oid: "some-other-user"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_owner_oid_is_admitted()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(InitializeRequest(oid: OwnerOid));

        // Authorization passed → the MCP handler processed the initialize (not 401/403).
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A minimal JSON-RPC <c>initialize</c> over streamable HTTP, optionally carrying the test oid.</summary>
    private static HttpRequestMessage InitializeRequest(string? oid)
    {
        const string body =
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pdp-mcp-tests","version":"1.0.0"}}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, McpPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // The streamable-HTTP transport negotiates a JSON or SSE response.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (oid is not null)
        {
            request.Headers.Add(HeaderOidAuthHandler.OidHeader, oid);
        }

        return request;
    }

    /// <summary>
    /// Boots the real <c>pdp-mcp</c> host on the Testcontainers Postgres. Program reads its Postgres/Entra
    /// config eagerly (to wire the DbContexts + Wolverine + JWT authority before config layering), so the
    /// values go in via environment variables — the same approach the spec-006 API tests use.
    /// </summary>
    private sealed class McpAuthFactory : WebApplicationFactory<Program>
    {
        public McpAuthFactory(string connectionString, string ownerOid, string? resourceMetadataUri = null)
        {
            Environment.SetEnvironmentVariable("ControlPlane__PostgresConnectionString", connectionString);
            Environment.SetEnvironmentVariable("GitHubApp__AppId", "1");
            Environment.SetEnvironmentVariable("GitHubApp__InstallationId", "1");
            Environment.SetEnvironmentVariable("GitHubApp__Owner", "pdp-owner");
            Environment.SetEnvironmentVariable("GitHubApp__Repository", "platform");
            Environment.SetEnvironmentVariable("GitHubApp__DefaultBranch", "main");
            Environment.SetEnvironmentVariable("GitHubApp__WebhookSecret", "pdp-test-webhook-secret-0123456789");
            Environment.SetEnvironmentVariable("AzureAd__TenantId", "00000000-0000-0000-0000-000000000000");
            Environment.SetEnvironmentVariable("AzureAd__Audience", "api://pdp-mcp");
            Environment.SetEnvironmentVariable("AzureAd__OwnerOid", ownerOid);
            // The host stack supplies an absolute public PRM URL in prod (the ingress FQDN). Passing null
            // CLEARS the process env var so the other tests fall back to the request-derived default rather
            // than inheriting a value a prior factory set.
            Environment.SetEnvironmentVariable("Mcp__ResourceMetadataUri", resourceMetadataUri);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                // Swap the default *authenticate* scheme for a header-driven test handler so owner/non-owner
                // are exercisable without real tokens. The MCP *challenge* scheme (the 401 PRM) is untouched.
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderOidAuthHandler>(
                    HeaderOidAuthHandler.SchemeName, _ => { });
                services.Configure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderOidAuthHandler.SchemeName);
            });
    }

    /// <summary>Authenticates from an <c>X-Test-Oid</c> header (no header ⇒ no result ⇒ the real PRM 401 fires).</summary>
    private sealed class HeaderOidAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestOid";
        public const string OidHeader = "X-Test-Oid";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(OidHeader, out var oid) || string.IsNullOrWhiteSpace(oid))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("oid", oid.ToString())], SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
