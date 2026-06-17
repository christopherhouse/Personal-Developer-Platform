namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// Configuration for the <c>pdp-orchestrator</c> GitHub App — the credential the control plane uses
/// to dispatch <c>workflow_dispatch</c> and to read run/artifact status (research §4). The GitHub App
/// is the <b>only</b> non-Azure secret (FR-020); there are no PATs and no stored cloud secrets. Bound
/// from the <see cref="SectionName"/> configuration section.
/// </summary>
public sealed class GitHubAppOptions
{
    /// <summary>Configuration section name (<c>GitHubApp</c>).</summary>
    public const string SectionName = "GitHubApp";

    /// <summary>The GitHub App id.</summary>
    public int AppId { get; set; }

    /// <summary>The App's installation id on the target repository's account.</summary>
    public long InstallationId { get; set; }

    /// <summary>
    /// The App's RSA private key in PEM form. Supplied via secret/env at runtime; mutually exclusive
    /// with <see cref="PrivateKeyPath"/>. Never logged.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Path to the App's private-key PEM file (alternative to <see cref="PrivateKeyPem"/>).</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The repository owner (org or user) hosting the dispatch workflows.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The repository name hosting the dispatch workflows.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>The git ref to dispatch against (the default branch).</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>The HMAC secret GitHub signs <c>workflow_run</c> webhook deliveries with (US5).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Override for the GitHub API base address. Null uses github.com; the WireMock.Net integration
    /// tests point this at the fake GitHub server (research §12).
    /// </summary>
    public string? ApiBaseUrl { get; set; }
}
