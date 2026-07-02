using GitHubJwt;
using Octokit;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// Mints GitHub App authentication for the <c>pdp-orchestrator</c> App: a short-lived JWT from the
/// App private key (GitHubJwt) is exchanged for a ~1-hour <b>installation token</b>
/// (<c>CreateInstallationToken</c>), which authenticates an installation-scoped
/// <see cref="GitHubClient"/> for <c>workflow_dispatch</c> and run/artifact reads (research §4). The
/// installation token is cached and refreshed only when near expiry, so we are not minting a token
/// per call. This is the <b>only</b> non-Azure secret in the platform (FR-020) — no PATs.
/// </summary>
public interface IGitHubAppCredential
{
    /// <summary>
    /// Returns an installation-scoped client, refreshing the cached installation token when it is
    /// within the safety margin of expiry.
    /// </summary>
    Task<IGitHubClient> CreateInstallationClientAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubAppCredential"/>
public sealed class GitHubAppCredential : IGitHubAppCredential, IDisposable
{
    // Refresh a little before the 1-hour token actually expires to avoid races at the boundary.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly GitHubAppOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ProductHeaderValue _product = new("pdp-control-plane");
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt;
    private IGitHubClient? _cachedClient;

    /// <summary>Creates the credential from bound <see cref="GitHubAppOptions"/>.</summary>
    public GitHubAppCredential(GitHubAppOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IGitHubClient> CreateInstallationClientAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedClient is not null && _cachedToken is not null && now < _cachedTokenExpiresAt - RefreshMargin)
        {
            return _cachedClient;
        }

        await GetInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
        return _cachedClient!;
    }

    private async Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedToken is not null && now < _cachedTokenExpiresAt - RefreshMargin)
        {
            return _cachedToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedToken is not null && now < _cachedTokenExpiresAt - RefreshMargin)
            {
                return _cachedToken;
            }

            var jwt = CreateJwt();
            var appClient = NewClient();
            appClient.Credentials = new Credentials(jwt, AuthenticationType.Bearer);

            var installationToken = await appClient.GitHubApps
                .CreateInstallationToken(_options.InstallationId)
                .ConfigureAwait(false);

            _cachedToken = installationToken.Token;
            _cachedTokenExpiresAt = installationToken.ExpiresAt;
            var client = NewClient();
            client.Credentials = new Credentials(_cachedToken);
            _cachedClient = client;
            return _cachedToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CreateJwt()
    {
        IPrivateKeySource keySource = _options switch
        {
            { PrivateKeyPem: { Length: > 0 } pem } => new PlainStringPrivateKeySource(pem),
            { PrivateKeyPath: { Length: > 0 } path } => new FilePrivateKeySource(path),
            _ => throw new InvalidOperationException(
                "GitHubApp configuration must supply either PrivateKeyPem or PrivateKeyPath."),
        };

        var factory = new GitHubJwtFactory(
            keySource,
            new GitHubJwtFactoryOptions
            {
                AppIntegrationId = _options.AppId,
                // GitHub caps App JWTs at 10 minutes; stay a touch under.
                ExpirationSeconds = 540,
            });

        return factory.CreateEncodedJwtToken();
    }

    private GitHubClient NewClient() =>
        string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
            ? new GitHubClient(_product)
            : new GitHubClient(_product, new Uri(_options.ApiBaseUrl));

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Adapts an in-memory PEM string to GitHubJwt's <see cref="IPrivateKeySource"/> (the library
    /// ships only a file-based source; the App key arrives as a secret string at runtime).
    /// </summary>
    private sealed class PlainStringPrivateKeySource(string pem) : IPrivateKeySource
    {
        public TextReader GetPrivateKeyReader() => new StringReader(pem);
    }
}
