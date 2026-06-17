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
}
