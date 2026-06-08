using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Group A8 — attribute / annotation emission (Fathom row
/// dotnet-l0-attribute-emission 5.0.79; H1 of the 2026-06-07 context-
/// sufficiency audit). Extracts the attributes applied to a declaration into
/// the wire-protocol <c>annotations</c> facet so the orchestrator can answer
/// auth / ORM-mapping / service-contract / generated-provenance / test /
/// serialization questions WITHOUT re-reading source.
///
/// Arg typing follows A8 Level-2 primitives: string / number / boolean /
/// identifier (dotted member-access permitted) / expression (escape hatch).
/// The <c>expression</c> fallback is observable per no-silent-degradation: it
/// is paired with a J1 limitation (<c>kind: "fallback-annotation-arg"</c>) so
/// the loss of precision is never silent.
///
/// SCOPED OUT of H1 (tracked follow-on): the <c>type-ref</c> arg kind and its
/// companion <c>references</c> edge (<c>subtype: "annotation-arg"</c>) for
/// <c>typeof(T)</c> args — those currently fall to the <c>expression</c>
/// fallback (honest + limitation-flagged), not silently dropped.
/// </summary>
internal static class AnnotationHelpers
{
    /// <summary>A fallback arg the analyzer could not type as an A8 primitive;
    /// surfaced as a J1 limitation by the caller.</summary>
    internal sealed record AnnotationFallback(int StartLine, int EndLine, string Source);

    /// <summary>Result of extracting a declaration's attributes: the
    /// wire-ready annotation dicts + any expression-fallback args to flag.</summary>
    internal sealed record AnnotationExtractResult(
        List<Dictionary<string, object?>> Annotations,
        List<AnnotationFallback> Fallbacks);

    /// <summary>Extract the A8 annotations applied to <paramref name="node"/>.
    /// Returns null when the declaration carries no attributes.</summary>
    internal static AnnotationExtractResult? Extract(SyntaxNode node, SemanticModel model)
    {
        var lists = GetAttributeLists(node);
        if (lists.Count == 0) return null;

        var annotations = new List<Dictionary<string, object?>>();
        var fallbacks = new List<AnnotationFallback>();

        foreach (var list in lists)
        {
            foreach (var attr in list.Attributes)
            {
                var anno = new Dictionary<string, object?>
                {
                    ["name"] = SimpleName(attr.Name),
                };
                var qualified = QualifiedName(attr, model);
                if (qualified != null) anno["qualifiedName"] = qualified;

                var args = new List<object>();
                Dictionary<string, object?>? namedArgs = null;
                if (attr.ArgumentList != null)
                {
                    foreach (var arg in attr.ArgumentList.Arguments)
                    {
                        var typed = ClassifyArg(arg.Expression, fallbacks);
                        // `Name = value` → named; positional (incl. `name:`) → args.
                        if (arg.NameEquals != null)
                        {
                            (namedArgs ??= new Dictionary<string, object?>())
                                [arg.NameEquals.Name.Identifier.Text] = typed;
                        }
                        else
                        {
                            args.Add(typed);
                        }
                    }
                }
                anno["args"] = args;
                if (namedArgs != null) anno["namedArgs"] = namedArgs;
                annotations.Add(anno);
            }
        }

        return new AnnotationExtractResult(annotations, fallbacks);
    }

    /// <summary>Attribute lists for the declaration kinds that can carry them.
    /// MemberDeclarationSyntax covers types / methods / properties / fields /
    /// events / constructors / operators / enum-members; accessors and
    /// parameters carry their own lists.</summary>
    private static SyntaxList<AttributeListSyntax> GetAttributeLists(SyntaxNode node) => node switch
    {
        MemberDeclarationSyntax m => m.AttributeLists,
        AccessorDeclarationSyntax a => a.AttributeLists,
        ParameterSyntax p => p.AttributeLists,
        TypeParameterSyntax tp => tp.AttributeLists,
        _ => default,
    };

    /// <summary>Short name as written in source (last segment, no namespace),
    /// e.g. `Authorize`, `Table`, `WebMethod`. The C# convention of dropping
    /// the `Attribute` suffix is preserved as-written (not synthesized).</summary>
    private static string SimpleName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => name.ToString(),
    };

    /// <summary>Fully-qualified attribute type name when resolvable
    /// (e.g. `System.ObsoleteAttribute`); null when the symbol doesn't resolve
    /// (references-free / external-unresolved) — never a guessed value.</summary>
    private static string? QualifiedName(AttributeSyntax attr, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(attr).Symbol
            ?? model.GetSymbolInfo(attr).CandidateSymbols.FirstOrDefault();
        var type = (symbol as IMethodSymbol)?.ContainingType ?? symbol?.ContainingType;
        if (type == null) return null;
        var fq = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
            .WithGenericsOptions(SymbolDisplayGenericsOptions.None));
        return string.IsNullOrEmpty(fq) ? null : fq;
    }

    /// <summary>Classify an attribute-arg expression into an A8 Level-2 typed
    /// primitive. Anything that isn't a literal / member-access falls to the
    /// `expression` escape hatch AND records a fallback (→ J1 limitation), so
    /// the loss of precision is observable (no-silent-degradation). The
    /// `type-ref` kind (`typeof(T)` + its companion `references` edge) is a
    /// tracked follow-on; for now it takes the expression fallback path.</summary>
    private static Dictionary<string, object?> ClassifyArg(
        ExpressionSyntax expr, List<AnnotationFallback> fallbacks)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax lit:
                if (lit.IsKind(SyntaxKind.StringLiteralExpression))
                    return new() { ["kind"] = "string", ["value"] = lit.Token.ValueText };
                if (lit.IsKind(SyntaxKind.NumericLiteralExpression))
                    return new() { ["kind"] = "number", ["value"] = lit.Token.Value };
                if (lit.IsKind(SyntaxKind.TrueLiteralExpression))
                    return new() { ["kind"] = "boolean", ["value"] = true };
                if (lit.IsKind(SyntaxKind.FalseLiteralExpression))
                    return new() { ["kind"] = "boolean", ["value"] = false };
                break;

            // Negative numeric literal: `-1` parses as unary-minus over a
            // numeric literal. Keep it a typed number.
            case PrefixUnaryExpressionSyntax u
                when u.IsKind(SyntaxKind.UnaryMinusExpression)
                    && u.Operand is LiteralExpressionSyntax nlit
                    && nlit.IsKind(SyntaxKind.NumericLiteralExpression):
                return new() { ["kind"] = "number", ["value"] = $"-{nlit.Token.ValueText}" };

            // Identifier / dotted member access (`EditorBrowsableState.Never`).
            case IdentifierNameSyntax:
            case MemberAccessExpressionSyntax:
                return new() { ["kind"] = "identifier", ["value"] = expr.ToString() };
        }

        // Escape hatch — record the fallback so the caller emits a J1 limitation.
        var span = expr.GetLocation().GetLineSpan();
        fallbacks.Add(new AnnotationFallback(
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            expr.ToString()));
        return new() { ["kind"] = "expression", ["source"] = expr.ToString() };
    }
}
