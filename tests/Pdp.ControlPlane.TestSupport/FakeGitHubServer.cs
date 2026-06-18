using WireMock.Server;

namespace Pdp.ControlPlane.TestSupport;

/// <summary>
/// A WireMock.Net stand-in for the GitHub API (research §12): the dispatch tests point the GitHub App
/// credential's <c>ApiBaseUrl</c> at <see cref="BaseUrl"/> to assert exact <c>workflow_dispatch</c>
/// inputs, drive <c>run-name</c> ↔ <c>env_id</c> correlation, and simulate a missed webhook so the
/// reconciler path can be proven — all without a live GitHub. Tests stub specific endpoints on
/// <see cref="Server"/> and inspect <c>Server.LogEntries</c> for received requests.
/// </summary>
public sealed class FakeGitHubServer : IDisposable
{
    /// <summary>The underlying WireMock server — tests configure stubs and read request logs on it.</summary>
    public WireMockServer Server { get; }

    /// <summary>The base address to hand to <c>GitHubAppOptions.ApiBaseUrl</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>Starts the fake server on an ephemeral port.</summary>
    public FakeGitHubServer()
    {
        Server = WireMockServer.Start();
        BaseUrl = Server.Url!;
    }

    /// <summary>Clears all stubs and request logs between tests.</summary>
    public void Reset() => Server.Reset();

    /// <inheritdoc />
    public void Dispose() => Server.Stop();
}
