using Octokit;
using Pdp.ControlPlane.Dispatch;

namespace Pdp.ControlPlane.TestSupport;

/// <summary>
/// An <see cref="IGitHubAppCredential"/> test double that hands out an Octokit client pointed at the
/// WireMock fake-GitHub base URL with a dummy token — so the dispatch/reconcile tests exercise the real
/// Octokit request shaping (paths, bodies) without minting a real App JWT or hitting github.com.
/// </summary>
public sealed class StubGitHubAppCredential(string baseUrl) : IGitHubAppCredential
{
    /// <inheritdoc />
    public Task<IGitHubClient> CreateInstallationClientAsync(CancellationToken cancellationToken = default)
    {
        var client = new GitHubClient(new ProductHeaderValue("pdp-tests"), new Uri(baseUrl))
        {
            Credentials = new Credentials("test-installation-token"),
        };
        return Task.FromResult<IGitHubClient>(client);
    }
}
