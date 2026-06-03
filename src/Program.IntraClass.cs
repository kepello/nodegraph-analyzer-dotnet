/// <summary>
/// Intra-class edge extraction for LCOM4 and friends (slice 3,
/// .NET portion). Mirrors the TS analyzer's intra-class-edges.ts.
///
/// The 0.4.0 wire contract added two edge types feeding the engine's
/// cohesion derivation (slice 6):
///
///   accessesField  — a method (source) reads or writes a field
///                    declared on the same class. One edge per
///                    (method, field) pair.
///   callsMethod    — a method (source) invokes another method
///                    declared on the same class. One edge per
///                    (caller, callee) pair.
///
/// Both are scoped to the source method's containing type — cross-
/// type references continue to use the existing `references` /
/// `calls` types so the cohesion graph stays disconnected from the
/// coupling graph (architecture.md §5).
///
/// Detection is *syntactic*. We don't run the SemanticModel for
/// member resolution; we look at three kinds of references:
///
///   1. `this.X` / `base.X` member access expressions — unambiguous,
///      always counted when X matches a class member name.
///   2. Invocation expressions whose callee is a bare IdentifierName
///      that matches a method in the containing type — e.g.,
///      `Reset()` calling `this.Reset()`. C# omits `this.` by
///      convention so this is the dominant pattern.
///   3. IdentifierName references inside the body that match a
///      field/property name — bare references to instance state.
///
/// Cases 2 and 3 suppress when the bare identifier name is shadowed
/// by a locally-introduced binding (parameter, local variable,
/// foreach iteration variable, catch variable, or pattern
/// designation) anywhere in the method body. This is conservative:
/// it doesn't model block scopes precisely, so a class member whose
/// name collides with an unrelated local in any branch is dropped
/// from the cohesion graph. Under-counting is the chosen bias for
/// LCOM4 (false-positives previously over-reported cohesion); the
/// `this.X` form is unambiguous and continues to fire under Case 1.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class IntraClassHelpers
{
    /// <summary>
    /// Per-type member index used by the intra-class edge extractor
    /// (LCOM4 input). <c>Fields</c> includes fields and properties
    /// (properties hold state for the cohesion graph);
    /// <c>Methods</c> includes regular methods, accessors, and
    /// constructors. Members shadowed by parameters or locals are
    /// over-counted by design — see work-tracking gap
    /// <c>analyzers-intraclass-shadow</c> for the deferred precision
    /// improvement.
    /// </summary>
    public sealed record ClassMemberIndex(
        HashSet<string> Fields,
        HashSet<string> Methods,
        IReadOnlyDictionary<string, List<MethodOverload>> MethodOverloads);

    /// <summary>One method declaration's arity + canonical-ready parameter
    /// signature (e.g. <c>(int,string)</c>), used to resolve a
    /// <c>callsMethod</c> target to the exact element natural key.</summary>
    public sealed record MethodOverload(int Arity, string Signature);

    /// <summary>A same-class call to an overloaded method name that arity
    /// alone cannot disambiguate (2+ overloads share the called arity, or a
    /// method-group reference to an overloaded name). Surfaced as a
    /// <c>csharp-ambiguous-overload</c> limitation rather than a guessed edge
    /// (Trade-off <c>dotnet-callsmethod-overload-ambiguity</c> 2.2.17).</summary>
    public sealed record AmbiguousCall(string MethodName, IReadOnlyList<int> Arities, int OverloadCount);

    /// <summary>Result of <see cref="ExtractEdges"/>: the resolved intra-class
    /// edges plus any same-arity-overload ambiguities the caller turns into
    /// structured limitations.</summary>
    public sealed record IntraClassResult(
        List<(string Type, string? Subtype, string Target)> Edges,
        List<AmbiguousCall> AmbiguousCalls);

    /// <summary>
    /// Build the member index for a single type declaration. Includes fields,
    /// properties (treated as fields for the cohesion graph since they
    /// hold state), methods, and accessors. Convenience wrapper over the
    /// partial-aware <see cref="BuildIndex(IEnumerable{TypeDeclarationSyntax})"/>;
    /// callers that have the type's full set of partial declarations should use
    /// the union overload (Fathom row dotnet-l0-partial-class-field-index).
    /// </summary>
    public static ClassMemberIndex BuildIndex(TypeDeclarationSyntax typeDecl)
        => BuildIndex(new[] { typeDecl });

    /// <summary>
    /// Build the member index unioned across ALL partial declarations of a
    /// type. A C# type may be split across files (the canonical WinForms case:
    /// controls live in <c>Form.Designer.cs</c>, handlers in <c>Form.cs</c>);
    /// indexing only the declaration in the file being walked made the other
    /// partials' fields/methods invisible, so <c>accessesField</c> /
    /// <c>callsMethod</c> edges + the <c>returnsField</c> fact to those members
    /// were silently dropped (Fathom row dotnet-l0-partial-class-field-index).
    /// The caller passes every partial declaration of the type (resolved via
    /// the type symbol's <c>DeclaringSyntaxReferences</c> over the shared
    /// multi-file compilation).
    /// </summary>
    public static ClassMemberIndex BuildIndex(IEnumerable<TypeDeclarationSyntax> decls)
    {
        var fields = new HashSet<string>();
        var methods = new HashSet<string>();
        var overloads = new Dictionary<string, List<MethodOverload>>();

        foreach (var typeDecl in decls)
        {
            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax fd:
                        foreach (var v in fd.Declaration.Variables)
                        {
                            fields.Add(v.Identifier.Text);
                        }
                        break;
                    case PropertyDeclarationSyntax pd:
                        fields.Add(pd.Identifier.Text);
                        break;
                    case EventFieldDeclarationSyntax ed:
                        foreach (var v in ed.Declaration.Variables)
                        {
                            fields.Add(v.Identifier.Text);
                        }
                        break;
                    case EventDeclarationSyntax ed2:
                        fields.Add(ed2.Identifier.Text);
                        break;
                    case MethodDeclarationSyntax md:
                        methods.Add(md.Identifier.Text);
                        if (!overloads.TryGetValue(md.Identifier.Text, out var list))
                        {
                            list = new List<MethodOverload>();
                            overloads[md.Identifier.Text] = list;
                        }
                        list.Add(new MethodOverload(
                            md.ParameterList.Parameters.Count,
                            NamingHelpers.GetParamSignature(md)));
                        break;
                    case ConstructorDeclarationSyntax:
                    case DestructorDeclarationSyntax:
                        // Not referenced by name from inside the type.
                        break;
                }
            }
        }

        return new ClassMemberIndex(fields, methods, overloads);
    }

    /// <summary>
    /// Walk a method/property/accessor body and return the intra-class
    /// edges it implies plus any unresolvable-by-arity overload ambiguities.
    /// <c>callsMethod</c> targets carry the resolved parameter signature
    /// (<c>method(int,string)</c>) so the emitted natural key matches the
    /// callee's element key — fields/properties stay bare (no signature).
    /// The caller canonicalizes + class-qualifies the target before emitting
    /// (Fathom row <c>dotnet-l0-internal-call-resolution</c> 5.0.68.1).
    /// </summary>
    public static IntraClassResult ExtractEdges(
        SyntaxNode body,
        ClassMemberIndex members)
    {
        // Per language-conformance B1 accessesField subtype core (Stage 3a,
        // 2026-05-16): each access is classified as `read`, `write`, or
        // `readwrite` based on the surrounding syntactic context.
        var fieldAccess = new Dictionary<string, (bool Read, bool Write)>();
        // name → set of observed call arities. Arity -1 = a method-group /
        // value reference with no call site (delegate binding) — arity unknown.
        var calledMethodArities = new Dictionary<string, HashSet<int>>();
        var shadowedNames = CollectShadowedNames(body);

        void RecordFieldAccess(string name, string kind)
        {
            fieldAccess.TryGetValue(name, out var existing);
            if (kind == "read" || kind == "readwrite") existing.Read = true;
            if (kind == "write" || kind == "readwrite") existing.Write = true;
            fieldAccess[name] = existing;
        }

        void RecordCall(string name, int arity)
        {
            if (!calledMethodArities.TryGetValue(name, out var arities))
            {
                arities = new HashSet<int>();
                calledMethodArities[name] = arities;
            }
            arities.Add(arity);
        }

        static int ArgCount(InvocationExpressionSyntax inv) => inv.ArgumentList.Arguments.Count;

        foreach (var node in body.DescendantNodes())
        {
            // Case 1: this.X / base.X member access.
            if (node is MemberAccessExpressionSyntax access
                && (access.Expression.Kind() == SyntaxKind.ThisExpression
                    || access.Expression.Kind() == SyntaxKind.BaseExpression))
            {
                var name = access.Name.Identifier.Text;
                var isCallee = access.Parent is InvocationExpressionSyntax inv
                    && inv.Expression == access;

                if (isCallee && members.Methods.Contains(name))
                {
                    // `this.M(args)` / `base.M(args)` — arity from the call site.
                    RecordCall(name, ArgCount((InvocationExpressionSyntax)access.Parent!));
                }
                else if (!isCallee && members.Fields.Contains(name))
                {
                    RecordFieldAccess(name, ClassifyFieldAccess(access));
                }
                else if (!isCallee && members.Methods.Contains(name))
                {
                    // `this.M` as a value (method-group / delegate) — arity unknown.
                    RecordCall(name, -1);
                }
                continue;
            }

            // Case 2: bare-identifier invocation whose callee matches
            // a method on the containing type. `Reset()` in a method
            // body is the implicit `this.Reset()`. Suppress when the
            // name is shadowed locally (see analyzers-intraclass-shadow
            // 2.2.4).
            if (node is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax callee
                && members.Methods.Contains(callee.Identifier.Text)
                && !shadowedNames.Contains(callee.Identifier.Text))
            {
                RecordCall(callee.Identifier.Text, ArgCount(invocation));
                continue;
            }

            // Case 3: bare-identifier references that match a field
            // or property on the containing type. C# allows omitting
            // `this.`; this is the dominant style. Skip identifiers
            // sitting in declaration-name slots (avoid `private int
            // total` counting as a self-access of `total`).
            if (node is IdentifierNameSyntax id)
            {
                var name = id.Identifier.Text;
                if (!members.Fields.Contains(name) && !members.Methods.Contains(name))
                {
                    continue;
                }
                // Suppress when a parameter / local / pattern-var /
                // foreach-var / catch-var in this body shadows the
                // class member (analyzers-intraclass-shadow 2.2.4).
                if (shadowedNames.Contains(name)) continue;
                if (IsDeclarationNameSlot(id)) continue;
                // Skip the `Name` slot of a MemberAccessExpression —
                // `obj.foo` should not double-count as a bare `foo`
                // reference. The MemberAccess case above handles the
                // `this.foo` / `base.foo` form.
                if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) continue;
                // Skip the `Expression` slot of an InvocationExpression
                // that we already handled above (case 2).
                if (id.Parent is InvocationExpressionSyntax invoke && invoke.Expression == id)
                {
                    // case 2 already counted it.
                    continue;
                }

                if (members.Fields.Contains(name))
                {
                    RecordFieldAccess(name, ClassifyFieldAccess(id));
                }
                else
                {
                    // Method name used as a value (delegate binding,
                    // method-group reference). No call site → arity unknown.
                    RecordCall(name, -1);
                }
            }
        }

        var edges = new List<(string Type, string? Subtype, string Target)>();
        foreach (var (name, kinds) in fieldAccess)
        {
            var subtype = kinds.Read && kinds.Write ? "readwrite" : kinds.Write ? "write" : "read";
            edges.Add(("accessesField", subtype, name));
        }

        // Resolve each called method name to the callee's signatured form so
        // the emitted target matches the method element's natural key
        // (`method(int,string)` → canonical `method-int-string`). Arity
        // disambiguates overloads; residual same-arity ambiguity is surfaced
        // as a limitation, never a guessed edge (Trade-off 2.2.17).
        var ambiguous = new List<AmbiguousCall>();
        foreach (var (name, arities) in calledMethodArities)
        {
            // Defensive: name was added only when present in members.Methods,
            // so overloads should exist; skip if somehow absent.
            if (!members.MethodOverloads.TryGetValue(name, out var overloads) || overloads.Count == 0)
            {
                continue;
            }

            var targets = new HashSet<string>();
            if (overloads.Count == 1)
            {
                // Unique name — the only candidate is the target regardless of
                // call arity (covers params / optional-argument calls too).
                targets.Add(name + overloads[0].Signature);
            }
            else
            {
                var ambiguousArities = new List<int>();
                foreach (var arity in arities)
                {
                    // arity -1 (method-group, unknown) against an overloaded
                    // name cannot be resolved syntactically.
                    var matches = arity < 0
                        ? overloads
                        : overloads.Where(o => o.Arity == arity).ToList();
                    if (matches.Count == 1)
                    {
                        targets.Add(name + matches[0].Signature);
                    }
                    else
                    {
                        ambiguousArities.Add(arity);
                    }
                }
                if (ambiguousArities.Count > 0)
                {
                    ambiguous.Add(new AmbiguousCall(name, ambiguousArities, overloads.Count));
                }
            }

            foreach (var target in targets)
            {
                edges.Add(("callsMethod", null, target));
            }
        }

        return new IntraClassResult(edges, ambiguous);
    }

    /// <summary>
    /// Determine read / write / readwrite for a field access based on
    /// the surrounding syntax. Per language-conformance B1 accessesField
    /// subtype core vocabulary (Stage 3a).
    /// </summary>
    static string ClassifyFieldAccess(SyntaxNode access)
    {
        var parent = access.Parent;
        if (parent is null) return "read";

        // Assignment: LHS of = is write; LHS of compound assignment (+=, -=, etc.) is readwrite.
        if (parent is AssignmentExpressionSyntax assign && assign.Left == access)
        {
            if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression)) return "write";
            return "readwrite";
        }

        // ++ / -- (prefix or postfix) — readwrite.
        if (parent is PostfixUnaryExpressionSyntax
            && (parent.IsKind(SyntaxKind.PostIncrementExpression) || parent.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return "readwrite";
        }
        if (parent is PrefixUnaryExpressionSyntax
            && (parent.IsKind(SyntaxKind.PreIncrementExpression) || parent.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return "readwrite";
        }

        return "read";
    }

    /// <summary>
    /// Collect names introduced locally inside <paramref name="body"/>
    /// that can shadow class members: method/lambda/local-function
    /// parameters, local variable declarations, foreach iteration
    /// variables, catch variables, and pattern variable
    /// designations. Used by <see cref="ExtractEdges"/> to suppress
    /// bare-identifier matches when a local binding of the same name
    /// is present — see analyzers-intraclass-shadow (2.2.4).
    ///
    /// Conservative: doesn't model block scopes precisely. If a name
    /// is introduced anywhere in the body, all bare-identifier
    /// references to it are dropped. `this.X` / `base.X` remain
    /// unambiguous and continue to fire under Case 1.
    /// </summary>
    private static HashSet<string> CollectShadowedNames(SyntaxNode body)
    {
        var names = new HashSet<string>();
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case ParameterSyntax p:
                    names.Add(p.Identifier.Text);
                    break;
                case VariableDeclaratorSyntax v:
                    names.Add(v.Identifier.Text);
                    break;
                case ForEachStatementSyntax fe:
                    names.Add(fe.Identifier.Text);
                    break;
                case CatchDeclarationSyntax cd when cd.Identifier.ValueText.Length > 0:
                    names.Add(cd.Identifier.Text);
                    break;
                case SingleVariableDesignationSyntax svd:
                    names.Add(svd.Identifier.Text);
                    break;
            }
        }
        return names;
    }

    /// <summary>
    /// True when this IdentifierName sits in a declaration-name slot
    /// (the name of a parameter, variable, property, etc.) rather
    /// than as a reference. Skipping these prevents the declaration
    /// itself from counting as a self-access.
    /// </summary>
    private static bool IsDeclarationNameSlot(IdentifierNameSyntax id)
    {
        var parent = id.Parent;
        return parent switch
        {
            ParameterSyntax => true,
            VariableDeclaratorSyntax => true,
            PropertyDeclarationSyntax => true,
            MethodDeclarationSyntax => true,
            FieldDeclarationSyntax => true,
            EventDeclarationSyntax => true,
            EventFieldDeclarationSyntax => true,
            TypeParameterSyntax => true,
            EnumMemberDeclarationSyntax => true,
            // The Name slot of a NameColon (named arguments / patterns)
            NameColonSyntax => true,
            _ => false,
        };
    }
}
