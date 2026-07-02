using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// The parsed, validated in-memory form of <c>archetypes/catalog.json</c> — the repo-managed
/// declarative catalog (spec 008, R1; contracts/archetype-catalog.md). <see cref="Parse"/> enforces
/// the file's shape rules and verifies every <c>parameterSchema</c> is a valid JSON Schema draft
/// 2020-12 document <b>before</b> the sync applies anything; an invalid file throws
/// <see cref="CatalogDefinitionException"/> carrying every violation (the sync records the rejection
/// and keeps serving the previous projection).
/// </summary>
public sealed class CatalogDefinition
{
    /// <summary>The only supported <c>$schemaVersion</c> of the catalog file.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly Regex ArchetypeNameRegex = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    // SemVer 2.0 core + optional prerelease, with the mandatory `v` prefix (the git-tag suffix).
    private static readonly Regex VersionRegex = new(
        @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled);

    /// <summary>SHA-256 (hex) of the whole file as read — the sync's no-change short-circuit key.</summary>
    public required string ContentHash { get; init; }

    /// <summary>The declared archetypes, in file order.</summary>
    public required IReadOnlyList<CatalogArchetypeDefinition> Archetypes { get; init; }

    /// <summary>
    /// Parses and validates the catalog file. Violations are collected across the whole file (not
    /// fail-fast) so a rejection names everything wrong at once.
    /// </summary>
    /// <exception cref="CatalogDefinitionException">The file is not a valid catalog definition.</exception>
    public static CatalogDefinition Parse(string catalogJson)
    {
        var violations = new List<string>();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(catalogJson);
        }
        catch (JsonException ex)
        {
            throw new CatalogDefinitionException([$"not valid JSON: {ex.Message}"]);
        }

        if (root is not JsonObject rootObject)
        {
            throw new CatalogDefinitionException(["the root must be a JSON object"]);
        }

        if (rootObject["$schemaVersion"] is not JsonValue schemaVersion
            || !schemaVersion.TryGetValue<int>(out var version)
            || version != SupportedSchemaVersion)
        {
            violations.Add($"$schemaVersion must be the number {SupportedSchemaVersion}");
        }

        var archetypes = new List<CatalogArchetypeDefinition>();
        if (rootObject["archetypes"] is not JsonArray archetypeArray)
        {
            violations.Add("archetypes must be an array");
        }
        else
        {
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < archetypeArray.Count; i++)
            {
                var parsed = ParseArchetype(archetypeArray[i], $"archetypes[{i}]", seenNames, violations);
                if (parsed is not null)
                {
                    archetypes.Add(parsed);
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new CatalogDefinitionException(violations);
        }

        return new CatalogDefinition
        {
            ContentHash = Sha256Hex(catalogJson),
            Archetypes = archetypes,
        };
    }

    /// <summary>SHA-256 of a string's UTF-8 bytes, lowercase hex.</summary>
    public static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static CatalogArchetypeDefinition? ParseArchetype(
        JsonNode? node,
        string path,
        HashSet<string> seenNames,
        List<string> violations)
    {
        if (node is not JsonObject obj)
        {
            violations.Add($"{path}: must be an object");
            return null;
        }

        var before = violations.Count;

        var name = ReadString(obj, "name", path, violations);
        if (name is not null && !ArchetypeNameRegex.IsMatch(name))
        {
            violations.Add($"{path}.name: '{name}' must match ^[a-z0-9-]{{1,32}}$");
        }

        if (name is not null && !seenNames.Add(name))
        {
            violations.Add($"{path}.name: duplicate archetype '{name}'");
        }

        var description = ReadString(obj, "description", path, violations);

        var statusText = ReadString(obj, "status", path, violations);
        var status = statusText switch
        {
            "active" => ArchetypeStatus.Active,
            "retired" => ArchetypeStatus.Retired,
            null => (ArchetypeStatus?)null,
            _ => Report(violations, $"{path}.status: '{statusText}' must be 'active' or 'retired'"),
        };

        var versions = new List<CatalogVersionDefinition>();
        if (obj["versions"] is not JsonArray versionArray || versionArray.Count == 0)
        {
            violations.Add($"{path}.versions: must be a non-empty array");
        }
        else
        {
            var seenVersions = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < versionArray.Count; i++)
            {
                var parsed = ParseVersion(versionArray[i], $"{path}.versions[{i}]", seenVersions, violations);
                if (parsed is not null)
                {
                    versions.Add(parsed);
                }
            }
        }

