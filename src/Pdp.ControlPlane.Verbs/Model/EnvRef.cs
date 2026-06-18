using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Verbs.Model;

/// <summary>
/// A reference to a managed environment — either by its surrogate <c>env_id</c> or by its natural
/// key <c>(Kind, Subscription, Name)</c> (data-model §1). Read and destroy verbs accept either form;
/// the verb layer resolves it to the single <see cref="Environment"/> row.
/// </summary>
public sealed record EnvRef
{
    /// <summary>The surrogate correlation key, when referencing by id.</summary>
    public Guid? EnvId { get; init; }

    /// <summary>The environment kind, when referencing by natural key.</summary>
    public EnvironmentKind? Kind { get; init; }

    /// <summary>The target subscription, when referencing by natural key.</summary>
    public string? Subscription { get; init; }

    /// <summary>The environment name, when referencing by natural key.</summary>
    public string? Name { get; init; }

    /// <summary>True when this reference carries a complete natural key.</summary>
    public bool HasNaturalKey => Kind is not null && Subscription is not null && Name is not null;

    /// <summary>Reference an environment by its surrogate <c>env_id</c>.</summary>
    public static EnvRef ById(Guid envId) => new() { EnvId = envId };

    /// <summary>Reference an environment by its natural key <c>(Kind, Subscription, Name)</c>.</summary>
    public static EnvRef ByNaturalKey(EnvironmentKind kind, string subscription, string name) =>
        new() { Kind = kind, Subscription = subscription, Name = name };

    /// <summary>
    /// Parses an owner-facing environment reference: either a bare <c>env_id</c> (UUID), or the compact
    /// natural-key form <c>&lt;kind&gt;:&lt;subscription&gt;:&lt;name&gt;</c> (e.g. <c>spoke:&lt;sub&gt;:app5</c>,
    /// <c>fabric:&lt;sub&gt;:westus3</c>). The kind token is case-insensitive. Returns false for any other
    /// shape. Shared by the CLI <c>--env</c> option and the future MCP surface.
    /// </summary>
    public static bool TryParse(string? value, out EnvRef? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Guid.TryParse(value, out var envId))
        {
            reference = ById(envId);
            return true;
        }

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 &&
            Enum.TryParse<EnvironmentKind>(parts[0], ignoreCase: true, out var kind) &&
            parts[1].Length > 0 && parts[2].Length > 0)
        {
            reference = ByNaturalKey(kind, parts[1], parts[2]);
            return true;
        }

        return false;
    }
}
