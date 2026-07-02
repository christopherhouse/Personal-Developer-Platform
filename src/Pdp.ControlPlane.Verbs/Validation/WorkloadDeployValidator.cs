using FluentValidation;
using Pdp.ControlPlane.Inventory.Classification;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Validation;

/// <summary>
/// Shape validation for a <see cref="WorkloadDeployRequest"/> — the first step of the spec-008
/// validation order (R3: shape → catalog → schema → spoke → intent). Name and <c>pdp-env</c> rules
/// delegate to <see cref="TagSchema"/> so the verb layer and the inventory classifier can never
/// disagree about what a valid workload/environment name is. The archetype's <b>parameters</b> are
/// deliberately not validated here — that is the per-archetype JSON schema's job
/// (<see cref="ParameterSchemaEvaluator"/>), evaluated against catalog data the compile-time rules
/// cannot know.
/// </summary>
public sealed class WorkloadDeployValidator : AbstractValidator<WorkloadDeployRequest>
{
    /// <summary>The archetype-name pattern (the catalog identity rule, contracts/archetype-catalog.md).</summary>
    public const string ArchetypeNamePattern = "^[a-z0-9-]{1,32}$";

    /// <summary>Configures the rules.</summary>
    public WorkloadDeployValidator()
    {
        RuleFor(r => r.Subscription)
            .NotEmpty()
            .Must(s => Guid.TryParse(s, out _))
            .WithMessage("'Subscription' must be an Azure subscription GUID.");

        RuleFor(r => r.SpokeName)
            .NotEmpty()
            .Must(TagSchema.IsValidResourceName)
            .WithMessage("'SpokeName' must be 1–24 chars of [a-z0-9-] (the pdp-spoke tag domain).");

        RuleFor(r => r.WorkloadName)
            .NotEmpty()
            .Must(TagSchema.IsValidResourceName)
            .WithMessage("'WorkloadName' must be 1–24 chars of [a-z0-9-] (the pdp-workload tag domain).");

        RuleFor(r => r.Archetype)
            .NotEmpty()
            .Matches(ArchetypeNamePattern)
            .WithMessage("'Archetype' must be 1–32 chars of [a-z0-9-] (the catalog naming rule).");

        RuleFor(r => r.Environment)
            .NotEmpty()
            .Must(TagSchema.IsValidEnvName)
            .WithMessage("'Environment' must be 1–16 chars of [a-z0-9-] (the pdp-env tag domain).");

        RuleFor(r => r.Parameters)
            .NotNull()
            .WithMessage("'Parameters' must be a JSON object (use {} for an archetype with all-default parameters).");
    }
}