        return violations.Count == before
            ? new CatalogArchetypeDefinition(name!, description!, status!.Value, versions)
            : null;
    }

    private static CatalogVersionDefinition? ParseVersion(
        JsonNode? node,
        string path,
        HashSet<string> seenVersions,
        List<string> violations)
    {
        if (node is not JsonObject obj)
        {
            violations.Add($"{path}: must be an object");
            return null;
        }

        var before = violations.Count;

        var version = ReadString(obj, "version", path, violations);
        if (version is not null && !VersionRegex.IsMatch(version))
        {
            violations.Add($"{path}.version: '{version}' must be SemVer with a 'v' prefix (e.g. v1.0.0)");
        }

        if (version is not null && !seenVersions.Add(version))
        {
            violations.Add($"{path}.version: duplicate version '{version}'");
        }

        var modulePath = ReadString(obj, "modulePath", path, violations);
        if (modulePath is not null
            && (modulePath.StartsWith('/') || modulePath.Contains('\\')
                || modulePath.Split('/').Any(segment => segment is "" or "." or "..")))
        {
            violations.Add($"{path}.modulePath: '{modulePath}' must be a clean repo-relative path");
        }

        string? canonicalSchema = null;
        if (obj["parameterSchema"] is not JsonObject schemaObject)
        {
            violations.Add($"{path}.parameterSchema: must be a JSON object");
        }
        else
        {
            canonicalSchema = CanonicalJson.Serialize(schemaObject);
            ValidateParameterSchema(canonicalSchema, $"{path}.parameterSchema", violations);
        }

        return violations.Count == before
            ? new CatalogVersionDefinition(
                version!,
                modulePath!,
                canonicalSchema!,
                Sha256Hex($"{modulePath}\n{canonicalSchema}"))
            : null;
    }

    /// <summary>
    /// A <c>parameterSchema</c> must both parse as a schema (JsonSchema.Net) and validate against the
    /// draft 2020-12 meta-schema — catching structurally-legal JSON that is not a legal schema (e.g.
    /// <c>"type": 42</c>) before it can ever reject a deploy at runtime.
    /// </summary>
    private static void ValidateParameterSchema(string canonicalSchema, string path, List<string> violations)
    {
        try
        {
            _ = JsonSchema.FromText(canonicalSchema);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            violations.Add($"{path}: not a parseable JSON Schema: {ex.Message}");
            return;
        }

        var results = MetaSchemas.Draft202012.Evaluate(
            JsonNode.Parse(canonicalSchema),
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
            var details = results.Details
                .Where(d => d.HasErrors)
                .SelectMany(d => d.Errors!.Values)
                .Distinct()
                .ToList();
            violations.Add($"{path}: not a valid draft 2020-12 schema: {string.Join("; ", details)}");
        }
    }

    private static string? ReadString(JsonObject obj, string property, string path, List<string> violations)
    {
        if (obj[property] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        violations.Add($"{path}.{property}: must be a non-empty string");
        return null;
    }

    private static ArchetypeStatus? Report(List<string> violations, string violation)
    {
        violations.Add(violation);
        return null;
    }
}

/// <summary>One archetype entry as declared in the catalog file.</summary>
/// <param name="Name">Catalog identity (<c>^[a-z0-9-]{1,32}$</c>).</param>
/// <param name="Description">Human summary.</param>
/// <param name="Status">Active or retired (FR-004).</param>
/// <param name="Versions">The declared versions (append-only across file revisions — R2).</param>
public sealed record CatalogArchetypeDefinition(
    string Name,
    string Description,
    ArchetypeStatus Status,
    IReadOnlyList<CatalogVersionDefinition> Versions);

/// <summary>One archetype version as declared in the catalog file.</summary>
/// <param name="Version">SemVer with <c>v</c> prefix — the git-tag suffix.</param>
/// <param name="ModulePath">Repo-relative OpenTofu module dir.</param>
/// <param name="ParameterSchemaJson">The version's parameter schema, in canonical JSON form.</param>
/// <param name="ContentHash">SHA-256 over <c>(modulePath, canonical schema)</c> — the immutability fingerprint (R2).</param>
public sealed record CatalogVersionDefinition(
    string Version,
    string ModulePath,
    string ParameterSchemaJson,
    string ContentHash);

/// <summary>
/// The catalog file failed shape or schema validation. Carries every violation so one rejection
/// names everything wrong with the file (surfaced in the <c>catalog_syncs</c> audit row).
/// </summary>
public sealed class CatalogDefinitionException(IReadOnlyList<string> violations)
    : Exception($"archetypes/catalog.json is invalid: {string.Join("; ", violations)}")
{
    /// <summary>Every violation found in the file.</summary>
    public IReadOnlyList<string> Violations { get; } = violations;
}
