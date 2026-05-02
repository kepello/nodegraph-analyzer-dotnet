/// <summary>
/// Per-method scalar extraction (slice 3, .NET portion). Mirrors the
/// TS analyzer's scalars.ts.
///
/// `ExtractScalars(node)` returns the 10 inputs the engine needs for
/// its complexity / Halstead derivations (slice 4):
///
///   - branchCount              cyclomatic input
///   - sonarBranchCount         cognitive input (Sonar 2017)
///   - sonarNestingDepthSum     cognitive input
///   - maxNestingDepth          (also from the cognitive walker)
///   - parameterCount           directly from the signature
///   - returnStatementCount     count of `return` in the body
///   - halsteadOperatorCount    Halstead N1
///   - halsteadOperandCount     Halstead N2
///   - halsteadUniqueOperators  Halstead n1
///   - halsteadUniqueOperands   Halstead n2
///
/// Returns null for nodes that aren't body-bearing (interface
/// methods, abstract methods, type-only declarations). Property /
/// indexer / event declarations with bodies are aggregated across
/// their accessors — for an auto-property with no body, no scalars
/// emit. A future refinement could split per-accessor; v1 collapses
/// to keep the wire shape parallel to TS's per-property emission.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class ScalarHelpers
{
    public sealed record ScalarsResult(
        int? BranchCount,
        int? SonarBranchCount,
        int? SonarNestingDepthSum,
        int? MaxNestingDepth,
        int? ParameterCount,
        int? ReturnStatementCount,
        int? HalsteadOperatorCount,
        int? HalsteadOperandCount,
        int? HalsteadUniqueOperators,
        int? HalsteadUniqueOperands);

    /// <summary>
    /// Extract scalars for a body-bearing declaration. Returns null
    /// when the declaration has no body (interface signature,
    /// abstract method, auto-property, field, type-only).
    /// </summary>
    public static ScalarsResult? Extract(SyntaxNode node)
    {
        var bodies = GetBodies(node);
        if (bodies.Count == 0) return null;

        var branchCount = 0;
        var sonarBranchCount = 0;
        var sonarNestingDepthSum = 0;
        var maxNestingDepth = 0;
        var returnCount = 0;

        foreach (var body in bodies)
        {
            branchCount += CyclomaticHelpers.CountBranches(body);
            var cog = CognitiveHelpers.Extract(body);
            sonarBranchCount += cog.SonarBranchCount;
            sonarNestingDepthSum += cog.SonarNestingDepthSum;
            if (cog.MaxNestingDepth > maxNestingDepth) maxNestingDepth = cog.MaxNestingDepth;
            returnCount += CountReturns(body);
        }

        // Halstead: union the unique sets across all bodies (a multi-
        // accessor property's get and set share operators; the union
        // is the right answer, not the sum).
        var hal = HalsteadHelpers.Extract(bodies);

        var parameterCount = GetParameterCount(node);

        return new ScalarsResult(
            branchCount,
            sonarBranchCount,
            sonarNestingDepthSum,
            maxNestingDepth,
            parameterCount,
            returnCount,
            hal.OperatorCount,
            hal.OperandCount,
            hal.UniqueOperators,
            hal.UniqueOperands);
    }

    /// <summary>
    /// True iff this declaration has at least one body whose scalars
    /// are meaningful.
    /// </summary>
    public static bool HasExtractableBody(SyntaxNode node) => GetBodies(node).Count > 0;

    /// <summary>
    /// Enumerate the bodies for a declaration. Methods / constructors
    /// / destructors / local-functions / operators / conversion-ops
    /// have one body or one expression-body. Properties / indexers
    /// have zero or more bodies (one per accessor with a body).
    /// </summary>
    private static List<SyntaxNode> GetBodies(SyntaxNode node)
    {
        var bodies = new List<SyntaxNode>();

        switch (node)
        {
            case MethodDeclarationSyntax m:
                if (m.Body is { } b) bodies.Add(b);
                else if (m.ExpressionBody is { } eb) bodies.Add(eb.Expression);
                break;
            case ConstructorDeclarationSyntax c:
                if (c.Body is { } cb) bodies.Add(cb);
                else if (c.ExpressionBody is { } ceb) bodies.Add(ceb.Expression);
                break;
            case DestructorDeclarationSyntax d:
                if (d.Body is { } db) bodies.Add(db);
                else if (d.ExpressionBody is { } deb) bodies.Add(deb.Expression);
                break;
            case LocalFunctionStatementSyntax lf:
                if (lf.Body is { } lfb) bodies.Add(lfb);
                else if (lf.ExpressionBody is { } lfeb) bodies.Add(lfeb.Expression);
                break;
            case OperatorDeclarationSyntax op:
                if (op.Body is { } ob) bodies.Add(ob);
                else if (op.ExpressionBody is { } oeb) bodies.Add(oeb.Expression);
                break;
            case ConversionOperatorDeclarationSyntax co:
                if (co.Body is { } cob) bodies.Add(cob);
                else if (co.ExpressionBody is { } coeb) bodies.Add(coeb.Expression);
                break;
            case PropertyDeclarationSyntax p:
                if (p.ExpressionBody is { } peb) bodies.Add(peb.Expression);
                else if (p.AccessorList is { } al)
                {
                    foreach (var acc in al.Accessors)
                    {
                        if (acc.Body is { } ab) bodies.Add(ab);
                        else if (acc.ExpressionBody is { } aeb) bodies.Add(aeb.Expression);
                    }
                }
                break;
            case IndexerDeclarationSyntax ix:
                if (ix.ExpressionBody is { } ixeb) bodies.Add(ixeb.Expression);
                else if (ix.AccessorList is { } ixal)
                {
                    foreach (var acc in ixal.Accessors)
                    {
                        if (acc.Body is { } ab) bodies.Add(ab);
                        else if (acc.ExpressionBody is { } aeb) bodies.Add(aeb.Expression);
                    }
                }
                break;
            case AccessorDeclarationSyntax acc:
                if (acc.Body is { } accb) bodies.Add(accb);
                else if (acc.ExpressionBody is { } acceb) bodies.Add(acceb.Expression);
                break;
        }

        return bodies;
    }

    private static int? GetParameterCount(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ParameterList.Parameters.Count,
        ConstructorDeclarationSyntax c => c.ParameterList.Parameters.Count,
        LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters.Count,
        OperatorDeclarationSyntax op => op.ParameterList.Parameters.Count,
        ConversionOperatorDeclarationSyntax co => co.ParameterList.Parameters.Count,
        IndexerDeclarationSyntax ix => ix.ParameterList.Parameters.Count,
        DestructorDeclarationSyntax => 0,
        // Property / accessor: no parameters in the conventional
        // sense (the implicit `value` for setters isn't user-declared).
        PropertyDeclarationSyntax => 0,
        AccessorDeclarationSyntax => 0,
        _ => null,
    };

    /// <summary>
    /// Count `return` statements anywhere in the body. Excludes
    /// returns inside nested function/lambda bodies (those returns
    /// belong to the inner callable's metric). A single-expression
    /// body (`=> expr`) is conceptually one implicit return; we add
    /// 1 when the body itself isn't a Block.
    /// </summary>
    private static int CountReturns(SyntaxNode body)
    {
        var count = 0;

        void Visit(SyntaxNode node, bool isOriginalBody)
        {
            if (!isOriginalBody && IsNestedCallable(node))
            {
                // Don't descend into nested callables.
                return;
            }
            if (node is ReturnStatementSyntax)
            {
                count += 1;
            }
            foreach (var child in node.ChildNodes())
            {
                Visit(child, false);
            }
        }

        foreach (var child in body.ChildNodes())
        {
            Visit(child, false);
        }

        // Expression-body (`=> expr`) — implicit return. Body slot is
        // an ExpressionSyntax, not a BlockSyntax.
        if (body is not BlockSyntax) count += 1;
        return count;
    }

    private static bool IsNestedCallable(SyntaxNode node) => node switch
    {
        SimpleLambdaExpressionSyntax => true,
        ParenthesizedLambdaExpressionSyntax => true,
        AnonymousMethodExpressionSyntax => true,
        LocalFunctionStatementSyntax => true,
        _ => false,
    };

}
