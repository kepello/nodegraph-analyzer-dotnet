/// <summary>
/// Naming + canonicalization helpers. Hosts the per-segment
/// <see cref="Canonicalize"/> primitive in a testable class so the
/// rules pinning collision behavior (notably underscore-preservation
/// per Fathom row <c>dotnet-canonical-name-underscore-collision</c>
/// 5.2.1) are unit-checkable from the test project.
/// </summary>

using System.Text;

static class NamingHelpers
{
    /// <summary>
    /// Canonicalize a single name segment. Lowercases ASCII letters,
    /// keeps digits and underscores verbatim, and collapses runs of
    /// other characters into single dash separators. Leading and
    /// trailing dashes are suppressed.
    ///
    /// Underscore is preserved (not collapsed to dash) so identifiers
    /// like <c>_field</c> stay distinct from <c>Field</c> — the
    /// dominant C# convention for private fields. Pre-5.2.1 the rule
    /// treated underscore as a separator, collapsing both names to
    /// <c>field</c> and producing canonical-name collisions on real
    /// C# codebases (e.g., PNP/Utilities `api/_environment` vs
    /// `api/Environment`).
    /// </summary>
    public static string Canonicalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var lower = raw.ToLowerInvariant();
        var sb = new StringBuilder();
        var lastDash = true;
        foreach (var c in lower)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var result = sb.ToString();
        if (result.EndsWith('-')) result = result.Substring(0, result.Length - 1);
        return result;
    }
}
