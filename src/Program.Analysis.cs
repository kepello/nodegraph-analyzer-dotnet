/// <fathom id="code-dotnet-analyzer-analysis" />
/// <fathom type="code" />
/// <fathom component="analysis" />
/// <fathom title=".NET Analyzer — Analysis Helpers" />
/// <fathom status="draft" />
/// <fathom traces-to="scenario-analysis" />

/// <summary>
/// Analysis-component helpers used by the .NET analyzer plugin.
///
/// The plugin as a whole has two component concerns:
///   (a) fulfilling the subprocess + NDJSON analyzer contract defined by
///       design-orchestrator — that work lives in Program.cs;
///   (b) producing the observation-contract fields that design-analysis
///       defines (declarable role/flavors/capabilities, physical metrics)
///       — that work lives here.
///
/// Keeping (b) in a separate file with component="analysis" makes each
/// side's boundary honest when the audit checks element content against
/// its file's declared component. Every function below derives from a
/// section of design-analysis or assertion-analysis and contributes to
/// the ObservationElement record the plugin emits.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <fathom derives-from="scenario-analysis#observation-contract/element-structure-rules" />
static class AnalysisHelpers
{
    // --- Physical-domain observations ---------------------------------

    /// <summary>
    /// Extract physical-domain observations for a C# declaration node.
    /// Returns null if no physical metrics apply to this element type.
    /// </summary>
    /// <fathom derives-from="assertion-analysis#observation-contract/physical-observations/physical-metric-rules" />
    public static object? ExtractPhysicalObservations(SyntaxNode node, SyntaxTree tree, string elementType)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var startLine = lineSpan.StartLinePosition.Line + 1; // 1-based
        var endLine = lineSpan.EndLinePosition.Line + 1;
        var linesOfCode = endLine - startLine + 1;

        // Count fully-blank lines within the span.
        var sourceText = tree.GetText();
        var blankLineCount = 0;
        for (var i = startLine; i <= endLine && i <= sourceText.Lines.Count; i++)
        {
            var line = sourceText.Lines[i - 1].ToString();
            if (string.IsNullOrWhiteSpace(line)) blankLineCount++;
        }

        // Comment lines: walk comment trivia inside the node's span.
        var commentLineCount = CountCommentLines(node);

        // commentDensity: derived, only when both inputs are measured and loc > 0.
        double? commentDensity = null;
        if (linesOfCode > 0)
        {
            commentDensity = (double)commentLineCount / linesOfCode;
        }

        // logicalLinesOfCode: applies only to elements with executable bodies.
        int? logicalLinesOfCode = null;
        if (HasExecutableBody(node, elementType))
        {
            logicalLinesOfCode = CountLogicalLines(node);
        }

        var physical = new Dictionary<string, object?>
        {
            ["linesOfCode"] = linesOfCode,
            ["blankLineCount"] = blankLineCount,
            ["commentLineCount"] = commentLineCount,
        };
        if (commentDensity.HasValue)
            physical["commentDensity"] = commentDensity.Value;
        if (logicalLinesOfCode.HasValue)
            physical["logicalLinesOfCode"] = logicalLinesOfCode.Value;

        return physical;
    }

    /// <summary>
    /// True if the node has an executable body whose statement count should
    /// be measured as logicalLinesOfCode. Applies to method, function,
    /// property with getter/setter body, and local functions.
    /// </summary>
    static bool HasExecutableBody(SyntaxNode node, string elementType)
    {
        if (elementType == "interface" || elementType == "enum" || elementType == "struct")
            return false;

        if (node is MethodDeclarationSyntax method)
            return method.Body != null || method.ExpressionBody != null;
        if (node is LocalFunctionStatementSyntax local)
            return local.Body != null || local.ExpressionBody != null;
        if (node is PropertyDeclarationSyntax prop)
        {
            if (prop.ExpressionBody != null) return true;
            if (prop.AccessorList == null) return false;
            foreach (var accessor in prop.AccessorList.Accessors)
            {
                if (accessor.Body != null || accessor.ExpressionBody != null) return true;
            }
            return false;
        }
        return false;
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
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                var text = trivia.ToFullString();
                count += text.Split('\n').Length;
            }
        }
        return count;
    }

    /// <summary>
    /// Count executable statement nodes in an element's body.
    /// </summary>
    static int CountLogicalLines(SyntaxNode node)
    {
        BlockSyntax? body = null;
        if (node is MethodDeclarationSyntax method)
            body = method.Body;
        else if (node is LocalFunctionStatementSyntax local)
            body = local.Body;
        else if (node is PropertyDeclarationSyntax prop && prop.AccessorList != null)
        {
            // Sum statements across all accessors.
            var count = 0;
            foreach (var accessor in prop.AccessorList.Accessors)
            {
                if (accessor.Body != null)
                {
                    count += accessor.Body.DescendantNodes().OfType<StatementSyntax>()
                        .Count(s => s is not BlockSyntax);
                }
            }
            return count;
        }

        if (body == null) return 0;
        return body.DescendantNodes().OfType<StatementSyntax>().Count(s => s is not BlockSyntax);
    }

    // --- Declarable Taxonomy Mapping ----------------------------------

    /// <summary>
    /// Compute DeclarableRole + DeclarableFlavors + CapabilityMask for a
    /// C# syntax node per reference/node-normalization.md §4. Returns null for
    /// nodes whose element type does not map to a declarable role.
    /// </summary>
    /// <fathom derives-from="scenario-analysis#observation-contract/element-structure-rules" />
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
                // Destructors map to method (same collapse as Swift deinit)
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
            case OperatorDeclarationSyntax op:
                role = "operator";
                // operatorKind: unary (1 param) or binary (2 params)
                typeKind = null; // clear; we'll set operatorKind below via a special flavor
                break;
            case ConversionOperatorDeclarationSyntax cod:
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

        // Role-specific flavor extraction for the new roles
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

        // Read modifier tokens on the declaration. Every member-level and
        // type-level declaration in C# exposes Modifiers as a SyntaxTokenList.
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
                // Access modifiers. `protected internal` and `private protected`
                // combine two tokens — the second-pass correction below merges
                // them into the canonical hyphenated form.
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
    /// Fixed per-role capability mask, mirroring the table in
    /// src/analysis/declarable.ts. Must stay in sync with the TS side.
    /// </summary>
    /// <fathom derives-from="scenario-analysis#observation-contract/element-structure-rules" />
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
