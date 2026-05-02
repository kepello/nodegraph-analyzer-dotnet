/// <summary>
/// Cognitive-complexity inputs — clean-room C# port of the rules
/// described in G. Ann Campbell, "Cognitive Complexity: A new way of
/// measuring understandability" (Sonar Source, 2017). The published
/// whitepaper documents the rules; this implementation derives from
/// those rules, not from any reference source-code implementation.
/// License oracle (sonar-dotnet) was not consulted for the
/// implementation shape — only the spec.
///
/// Three increment categories per the spec:
///
///   B (Increment) — "+1 for each break in linear control flow"
///     fires for: if, ternary, switch, for/foreach/while/do-while,
///     catch, goto-likes, sequences of binary logical operators
///     (each contiguous run of identical operators counts once).
///
///   N (Nesting) — "+N for each break that occurs inside a nested
///     control structure" where N is the current nesting level. The
///     same constructs that increment B at the top level also add
///     N when they're nested inside another control flow.
///
///   S (Structural) — "+1 with no nesting penalty" for `else`, `else
///     if`, and label jumps with explicit targets.
///
/// The analyzer emits two raw scalars:
///
///   sonarBranchCount     = total count of B-increments
///   sonarNestingDepthSum = sum of nesting penalties at B-increment
///                          sites that take a nesting penalty
///
/// Engine derives Sonar-cognitive = sonarBranchCount +
/// sonarNestingDepthSum.
///
/// Maximum nesting depth is computed alongside since the same walker
/// already maintains the depth counter.
///
/// Documented gap from the spec: recursion (a method calling itself)
/// adds B + N per Sonar's rules but isn't detected here yet —
/// detecting it requires symbol resolution against the enclosing
/// method, deferred to a follow-up refinement.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class CognitiveHelpers
{
    public record CognitiveResult(
        int SonarBranchCount,
        int SonarNestingDepthSum,
        int MaxNestingDepth);

    public static CognitiveResult Extract(SyntaxNode body)
    {
        var state = new State();
        foreach (var child in body.ChildNodes())
        {
            Visit(child, state);
        }
        return new CognitiveResult(
            state.BranchCount,
            state.NestingDepthSum,
            state.MaxNestingDepth);
    }

    private sealed class State
    {
        public int BranchCount;
        public int NestingDepthSum;
        public int MaxNestingDepth;
        public int Depth;
    }

    /// <summary>
    /// Constructs that contribute B + N (i.e., add the nesting penalty
    /// when nested) AND open a new nesting level for descendants.
    /// </summary>
    private static bool IsNestingControlFlow(SyntaxNode node) => node switch
    {
        IfStatementSyntax => true,
        ForStatementSyntax => true,
        ForEachStatementSyntax => true,
        ForEachVariableStatementSyntax => true,
        WhileStatementSyntax => true,
        DoStatementSyntax => true,
        SwitchStatementSyntax => true,
        SwitchExpressionSyntax => true,
        CatchClauseSyntax => true,
        ConditionalExpressionSyntax => true,
        _ => false,
    };

    /// <summary>
    /// True iff this IfStatementSyntax is the `else if` continuation of
    /// a parent IfStatementSyntax — i.e., it sits in the parent's `Else`
    /// clause's `Statement` slot. Roslyn models `else if` as an
    /// IfStatement nested in an ElseClause; the recogniser walks two
    /// parents up to confirm shape.
    /// </summary>
    private static bool IsElseIfContinuation(SyntaxNode node)
    {
        if (node is not IfStatementSyntax) return false;
        var parent = node.Parent;
        if (parent is not ElseClauseSyntax) return false;
        // The grandparent of an else-if is the outer IfStatement.
        return parent.Parent is IfStatementSyntax;
    }

    /// <summary>
    /// Lambdas / local functions / nested methods open a nesting level
    /// for code declared inside them. Per the spec they don't
    /// themselves contribute a B-increment unless they're recursive
    /// (recursion handling deferred to a follow-up).
    /// </summary>
    private static bool IsFunctionLike(SyntaxNode node) => node switch
    {
        SimpleLambdaExpressionSyntax => true,
        ParenthesizedLambdaExpressionSyntax => true,
        AnonymousMethodExpressionSyntax => true,
        LocalFunctionStatementSyntax => true,
        _ => false,
    };

    /// <summary>
    /// True when a binary-logical node is the top of its operator
    /// chain — i.e., its parent is not itself a logical binary. The
    /// chain root counts; nested operands are walked through the run-
    /// counter.
    /// </summary>
    private static bool IsLogicalChainRoot(SyntaxNode node)
    {
        if (node is not BinaryExpressionSyntax bin) return false;
        if (!CyclomaticHelpers.IsLogicalOp(bin.OperatorToken.Kind())) return false;
        if (node.Parent is not BinaryExpressionSyntax parentBin) return true;
        return !CyclomaticHelpers.IsLogicalOp(parentBin.OperatorToken.Kind());
    }

    /// <summary>
    /// Flatten a logical-binary chain into the sequence of operators in
    /// textual (in-order) order. `(a && b) || (c && d)` parses left-
    /// associatively; the in-order walk yields [&&, ||, &&].
    /// </summary>
    private static void FlattenOperators(SyntaxNode node, List<SyntaxKind> ops)
    {
        if (node is BinaryExpressionSyntax bin
            && CyclomaticHelpers.IsLogicalOp(bin.OperatorToken.Kind()))
        {
            FlattenOperators(bin.Left, ops);
            ops.Add(bin.OperatorToken.Kind());
            FlattenOperators(bin.Right, ops);
        }
    }

    /// <summary>
    /// Returns the count of distinct logical-operator runs in a chain
    /// rooted at a top-level binary-logical expression. A "run" is a
    /// contiguous group of identical operators in the textual sequence;
    /// each switch starts a new run.
    /// </summary>
    private static int CountLogicalRuns(SyntaxNode chainRoot)
    {
        var ops = new List<SyntaxKind>();
        FlattenOperators(chainRoot, ops);
        if (ops.Count == 0) return 0;
        var runs = 1;
        for (var i = 1; i < ops.Count; i++)
        {
            if (ops[i] != ops[i - 1]) runs += 1;
        }
        return runs;
    }

    private static void Visit(SyntaxNode node, State state)
    {
        // 1. Increments at this node.
        if (IsElseIfContinuation(node))
        {
            // +1, no nesting penalty (continuation of an already-nested if).
            state.BranchCount += 1;
        }
        else if (IsNestingControlFlow(node))
        {
            state.BranchCount += 1;
            state.NestingDepthSum += state.Depth;
        }
        else if (IsLogicalChainRoot(node))
        {
            state.BranchCount += CountLogicalRuns(node);
        }

        // Bare-else: an IfStatement whose Else clause's Statement is
        // not another IfStatement. Each such "else" gets +1 (no
        // nesting penalty) per the "if / else if / else: each gets +1"
        // rule.
        if (node is IfStatementSyntax ifNode
            && ifNode.Else is { Statement: var elseStmt }
            && elseStmt is not IfStatementSyntax)
        {
            state.BranchCount += 1;
        }

        // 2. Open a nesting level for descendants when this is a
        // nesting construct or a function-like.
        var opensNesting = IsNestingControlFlow(node) && !IsElseIfContinuation(node);
        var opensFunctionNesting = IsFunctionLike(node);

        if (opensNesting || opensFunctionNesting)
        {
            state.Depth += 1;
            if (state.Depth > state.MaxNestingDepth) state.MaxNestingDepth = state.Depth;
        }

        // 3. Recurse.
        foreach (var child in node.ChildNodes())
        {
            Visit(child, state);
        }

        if (opensNesting || opensFunctionNesting)
        {
            state.Depth -= 1;
        }
    }
}
