using System.Collections.Concurrent;
using System.Security.Cryptography;
using ModelContextProtocol;

namespace Pdp.Mcp.Confirm;

/// <summary>
/// In-memory <see cref="IConfirmationTokens"/> for the MCP host (data-model §5). Tokens are opaque random
/// ids held in a <see cref="ConcurrentDictionary{TKey,TValue}"/> — no persistence is needed: a token only
/// has to outlive the few minutes between a <c>Plan*</c> call and its <c>Apply*</c>/<c>Destroy*</c>, and a
/// host restart simply invalidates any in-flight confirmation (fail-closed). Registered as a singleton so
/// the store is shared across requests on a replica; the MCP node is stateless streamable HTTP, so a token
/// must be redeemed against the replica that issued it (acceptable for the single-owner flow).
/// </summary>
/// <remarks>
/// The token also carries the <b>planned payload</b> (the request / target ref the <c>Plan*</c> call already
/// captured), so the <c>Apply*</c>/<c>Destroy*</c> tool needs only the token + the verbatim target name —
/// the incidental inputs (subscription, region, size, …) flow from the token, not the owner re-typing them.
/// This also guarantees the apply uses exactly what was planned and reviewed (clarify 2026-06-25).
/// </remarks>
public sealed class ConfirmationTokenService(TimeProvider? timeProvider = null) : IConfirmationTokens
{
    /// <summary>
    /// The confirmation-token lifetime (~15 minutes — clarify 2026-06-24). Widened from ~5 min because the
    /// token is issued at plan <b>dispatch</b>: the window must cover the plan run's queue + execution
    /// (2–5 min) plus the owner's review, not just the review.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Issue(ConfirmationOperation operation, string targetName, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(payload);

        // 256 bits of CSPRNG entropy → an opaque, unguessable id the model cannot fabricate.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new Entry(operation, targetName.Trim(), _time.GetUtcNow() + Ttl, payload);
        return token;
    }

    /// <inheritdoc />
    public T Redeem<T>(string token, ConfirmationOperation operation, string targetName)
    {
        var entry = Validate(token, operation, targetName);
        return (T)entry.Payload;
    }

    /// <inheritdoc />
    public void Consume(string token) => _tokens.TryRemove(token, out _);

    /// <summary>
    /// Validates the token <b>without consuming it</b> and returns its entry. Throws
    /// <see cref="McpException"/> unless it is present, unexpired, matches <paramref name="operation"/>, and
    /// restates <paramref name="targetName"/> verbatim. An expired token is removed; a mismatch leaves it
    /// intact (a typo must not burn a valid confirmation; the verbatim restatement is mandatory — FR-013).
    /// Not consuming here means a rejected gated mutation (e.g. "plan not ready") leaves it redeemable.
    /// </summary>
    private Entry Validate(string token, ConfirmationOperation operation, string targetName)
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
                "Confirmation token has expired (it is valid for ~15 minutes). Re-run the Plan… tool.");
        }

        if (entry.Operation != operation ||
            !string.Equals(entry.TargetName, targetName?.Trim(), StringComparison.Ordinal))
        {
            throw new McpException(
                "Confirmation does not match the planned operation and target. The exact target name must be " +
                "restated verbatim, with the token from its own Plan… call.");
        }

        return entry;
    }

    private sealed record Entry(ConfirmationOperation Operation, string TargetName, DateTimeOffset ExpiresAt, object Payload);
}
