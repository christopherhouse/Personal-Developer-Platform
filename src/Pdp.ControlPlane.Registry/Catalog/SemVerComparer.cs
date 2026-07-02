namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// Orders archetype version strings (<c>v1.2.3</c>, optionally <c>-prerelease</c>) by SemVer 2.0
/// precedence — "newest active version" resolution must be numeric (<c>v1.10.0</c> &gt;
/// <c>v1.9.0</c>), which text ordering gets wrong. Inputs are already shape-validated by
/// <see cref="CatalogDefinition"/>; unparseable strings sort first so they can never win resolution.
/// </summary>
public sealed class SemVerComparer : IComparer<string>
{
    /// <summary>The shared instance.</summary>
    public static SemVerComparer Instance { get; } = new();

    private SemVerComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        var (xCore, xPre, xOk) = Parse(x);
        var (yCore, yPre, yOk) = Parse(y);

        if (!xOk || !yOk)
        {
            return xOk.CompareTo(yOk); // unparseable sorts lowest
        }

        for (var i = 0; i < 3; i++)
        {
            var cmp = xCore[i].CompareTo(yCore[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return ComparePrerelease(xPre, yPre);
    }

    private static (int[] Core, string? Prerelease, bool Ok) Parse(string? version)
    {
        if (string.IsNullOrEmpty(version) || version[0] != 'v')
        {
            return ([], null, false);
        }

        var text = version[1..];
        var dash = text.IndexOf('-');
        string? prerelease = null;
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return ([], null, false);
        }

        var core = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], out core[i]))
            {
                return ([], null, false);
            }
        }

        return (core, prerelease, true);
    }

    // SemVer 2.0 §11: a prerelease sorts before its release; identifiers compare dot-by-dot,
    // numeric identifiers numerically (and lower than alphanumeric), the shorter set first on ties.
    private static int ComparePrerelease(string? x, string? y)
    {
        if (x is null && y is null)
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? 1 : -1;
        }

        var xIds = x.Split('.');
        var yIds = y.Split('.');
        for (var i = 0; i < Math.Min(xIds.Length, yIds.Length); i++)
        {
            var xNumeric = int.TryParse(xIds[i], out var xNum);
            var yNumeric = int.TryParse(yIds[i], out var yNum);
            var cmp = (xNumeric, yNumeric) switch
            {
                (true, true) => xNum.CompareTo(yNum),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(xIds[i], yIds[i]),
            };
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return xIds.Length.CompareTo(yIds.Length);
    }
}
