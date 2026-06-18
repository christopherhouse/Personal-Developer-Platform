using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// The <c>run-name</c> ↔ <c>env_id</c> correlation scheme (research §4). A dispatched workflow sets
/// <c>run-name: pdp &lt;mode&gt; &lt;env_id&gt;</c>; since <c>workflow_dispatch</c> returns no run id and
/// the <c>workflow_run</c> payload does not echo inputs, the tracker resolves a run back to its
/// environment by parsing this title. Unparseable or foreign run names are ignored.
/// </summary>
public static class RunNameCorrelation
{
    private const string Prefix = "pdp";

    /// <summary>Builds the <c>run-name</c> the workflow sets for a given phase + environment.</summary>
    public static string Format(RunPhase mode, Guid envId) =>
        $"{Prefix} {ModeToken(mode)} {envId}";

    /// <summary>The lowercase <c>mode</c> token used in dispatch inputs and the run-name.</summary>
    public static string ModeToken(RunPhase mode) => mode switch
    {
        RunPhase.Plan => "plan",
        RunPhase.Apply => "apply",
        RunPhase.Destroy => "destroy",
        _ => mode.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Parses a <c>run-name</c> of the form <c>pdp &lt;mode&gt; &lt;env_id&gt;</c>. Returns false (and
    /// ignores the run) for any title that is not one of ours.
    /// </summary>
    public static bool TryParse(string? runName, out RunPhase mode, out Guid envId)
    {
        mode = default;
        envId = default;
        if (string.IsNullOrWhiteSpace(runName))
        {
            return false;
        }

        var parts = runName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParse(parts[2], out envId))
        {
            return false;
        }

        switch (parts[1])
        {
            case "plan": mode = RunPhase.Plan; return true;
            case "apply": mode = RunPhase.Apply; return true;
            case "destroy": mode = RunPhase.Destroy; return true;
            default: return false;
        }
    }
}
