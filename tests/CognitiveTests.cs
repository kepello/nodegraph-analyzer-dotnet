using Microsoft.CodeAnalysis.CSharp;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Tests for Sonar 2017 cognitive complexity (Program.Cognitive.cs).
/// Per testing rule 2: public exports must have direct tests.
/// Tests cover both branch counting and nesting penalties.
/// </summary>
public class CognitiveTests
{
    private static CognitiveHelpers.CognitiveResult Extract(string methodBody)
    {
        var code = $"class C {{ void M() {{ {methodBody} }} }}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();
        return CognitiveHelpers.Extract(method.Body!);
    }

    [Fact]
    public void EmptyMethod_ReturnsZero()
    {
        var result = Extract("");
        Assert.Equal(0, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
        Assert.Equal(0, result.MaxNestingDepth);
    }

    [Fact]
    public void SingleIfStatement_BranchCountOne_NoNestingPenalty()
    {
        // Top-level if: +1 branch, depth=0 so no nesting penalty
        var result = Extract("if (true) { }");
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
        Assert.Equal(1, result.MaxNestingDepth);
    }

    [Fact]
    public void NestedIf_AddsNestingPenalty()
    {
        // Outer if: +1 (depth 0), inner if: +1 + 1 (depth 1) = 3 total
        var result = Extract("if (a) { if (b) { } }");
        Assert.Equal(2, result.SonarBranchCount);
        Assert.Equal(1, result.SonarNestingDepthSum); // inner if at depth 1
        Assert.Equal(2, result.MaxNestingDepth);
    }

    [Fact]
    public void ElseIfContinuation_CountsWithoutNestingPenalty()
    {
        // if + else-if: each gets +1, but else-if doesn't take nesting penalty
        var result = Extract("if (a) { } else if (b) { }");
        Assert.Equal(2, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
    }

    [Fact]
    public void BareElse_CountsAsStructural()
    {
        // if + else: if gets +1, else gets +1 (no nesting penalty)
        var result = Extract("if (a) { } else { }");
        Assert.Equal(2, result.SonarBranchCount); // if=1 + else=1
        Assert.Equal(0, result.SonarNestingDepthSum);
    }

    [Fact]
    public void ForLoop_CountsAsNestingControlFlow()
    {
        var result = Extract("for (int i = 0; i < 10; i++) { }");
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
        Assert.Equal(1, result.MaxNestingDepth);
    }

    [Fact]
    public void NestedForLoop_AddsNestingPenalty()
    {
        // Outer for: +1 (depth 0), inner for: +1 + 1 (depth 1) = 3 total
        var result = Extract("for (int i = 0; i < 10; i++) { for (int j = 0; j < 5; j++) { } }");
        Assert.Equal(2, result.SonarBranchCount);
        Assert.Equal(1, result.SonarNestingDepthSum);
        Assert.Equal(2, result.MaxNestingDepth);
    }

    [Fact]
    public void LogicalOperatorChain_CountsRuns()
    {
        // a && b && c: single run of &&, counts as 1
        var result = Extract("var x = a && b && c;");
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
    }

    [Fact]
    public void MixedLogicalOperators_CountsMultipleRuns()
    {
        // (a && b) || (c && d): && run, || run, && run = 3 runs
        var result = Extract("var x = (a && b) || (c && d);");
        Assert.Equal(3, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
    }

    [Fact]
    public void SwitchStatement_CountsAsNestingControlFlow()
    {
        var result = Extract("switch (x) { case 1: break; }");
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
        Assert.Equal(1, result.MaxNestingDepth);
    }

    [Fact]
    public void TernaryOperator_CountsAsNestingControlFlow()
    {
        var result = Extract("var x = a ? 1 : 2;");
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
        Assert.Equal(1, result.MaxNestingDepth);
    }

    [Fact]
    public void CatchClause_CountsAsNestingControlFlow()
    {
        var result = Extract("try { } catch { }");
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
        Assert.Equal(1, result.MaxNestingDepth);
    }

    [Fact]
    public void Lambda_OpensNestingLevelWithoutIncrement()
    {
        // Lambda opens nesting but doesn't increment branch count (unless recursive)
        var result = Extract("Action a = () => { if (x) { } };");
        Assert.Equal(1, result.SonarBranchCount); // the if
        Assert.Equal(1, result.SonarNestingDepthSum); // if at depth 1 (inside lambda)
        Assert.Equal(2, result.MaxNestingDepth);
    }

    [Fact]
    public void LocalFunction_OpensNestingLevelWithoutIncrement()
    {
        var result = Extract(@"
            void Local() {
                if (x) { }
            }
            Local();
        ");
        Assert.Equal(1, result.SonarBranchCount); // the if
        Assert.Equal(1, result.SonarNestingDepthSum); // if at depth 1 (inside local function)
        Assert.Equal(2, result.MaxNestingDepth);
    }

    [Fact]
    public void DeeplyNested_AccumulatesNestingPenalties()
    {
        // if (depth 0: +1), if (depth 1: +1+1), if (depth 2: +1+2) = 6 total
        var result = Extract("if (a) { if (b) { if (c) { } } }");
        Assert.Equal(3, result.SonarBranchCount);
        Assert.Equal(3, result.SonarNestingDepthSum); // 0 + 1 + 2
        Assert.Equal(3, result.MaxNestingDepth);
    }

    [Fact]
    public void ComplexMethod_SumsAllComponents()
    {
        // Top-level if (+1, depth 0)
        // Inner for (+1 at depth 1, so nesting penalty = 1)
        // Inner if (+1 at depth 2, so nesting penalty = 2)
        // Logical chain (a && b) || c: has 2 runs (&& run, then || run)
        // CountLogicalRuns adds 2 to branch count
        // Total: if (1), for (1), inner-if (1), logical-runs (2) = 5
        var result = Extract(@"
            if (x > 0) {
                for (int i = 0; i < 10; i++) {
                    if ((a && b) || c) {
                        DoSomething();
                    }
                }
            }
        ");
        Assert.Equal(5, result.SonarBranchCount); // if, for, inner-if, 2 logical runs
        Assert.Equal(3, result.SonarNestingDepthSum); // for=1, inner-if=2
        Assert.Equal(3, result.MaxNestingDepth);
    }

    /// <summary>
    /// Pins trade-off 2.2.3: recursion (method calling itself) should add B+N
    /// but isn't detected yet. This test documents the CURRENT behavior (no increment).
    /// Per testing rule 3: documented gaps must be pinned.
    /// </summary>
    [Fact]
    public void Recursion_NotDetected_TradeOff_2_2_3()
    {
        // Recursion should add +1 + nesting, but detection requires symbol resolution
        // Current behavior: M() call is just an invocation expression, not recognized as recursive
        var code = "class C { void M() { if (x) M(); } }";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();
        var result = CognitiveHelpers.Extract(method.Body!);

        // CURRENT behavior: only the if counts (1+0=1)
        // FUTURE behavior (when 2.2.3 is resolved): if + recursive call = 1 + (1+1) = 3
        Assert.Equal(1, result.SonarBranchCount);
        Assert.Equal(0, result.SonarNestingDepthSum);
    }
}
