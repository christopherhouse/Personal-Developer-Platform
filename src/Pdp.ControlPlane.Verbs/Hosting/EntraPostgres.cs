using Azure.Core;
using Npgsql;

namespace Pdp.ControlPlane.Verbs.Hosting;

/// <summary>
/// The shared seam for reaching the private, Entra-only platform Postgres with a managed-identity
/// access token instead of a password. Builds an <see cref="NpgsqlDataSource"/> whose connections are
/// authenticated by an Entra token, supplied through Npgsql's periodic-password provider and refreshed
/// ahead of expiry (research §5). Every in-VNet host that talks to the ledger — the spec-007 MCP server
/// and the spec-006 Api — uses this; the laptop/CLI keeps its plain connection string.
/// </summary>
/// <remarks>
/// There is deliberately NO <c>Microsoft.Azure.PostgreSQL.Auth</c> package — it does not exist on NuGet
/// (an earlier research draft assumed one). The mechanism is Npgsql's
/// <see cref="NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider"/> returning an
/// <see cref="Azure.Core.TokenCredential"/> token, needing nothing beyond <c>Azure.Identity</c> and the
/// Npgsql the verb layer already pulls in.
/// </remarks>
public static class EntraPostgres
{
    /// <summary>
    /// The AAD token scope for Azure Database for PostgreSQL flexible server
    /// (<c>https://ossrdbms-aad.database.windows.net/.default</c>).
    /// </summary>
    public const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// Builds a token-authenticated <see cref="NpgsqlDataSource"/> from <paramref name="connectionString"/>
    /// — which carries Host / Database / Username (the UAMI display name) / SSL settings but NO password —
    /// using <paramref name="credential"/> as the token source.
    /// </summary>
    /// <param name="connectionString">A password-less connection string (Username = the UAMI display name).</param>
    /// <param name="credential">The per-app managed-identity credential the token is requested from.</param>
    public static NpgsqlDataSource CreateDataSource(string connectionString, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);

        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // The provider is invoked per new physical connection and re-fetched on the success cadence
        // (~55 min — comfortably inside the token's lifetime). On a transient token failure Npgsql retries
        // on the shorter failure cadence rather than caching the error for the full success interval.
        builder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) =>
            {
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext([TokenScope]), cancellationToken)
                    .ConfigureAwait(false);
                return token.Token;
            },
            successRefreshInterval: TimeSpan.FromMinutes(55),
            failureRefreshInterval: TimeSpan.FromSeconds(10));

        return builder.Build();
    }
}
