using Microsoft.CodeAnalysis.CSharp;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Tests for cyclomatic complexity branch counting (Program.Cyclomatic.cs).
/// Per testing rule 2: public exports must have direct tests.
/// </summary>
public class CyclomaticTests
{
    private static int CountBranches(string methodBody)
    {
        var code = $"class C {{ void M() {{ {methodBody} }} }}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();
        return CyclomaticHelpers.CountBranches(method.Body!);
    }

    [Fact]
    public void EmptyMethod_ReturnsZero()
    {
        Assert.Equal(0, CountBranches(""));
    }

    [Fact]
    public void SingleIfStatement_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("if (true) { }"));
    }

    [Fact]
    public void IfElseIfElse_ReturnsTwo()
    {
        // if counts, else-if counts, else does NOT count (not a branch point)
        Assert.Equal(2, CountBranches("if (x) { } else if (y) { } else { }"));
    }

    [Fact]
    public void SwitchWithThreeCases_ReturnsThree()
    {
        // Each case label counts as a branch
        Assert.Equal(3, CountBranches(@"
            switch (x) {
                case 1: break;
                case 2: break;
                default: break;
            }
        "));
    }

    [Fact]
    public void ForLoop_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("for (int i = 0; i < 10; i++) { }"));
    }

    [Fact]
    public void ForEachLoop_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("foreach (var x in new[] {1, 2}) { }"));
    }

    [Fact]
    public void WhileLoop_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("while (true) { }"));
    }

    [Fact]
    public void DoWhileLoop_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("do { } while (true);"));
    }

    [Fact]
    public void TryCatchFinally_ReturnsOne()
    {
        // catch counts; try and finally don't
        Assert.Equal(1, CountBranches("try { } catch { } finally { }"));
    }

    [Fact]
    public void TernaryOperator_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("var x = true ? 1 : 2;"));
    }

    [Fact]
    public void LogicalAndOperator_ReturnsOne()
    {
        // Each && counts separately (Sonar extended cyclomatic)
        Assert.Equal(1, CountBranches("var x = a && b;"));
    }

    [Fact]
    public void LogicalOrOperator_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("var x = a || b;"));
    }

    [Fact]
    public void NullCoalescingOperator_ReturnsOne()
    {
        Assert.Equal(1, CountBranches("var x = a ?? b;"));
    }

    [Fact]
    public void ChainedLogicalOperators_CountsEachOccurrence()
    {
        // a && b && c has two && operators -> 2 branches
        Assert.Equal(2, CountBranches("var x = a && b && c;"));
    }

    [Fact]
    public void MixedLogicalOperators_CountsEachOccurrence()
    {
        // (a && b) || (c && d) has 3 logical operators -> 3 branches
        Assert.Equal(3, CountBranches("var x = (a && b) || (c && d);"));
    }

    [Fact]
    public void GotoStatement_ReturnsOne()
    {
        Assert.Equal(1, CountBranches(@"
            label:
            goto label;
        "));
    }

    [Fact]
    public void ComplexMethod_SumsAllBranches()
    {
        // if (1) + for (1) + if (1) + && (1) + switch-2-cases (2) = 6
        var result = CountBranches(@"
            if (x > 0) {
                for (int i = 0; i < 10; i++) {
                    if (a && b) {
                        switch (i) {
                            case 1: break;
                            case 2: break;
                        }
                    }
                }
            }
        ");
        Assert.Equal(6, result); // outer if, for, inner if, &&, 2 case labels
    }

    [Fact]
    public void SwitchExpression_CountsEachArm()
    {
        // Switch expression with 3 arms -> 3 branches
        var result = CountBranches(@"
            var result = x switch {
                1 => ""one"",
                2 => ""two"",
                _ => ""other""
            };
        ");
        Assert.Equal(3, result);
    }
}
