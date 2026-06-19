using System.Collections.Concurrent;
using System.Security.Cryptography;
using ModelContextProtocol;

namespace Pdp.Mcp.Confirm;

/// <summary>
/// In-memory <see cref="IConfirmationTokens"/> for the MCP host (data-model §5). Tokens are opaque random
/// ids held in a <see cref="ConcurrentDictionary{TKey,TValue}"/> — no persistence is needed: a token only
/// has to outlive the few seconds between a <c>Plan*</c> call and its <c>Apply*</c>/<c>Destroy*</c>, and a
/// host restart simply invalidates any in-flight confirmation (fail-closed). Registered as a singleton so
/// the store is shared across requests on a replica; the MCP node is stateless streamable HTTP, so a token
/// must be redeemed against the replica that issued it (acceptable for the single-owner flow).
/// </summary>
public sealed class ConfirmationTokenService(TimeProvider? timeProvider = null) : IConfirmationTokens
{
    /// <summary>The confirmation-token lifetime (~5 minutes — contracts/mcp-tool-surface.md §plan/confirm).</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Issue(ConfirmationOperation operation, string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        // 256 bits of CSPRNG entropy → an opaque, unguessable id the model cannot fabricate.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new Entry(operation, targetName.Trim(), _time.GetUtcNow() + Ttl);
        return token;
    }

    /// <inheritdoc />
    public void Validate(string token, ConfirmationOperation operation, string targetName)
    {
        if (string.IsNullOrWhiteSpace(token) || !_tokens.TryGetValue(token, out var entry))
        {
            throw new McpException(
                "Confirmation token is missing or unknown. Run the matching Plan… tool first to obtain one.");
        }

        if (_time.GetUtcNow() >= entry.ExpiresAt)
        {
            _tokens.TryRemove(token, out _);
            throw new McpException(
                "Confirmation token has expired (it is valid for ~5 minutes). Re-run the Plan… tool.");
        }

        if (entry.Operation != operation ||
            !string.Equals(entry.TargetName, targetName?.Trim(), StringComparison.Ordinal))
        {
            // Leave the token in place: a mismatched operation or mistyped target must not consume a token
            // the owner can still redeem correctly. The verbatim restatement is mandatory (FR-013).
            throw new McpException(
                "Confirmation does not match the planned operation and target. The exact target name must be " +
                "restated verbatim, with the token from its own Plan… call.");
        }

        // Single-use: a token is good for exactly one mutation (Article VIII / SC-002).
        _tokens.TryRemove(token, out _);
    }

    private sealed record Entry(ConfirmationOperation Operation, string TargetName, DateTimeOffset ExpiresAt);
}
