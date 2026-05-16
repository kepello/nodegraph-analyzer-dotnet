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
/// Cases 2 and 3 can over-count when a parameter or local variable
/// shadows a class member's name. Walking the enclosing scope to
/// detect shadowing would tighten precision; v1 accepts the small
/// over-count because false-positives in the cohesion graph
/// over-report cohesion (LCOM4 lower) while false-negatives
/// under-report it. Over-reporting is the safer bias for slice 6's
/// derivation; LCOM4 precision can be tightened with semantic
/// analysis when a real consumer hits the wall.
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
        HashSet<string> Methods);

    /// <summary>
    /// Build the member index for a type declaration. Includes fields,
    /// properties (treated as fields for the cohesion graph since they
    /// hold state), methods, and accessors.
    /// </summary>
    public static ClassMemberIndex BuildIndex(TypeDeclarationSyntax typeDecl)
    {
        var fields = new HashSet<string>();
        var methods = new HashSet<string>();

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
                    break;
                case ConstructorDeclarationSyntax:
                case DestructorDeclarationSyntax:
                    // Not referenced by name from inside the type.
                    break;
            }
        }

        return new ClassMemberIndex(fields, methods);
    }

    /// <summary>
    /// Walk a method/property/accessor body and return the intra-class
    /// edges it implies. Each member-name appears at most once per
    /// edge type; the caller canonicalizes targetName before emitting.
    /// </summary>
    public static List<(string Type, string? Subtype, string Target)> ExtractEdges(
        SyntaxNode body,
        ClassMemberIndex members)
    {
        // Per language-conformance B1 accessesField subtype core (Stage 3a,
        // 2026-05-16): each access is classified as `read`, `write`, or
        // `readwrite` based on the surrounding syntactic context.
        var fieldAccess = new Dictionary<string, (bool Read, bool Write)>();
        var calledMethods = new HashSet<string>();

        void RecordFieldAccess(string name, string kind)
        {
            fieldAccess.TryGetValue(name, out var existing);
            if (kind == "read" || kind == "readwrite") existing.Read = true;
            if (kind == "write" || kind == "readwrite") existing.Write = true;
            fieldAccess[name] = existing;
        }

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
                    calledMethods.Add(name);
                }
                else if (!isCallee && members.Fields.Contains(name))
                {
                    RecordFieldAccess(name, ClassifyFieldAccess(access));
                }
                else if (!isCallee && members.Methods.Contains(name))
                {
                    calledMethods.Add(name);
                }
                continue;
            }

            // Case 2: bare-identifier invocation whose callee matches
            // a method on the containing type. `Reset()` in a method
            // body is the implicit `this.Reset()`.
            if (node is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax callee
                && members.Methods.Contains(callee.Identifier.Text))
            {
                calledMethods.Add(callee.Identifier.Text);
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
                    // method-group reference). Treat as callsMethod.
                    calledMethods.Add(name);
                }
            }
        }

        var edges = new List<(string Type, string? Subtype, string Target)>();
        foreach (var (name, kinds) in fieldAccess)
        {
            var subtype = kinds.Read && kinds.Write ? "readwrite" : kinds.Write ? "write" : "read";
            edges.Add(("accessesField", subtype, name));
        }
        foreach (var m in calledMethods) edges.Add(("callsMethod", null, m));
        return edges;
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
