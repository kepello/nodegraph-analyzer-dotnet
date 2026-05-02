/// <summary>
/// Cyclomatic-complexity input: branch-point count for a C# body.
///
/// Counts decision points within a method/property/accessor/constructor
/// body. Cyclomatic complexity itself = branchCount + 1; emitting just
/// the raw count keeps the analyzer language-agnostic — the engine
/// applies a single derivation rule across every analyzer's output.
///
/// Branch points counted (Sonar's "extended" cyclomatic, which counts
/// each `&&` / `||` separately — distinct from McCabe's classic
/// cyclomatic which counts a chain as one):
///
///   - if statements (each `if`; `else` alone is not)
///   - case labels in switch statements + default labels
///   - switch-expression arms
///   - for / foreach / while / do-while
///   - catch clauses
///   - ternary expression (?:)
///   - && / || / ?? (each occurrence)
///   - is-pattern expressions (`x is T t`) when the pattern is non-trivial
///   - goto labels (each `goto case` / `goto label`)
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class CyclomaticHelpers
{
    public static int CountBranches(SyntaxNode body)
    {
        var count = 0;
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case DefaultSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case GotoStatementSyntax:
                    count += 1;
                    continue;
            }

            if (node is BinaryExpressionSyntax bin && IsLogicalOp(bin.OperatorToken.Kind()))
            {
                count += 1;
                continue;
            }
        }
        return count;
    }

    public static bool IsLogicalOp(SyntaxKind kind) =>
        kind == SyntaxKind.AmpersandAmpersandToken
        || kind == SyntaxKind.BarBarToken
        || kind == SyntaxKind.QuestionQuestionToken;
}
