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
