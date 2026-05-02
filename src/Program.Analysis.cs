/// <summary>
/// Analysis-component helpers used by the .NET analyzer plugin.
///
/// The plugin produces the structural and observation fields the
/// 0.4.0 `@kepello/nodegraph-analysis` wire contract defines:
///   - AnalyzerObservation.size — per-element size measurements
///   - role / flavors / capabilities — declarable taxonomy metadata
///
/// Per the 0.4.0 contract:
///   - `linesOfCode` is the effective LOC (span − blanks − comments)
///   - `physicalLinesOfCode` is the geometric span
///   - `commentDensity` divides commentLineCount by physicalLinesOfCode
///
/// Statement-level body measurements (the old `logicalLinesOfCode`)
/// move into per-method scalars in slice 3 — not emitted today.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class AnalysisHelpers
{
    // --- Size observations ---------------------------------------------

    /// <summary>
    /// Extract size-domain measurements for a C# declaration node.
    /// Always returns a populated `size` sub-record — every element has
    /// a span, even if the body is empty.
    /// </summary>
    public static object ExtractObservation(SyntaxNode node, SyntaxTree tree)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var startLine = lineSpan.StartLinePosition.Line + 1; // 1-based
        var endLine = lineSpan.EndLinePosition.Line + 1;
        var physicalLinesOfCode = endLine - startLine + 1;

        var sourceText = tree.GetText();
        var blankLineCount = 0;
        for (var i = startLine; i <= endLine && i <= sourceText.Lines.Count; i++)
        {
            var line = sourceText.Lines[i - 1].ToString();
            if (string.IsNullOrWhiteSpace(line)) blankLineCount++;
        }

        var commentLineCount = CountCommentLines(node);
        // Effective LOC: span minus blanks minus comments. Bounded at 0
        // for degenerate cases where the comment counter overcounts (the
        // span-bounded blank counter never undercounts, but the comment
        // counter walks descendant trivia and may include comments that
        // span beyond the geometric line range).
        var linesOfCode = Math.Max(0, physicalLinesOfCode - blankLineCount - commentLineCount);

        double? commentDensity = null;
        if (physicalLinesOfCode > 0)
        {
            commentDensity = (double)commentLineCount / physicalLinesOfCode;
        }

        var size = new Dictionary<string, object?>
        {
            ["linesOfCode"] = linesOfCode,
            ["physicalLinesOfCode"] = physicalLinesOfCode,
            ["blankLineCount"] = blankLineCount,
            ["commentLineCount"] = commentLineCount,
        };
        if (commentDensity.HasValue)
            size["commentDensity"] = commentDensity.Value;

        return new Dictionary<string, object?> { ["size"] = size };
    }

    /// <summary>
    /// Count comment-trivia lines within a node's source span.
    /// </summary>
    static int CountCommentLines(SyntaxNode node)
    {
        var count = 0;
        foreach (var trivia in node.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                var text = trivia.ToFullString();
                count += text.Split('\n').Length;
            }
        }
        return count;
    }

    // --- Declarable Taxonomy Mapping ----------------------------------

    /// <summary>
    /// Compute role + flavors + capabilities for a C# syntax node.
    /// Returns null for nodes whose element type does not map to a
    /// declarable role.
    ///
    /// Mirrors the role/flavor vocabulary the TS analyzer uses so a
    /// consumer's bridge sees structurally identical metadata across
    /// the two languages.
    /// </summary>
    public static (string role, Dictionary<string, object?> flavors, Dictionary<string, bool> capabilities)?
        MapToDeclarable(SyntaxNode node)
    {
        string? role = null;
        string? typeKind = null;

        switch (node)
        {
            case ClassDeclarationSyntax:
                role = "type";
                typeKind = "class";
                break;
            case InterfaceDeclarationSyntax:
                role = "type";
                typeKind = "interface";
                break;
            case StructDeclarationSyntax:
                role = "type";
                typeKind = "struct";
                break;
            case EnumDeclarationSyntax:
                role = "type";
                typeKind = "enum";
                break;
            case MethodDeclarationSyntax:
                role = "method";
                break;
            case PropertyDeclarationSyntax:
                role = "property";
                break;
            case LocalFunctionStatementSyntax:
                role = "method";
                break;
            case ConstructorDeclarationSyntax:
                role = "constructor";
                break;
            case DestructorDeclarationSyntax:
                role = "method";
                break;
            case EnumMemberDeclarationSyntax:
                role = "enumMember";
                break;
            case FieldDeclarationSyntax:
                role = "field";
                break;
            case EventDeclarationSyntax:
            case EventFieldDeclarationSyntax:
                role = "event";
                break;
            case IndexerDeclarationSyntax:
                role = "indexer";
                break;
            case OperatorDeclarationSyntax:
            case ConversionOperatorDeclarationSyntax:
                role = "operator";
                break;
            case ParameterSyntax:
                role = "parameter";
                break;
            case TypeParameterSyntax:
                role = "typeParameter";
                break;
            case AccessorDeclarationSyntax:
                role = "accessor";
                break;
            case AttributeSyntax:
                role = "annotation";
                break;
        }

        if (role == null) return null;

        var flavors = new Dictionary<string, object?>();
        if (typeKind != null) flavors["typeKind"] = typeKind;

        if (node is OperatorDeclarationSyntax opDecl)
        {
            flavors["operatorKind"] = opDecl.ParameterList.Parameters.Count == 1 ? "unary" : "binary";
        }
        else if (node is ConversionOperatorDeclarationSyntax convDecl)
        {
            flavors["operatorKind"] = convDecl.ImplicitOrExplicitKeyword.Text == "implicit"
                ? "conversion-implicit"
                : "conversion-explicit";
        }
        else if (node is IndexerDeclarationSyntax)
        {
            flavors["indexerKind"] = "cs-callable";
        }
        else if (node is ParameterSyntax paramSyntax)
        {
            foreach (var pm in paramSyntax.Modifiers)
            {
                switch (pm.Text)
                {
                    case "ref": flavors["parameterModifier"] = "ref"; break;
                    case "out": flavors["parameterModifier"] = "out"; break;
                    case "in": flavors["parameterModifier"] = "in"; break;
                    case "params": flavors["parameterModifier"] = "params"; break;
                }
            }
            if (paramSyntax.Default != null) flavors["hasDefault"] = true;
        }

        var modifiers = node switch
        {
            MemberDeclarationSyntax m => m.Modifiers,
            LocalFunctionStatementSyntax l => l.Modifiers,
            _ => default
        };

        foreach (var m in modifiers)
        {
            switch (m.Text)
            {
                case "abstract": flavors["abstract"] = true; break;
                case "static": flavors["static"] = true; break;
                case "sealed": flavors["sealed"] = true; break;
                case "virtual": flavors["virtual"] = true; break;
                case "override": flavors["override"] = true; break;
                case "readonly": flavors["readonly"] = true; break;
                case "async": flavors["async"] = true; break;
                case "partial": flavors["partial"] = true; break;
                case "public": flavors["access"] = "public"; break;
                case "private":
                    flavors["access"] = flavors.TryGetValue("access", out var pa) && (string?)pa == "protected"
                        ? "private-protected"
                        : "private";
                    break;
                case "protected":
                    flavors["access"] = flavors.TryGetValue("access", out var pra) && (string?)pra == "internal"
                        ? "protected-internal"
                        : "protected";
                    break;
                case "internal":
                    flavors["access"] = flavors.TryGetValue("access", out var ia) && (string?)ia == "protected"
                        ? "protected-internal"
                        : "internal";
                    break;
            }
        }

        return (role, flavors, CapabilitiesForRole(role));
    }

    /// <summary>
    /// Fixed per-role capability mask. Mirrors the same table TS / HTML
    /// / CSS analyzers use; a consumer that maps role → capabilities
    /// gets identical results across languages.
    /// </summary>
    static Dictionary<string, bool> CapabilitiesForRole(string role) => role switch
    {
        "namespace" or "type" or "section" =>
            new Dictionary<string, bool> { ["scope"] = true, ["behavior"] = false, ["binding"] = false },
        "method" or "constructor" or "function" or "accessor" or "operator" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = true, ["binding"] = false },
        "property" or "event" or "indexer" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = true, ["binding"] = true },
        "field" or "variable" or "parameter" or "enumMember" or "typeParameter" or "frontmatterField" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = false, ["binding"] = true },
        "ambient" or "annotation" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = false, ["binding"] = false },
        _ =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = false, ["binding"] = false }
    };
}
