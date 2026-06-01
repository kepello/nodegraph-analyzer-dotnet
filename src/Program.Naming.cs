/// <summary>
/// Naming + canonicalization helpers. Hosts the per-segment
/// <see cref="Canonicalize"/> primitive in a testable class so the
/// rules pinning collision behavior (notably underscore-preservation
/// per Fathom row <c>dotnet-canonical-name-underscore-collision</c>
/// 5.2.1) are unit-checkable from the test project.
/// </summary>

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class NamingHelpers
{
    /// <summary>
    /// Build the parameter-type signature segment appended to a method /
    /// constructor / indexer raw name before canonicalization, e.g.
    /// <c>(object,CameraEventArgs)</c>. Zero parameters → <c>()</c>; a node
    /// with no parameter list → empty string. Param types drop generic spaces
    /// and replace nested <c>/</c> so the signature survives
    /// <see cref="Canonicalize"/> as a single dash-joined run
    /// (<c>method-object-cameraeventargs</c>).
    ///
    /// Single source of truth shared by the element natural-key construction
    /// (<c>Program.cs</c>) and the intra-class <c>callsMethod</c> target
    /// resolution (<c>Program.IntraClass.cs</c>) so an edge's target key and
    /// the declaration's element key are byte-identical (Fathom row
    /// <c>dotnet-l0-internal-call-resolution</c> 5.0.68.1).
    /// </summary>
    /// <summary>
    /// Build a case-folded lookup from the analyzer's discovered (on-disk-case)
    /// file paths: lowercased path → the real discovered path. Ties keep the
    /// first seen. Used to normalize a resolved declaration path (whose case
    /// follows the .csproj `<Compile Include>`, not disk) back to the on-disk
    /// case so a cross-file edge's targetRef string-matches the callee's
    /// element key (Fathom row dotnet-l0-partial-class-dispose-binding 5.0.68.1.1).
    /// </summary>
    public static Dictionary<string, string> BuildCanonicalPathMap(IEnumerable<string> discoveredFiles)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in discoveredFiles)
        {
            var key = f.ToLowerInvariant();
            if (!map.ContainsKey(key)) map[key] = f;
        }
        return map;
    }

    /// <summary>
    /// Normalize <paramref name="path"/> to the discovered on-disk case via
    /// <paramref name="canonByLower"/> (built by <see cref="BuildCanonicalPathMap"/>).
    /// Returns the path unchanged when it isn't a known discovered file (e.g.
    /// an external/library declaration the caller won't emit an edge to anyway).
    /// </summary>
    public static string CanonicalizeFilePathCase(string path, IReadOnlyDictionary<string, string> canonByLower)
        => canonByLower.TryGetValue(path.ToLowerInvariant(), out var real) ? real : path;

    public static string GetParamSignature(SyntaxNode node)
    {
        SeparatedSyntaxList<ParameterSyntax>? parameters = node switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
            LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters,
            IndexerDeclarationSyntax idx => idx.ParameterList.Parameters,
            _ => null
        };
        if (parameters == null) return string.Empty;
        if (parameters.Value.Count == 0) return "()";
        var types = parameters.Value.Select(p =>
            (p.Type?.ToString() ?? "")
                .Replace("/", "-")
                .Replace(" ", ""));
        return "(" + string.Join(",", types) + ")";
    }

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
