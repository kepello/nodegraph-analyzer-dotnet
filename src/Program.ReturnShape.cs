/// <summary>
/// F6 return-shape extraction — `returnKind` + `returnsField`
/// (Fathom row l1a-stereotype-derivation-precise 3.1.1.1, Stage 2,
/// .NET portion). Mirrors the TS analyzer's return-shape.ts.
///
///   returnKind   — void / boolean / primitive / reference / collection /
///                  unknown. Resolved via the SemanticModel when available
///                  (handles enums, aliases, generics), with a syntactic
///                  fallback for orphan files whose Compilation can't bind
///                  the type. `Task`/`ValueTask` (+ `Nullable<T>`) are
///                  unwrapped, so an `async` predicate returning
///                  `Task&lt;bool&gt;` reads as `boolean`.
///   returnsField — true iff a body-bearing get-shaped member returns an
///                  own-class field/property directly (`return _x;` /
///                  `return this.X;` / `=> _x`). Reuses the intra-class
///                  member-field index; matches the same `this.X` +
///                  bare-identifier forms the LCOM4 extractor uses.
///
/// Both are computed only for body-bearing callables. Setters / init /
/// event accessors / destructors report `void` + `returnsField=false`;
/// constructors report `reference` + `false` (the constructed instance).
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class ReturnShapeHelpers
{
    private static readonly HashSet<string> PrimitiveSpecialNames = new()
    {
        // SpecialType-backed value primitives + string (parity with the TS
        // categorizer, which treats string as primitive).
        "Char", "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32",
        "Int64", "UInt64", "Decimal", "Single", "Double", "String",
        "IntPtr", "UIntPtr", "DateTime",
    };

    private static readonly HashSet<string> SyntacticPrimitiveNames = new()
    {
        "bool", "byte", "sbyte", "char", "short", "ushort", "int", "uint",
        "long", "ulong", "float", "double", "decimal", "string", "nint",
        "nuint", "Boolean", "Byte", "SByte", "Char", "Int16", "UInt16",
        "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal",
        "String", "DateTime", "Guid", "TimeSpan", "DateTimeOffset",
    };

    private static readonly HashSet<string> SyntacticCollectionNames = new()
    {
        "List", "IList", "IEnumerable", "ICollection", "IReadOnlyList",
        "IReadOnlyCollection", "HashSet", "ISet", "Dictionary", "IDictionary",
        "IReadOnlyDictionary", "Queue", "Stack", "SortedList", "SortedSet",
        "LinkedList", "ImmutableList", "ImmutableArray", "ImmutableDictionary",
        "ImmutableHashSet", "Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory",
        "IQueryable", "IAsyncEnumerable", "IReadOnlyCollection",
    };

    /// <summary>
    /// Compute the F6 return-shape facets for a node. Returns
    /// <c>(null, null)</c> for non-callable / body-less declarations.
    /// <paramref name="fields"/> is the containing type's field/property
    /// name set (from the intra-class member index); pass an empty set
    /// when no containing type (then <c>returnsField</c> stays false).
    /// </summary>
    public static (string? ReturnKind, bool? ReturnsField) Extract(
        SyntaxNode node, SemanticModel? model, HashSet<string> fields)
    {
        var kind = ExtractReturnKind(node, model);
        if (kind == null) return (null, null);
        return (kind, ExtractReturnsField(node, fields));
    }

    private static string? ExtractReturnKind(SyntaxNode node, SemanticModel? model)
    {
        switch (node)
        {
            case ConstructorDeclarationSyntax:
                return "reference"; // the constructed instance
            case DestructorDeclarationSyntax:
                return "void";
            // Non-get accessors never yield a value.
            case AccessorDeclarationSyntax accNonGet
                when !accNonGet.IsKind(SyntaxKind.GetAccessorDeclaration):
                return "void";
        }

        var typeSyntax = GetReturnTypeSyntax(node);
        if (typeSyntax == null) return null;

        // `void` is unambiguous syntactically — cheapest + most reliable.
        if (typeSyntax is PredefinedTypeSyntax pre
            && pre.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return "void";
        }

        if (model != null)
        {
            try
            {
                var symbol = model.GetTypeInfo(typeSyntax).Type;
                if (symbol != null && symbol.TypeKind != TypeKind.Error)
                {
                    return CategorizeSymbol(symbol);
                }
            }
            catch
            {
                // Semantic miss (orphan file / shared compilation gap) —
                // fall through to the syntactic categorizer.
            }
        }
        return CategorizeSyntactic(typeSyntax.ToString());
    }

    private static TypeSyntax? GetReturnTypeSyntax(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ReturnType,
        LocalFunctionStatementSyntax lf => lf.ReturnType,
        OperatorDeclarationSyntax op => op.ReturnType,
        ConversionOperatorDeclarationSyntax co => co.Type,
        PropertyDeclarationSyntax p => p.Type,
        IndexerDeclarationSyntax ix => ix.Type,
        // get accessor → the containing property/indexer type.
        AccessorDeclarationSyntax acc when acc.IsKind(SyntaxKind.GetAccessorDeclaration)
            => acc.Ancestors().OfType<BasePropertyDeclarationSyntax>().FirstOrDefault()?.Type,
        _ => null,
    };

    private static string CategorizeSymbol(ITypeSymbol type)
    {
        var t = UnwrapNullable(UnwrapAwaitable(type));

        if (t.SpecialType == SpecialType.System_Void) return "void";
        if (t.SpecialType == SpecialType.System_Boolean) return "boolean";
        if (t.TypeKind == TypeKind.Enum) return "primitive";
        if (PrimitiveSpecialNames.Contains(t.Name) && IsSystemNamespace(t)) return "primitive";
        if (t.SpecialType == SpecialType.System_String) return "primitive";
        if (t.TypeKind == TypeKind.Array) return "collection";
        if (IsCollection(t)) return "collection";
        if (t.TypeKind is TypeKind.Class or TypeKind.Interface or TypeKind.Struct
            or TypeKind.Delegate or TypeKind.TypeParameter or TypeKind.Dynamic)
        {
            return "reference";
        }
        return "unknown";
    }

    /// <summary>Unwrap <c>Task</c>/<c>ValueTask</c> (and their generic forms)
    /// to the awaited type; non-generic Task/ValueTask → void.</summary>
    private static ITypeSymbol UnwrapAwaitable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && (named.Name == "Task" || named.Name == "ValueTask"))
        {
            if (named.TypeArguments.Length == 1) return named.TypeArguments[0];
            // bare Task / ValueTask — no value.
            return type.ContainingAssembly?.GetTypeByMetadataName("System.Void") as ITypeSymbol
                ?? type;
        }
        return type;
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && named.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            return named.TypeArguments[0];
        }
        return type;
    }

    private static bool IsSystemNamespace(ITypeSymbol t)
        => t.ContainingNamespace?.ToString() == "System";

    private static bool IsCollection(ITypeSymbol t)
    {
        if (t.SpecialType == SpecialType.System_String) return false;
        // The type itself or any interface it implements is IEnumerable.
        if (t is INamedTypeSymbol nt && nt.ConstructedFrom?.Name == "IEnumerable") return true;
        foreach (var iface in t.AllInterfaces)
        {
            if (iface.SpecialType == SpecialType.System_Collections_IEnumerable) return true;
            if (iface.Name == "IEnumerable") return true;
        }
        return false;
    }

    private static string CategorizeSyntactic(string typeText)
    {
        var text = typeText.Trim();
        // Strip trailing nullable annotation.
        if (text.EndsWith("?")) text = text[..^1].Trim();
        // Unwrap Task<...> / ValueTask<...>.
        foreach (var wrapper in new[] { "Task<", "ValueTask<", "System.Threading.Tasks.Task<" })
        {
            if (text.StartsWith(wrapper) && text.EndsWith(">"))
            {
                text = text[wrapper.Length..^1].Trim();
                break;
            }
        }
        if (text is "void") return "void";
        if (text is "Task" or "ValueTask" or "System.Threading.Tasks.Task") return "void";
        if (text is "bool" or "Boolean" or "System.Boolean") return "boolean";

        var simple = text.Split('.')[^1];
        var generic = simple.Split('<')[0];
        if (text.EndsWith("[]")) return "collection";
        if (SyntacticCollectionNames.Contains(generic)) return "collection";
        if (SyntacticPrimitiveNames.Contains(generic)) return "primitive";
        if (simple is "var" or "dynamic") return "unknown";
        // Custom class/interface/struct — can't tell enum from class
        // syntactically; default reference (semantic path catches enums).
        return "reference";
    }

    private static bool? ExtractReturnsField(SyntaxNode node, HashSet<string> fields)
    {
        switch (node)
        {
            case ConstructorDeclarationSyntax:
            case DestructorDeclarationSyntax:
                return false;
            case AccessorDeclarationSyntax accNonGet
                when !accNonGet.IsKind(SyntaxKind.GetAccessorDeclaration):
                return false;
        }
        if (fields.Count == 0) return false;
        foreach (var expr in GetReturnExpressions(node))
        {
            if (IsOwnFieldReference(expr, fields)) return true;
        }
        return false;
    }

    private static IEnumerable<ExpressionSyntax> GetReturnExpressions(SyntaxNode node)
    {
        var arrow = node switch
        {
            MethodDeclarationSyntax m => m.ExpressionBody,
            PropertyDeclarationSyntax p => p.ExpressionBody,
            IndexerDeclarationSyntax ix => ix.ExpressionBody,
            AccessorDeclarationSyntax acc => acc.ExpressionBody,
            LocalFunctionStatementSyntax lf => lf.ExpressionBody,
            OperatorDeclarationSyntax op => op.ExpressionBody,
            _ => null,
        };
        if (arrow != null)
        {
            yield return arrow.Expression;
            yield break;
        }

        var block = node switch
        {
            MethodDeclarationSyntax m => m.Body,
            AccessorDeclarationSyntax acc => acc.Body,
            LocalFunctionStatementSyntax lf => lf.Body,
            OperatorDeclarationSyntax op => op.Body,
            _ => null,
        };
        if (block == null) yield break;

        foreach (var ret in block.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (ret.Expression == null) continue;
            if (IsInsideNestedFunction(ret, block)) continue; // its return belongs to the inner fn
            yield return ret.Expression;
        }
    }

    private static bool IsInsideNestedFunction(SyntaxNode descendant, SyntaxNode body)
    {
        foreach (var ancestor in descendant.Ancestors())
        {
            if (ancestor == body) return false;
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True iff <paramref name="expr"/> is a direct own-field
    /// reference: <c>this.X</c> / <c>base.X</c> member access, or a bare
    /// identifier matching a member-field name (the C# `return _x;`
    /// convention). Mirrors the intra-class extractor's Case 1 + Case 3.</summary>
    private static bool IsOwnFieldReference(ExpressionSyntax expr, HashSet<string> fields)
    {
        var e = Unwrap(expr);
        return e switch
        {
            MemberAccessExpressionSyntax ma
                when (ma.Expression is ThisExpressionSyntax or BaseExpressionSyntax)
                    && fields.Contains(ma.Name.Identifier.Text) => true,
            IdentifierNameSyntax id when fields.Contains(id.Identifier.Text) => true,
            _ => false,
        };
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expr)
    {
        var e = expr;
        while (true)
        {
            switch (e)
            {
                case ParenthesizedExpressionSyntax paren:
                    e = paren.Expression;
                    break;
                case AwaitExpressionSyntax aw:
                    e = aw.Expression;
                    break;
                default:
                    return e;
            }
        }
    }
}
