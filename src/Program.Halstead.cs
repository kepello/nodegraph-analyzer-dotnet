/// <summary>
/// Halstead software-science raw inputs (Halstead 1977) for C# bodies.
///
/// The classic four:
///   n1 = halsteadUniqueOperators
///   n2 = halsteadUniqueOperands
///   N1 = halsteadOperatorCount  (total occurrences of operators)
///   N2 = halsteadOperandCount   (total occurrences of operands)
///
/// The engine derives Halstead vocabulary, length, volume, effort
/// from these four; this module only counts.
///
/// Classification (clean-room derivation from Halstead's original
/// paper plus the conventional treatments for C# in subsequent
/// literature; the exact set of token kinds is language-specific —
/// the choices below are conservative and stable across method
/// bodies):
///
///   Operators (each occurrence ticks N1; the operator's name is its
///   identity for n1):
///     - All binary, unary, assignment, and update operators
///     - The ternary `?:`
///     - Property access `.`, member access via `?.`
///     - Element access `[]`, `?[]`
///     - Method invocation (each CallExpression)
///     - `new`, `is`, `as`
///     - `await`, `throw expression`, `default`, `nameof`, `sizeof`,
///       `typeof`, `checked`, `unchecked`
///     - Control keywords as operators: `if`, `for`, `while`, `do`,
///       `switch`, `case`, `break`, `continue`, `return`, `throw`,
///       `try`, `catch`, `finally`, `goto`
///     - Object/array initializer syntax, with-expression, target-
///       typed new
///
///   Operands (each occurrence ticks N2; the operand's text is its
///   identity for n2):
///     - Identifier references (variables, parameters, member names
///       used as identifiers)
///     - Numeric / string / boolean / null / character literals,
///       interpolated-string parts, `this`, `base`
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class HalsteadHelpers
{
    public record HalsteadResult(
        int OperatorCount,
        int OperandCount,
        int UniqueOperators,
        int UniqueOperands);

    public static bool IsTokenOperator(SyntaxKind kind) => TokenOperators.Contains(kind);
    public static bool IsNodeOperator(SyntaxKind kind) => NodeOperators.Contains(kind);
    public static bool IsLiteralOperandKind(SyntaxKind kind) => LiteralOperandKinds.Contains(kind);

    /// <summary>
    /// Token kinds that are operators in their own right when they
    /// appear as the operator slot of a binary / prefix / postfix
    /// expression. The token text is the operator identity.
    /// </summary>
    private static readonly HashSet<SyntaxKind> TokenOperators = new()
    {
        // Binary arithmetic / bitwise / shift / comparison / logical
        SyntaxKind.PlusToken,
        SyntaxKind.MinusToken,
        SyntaxKind.AsteriskToken,
        SyntaxKind.SlashToken,
        SyntaxKind.PercentToken,
        SyntaxKind.LessThanToken,
        SyntaxKind.GreaterThanToken,
        SyntaxKind.LessThanEqualsToken,
        SyntaxKind.GreaterThanEqualsToken,
        SyntaxKind.EqualsEqualsToken,
        SyntaxKind.ExclamationEqualsToken,
        SyntaxKind.AmpersandToken,
        SyntaxKind.BarToken,
        SyntaxKind.CaretToken,
        SyntaxKind.AmpersandAmpersandToken,
        SyntaxKind.BarBarToken,
        SyntaxKind.QuestionQuestionToken,
        SyntaxKind.LessThanLessThanToken,
        SyntaxKind.GreaterThanGreaterThanToken,
        // Assignment + compound-assignment
        SyntaxKind.EqualsToken,
        SyntaxKind.PlusEqualsToken,
        SyntaxKind.MinusEqualsToken,
        SyntaxKind.AsteriskEqualsToken,
        SyntaxKind.SlashEqualsToken,
        SyntaxKind.PercentEqualsToken,
        SyntaxKind.LessThanLessThanEqualsToken,
        SyntaxKind.GreaterThanGreaterThanEqualsToken,
        SyntaxKind.AmpersandEqualsToken,
        SyntaxKind.BarEqualsToken,
        SyntaxKind.CaretEqualsToken,
        SyntaxKind.QuestionQuestionEqualsToken,
        // Update / unary
        SyntaxKind.PlusPlusToken,
        SyntaxKind.MinusMinusToken,
        SyntaxKind.ExclamationToken,
        SyntaxKind.TildeToken,
    };

    /// <summary>
    /// Node kinds that are themselves operators (not just punctuation).
    /// Each node contributes one occurrence; the node-kind name is the
    /// operator identity for unique counting.
    /// </summary>
    private static readonly HashSet<SyntaxKind> NodeOperators = new()
    {
        SyntaxKind.ConditionalExpression,           // ?:
        SyntaxKind.SimpleMemberAccessExpression,    // .
        SyntaxKind.PointerMemberAccessExpression,   // ->
        SyntaxKind.MemberBindingExpression,         // ?.
        SyntaxKind.ElementAccessExpression,         // [ ]
        SyntaxKind.ElementBindingExpression,        // ?[ ]
        SyntaxKind.InvocationExpression,            // ()
        SyntaxKind.ObjectCreationExpression,        // new T(...)
        SyntaxKind.ImplicitObjectCreationExpression,// new(...)
        SyntaxKind.ArrayCreationExpression,         // new T[]
        SyntaxKind.ImplicitArrayCreationExpression, // new[] {...}
        SyntaxKind.AwaitExpression,                 // await
        SyntaxKind.IsExpression,                    // is
        SyntaxKind.IsPatternExpression,             // is pattern
        SyntaxKind.AsExpression,                    // as
        SyntaxKind.CastExpression,                  // (T)x
        SyntaxKind.ThrowExpression,                 // throw
        SyntaxKind.DefaultExpression,               // default(T)
        SyntaxKind.DefaultLiteralExpression,        // default
        SyntaxKind.SizeOfExpression,
        SyntaxKind.TypeOfExpression,
        SyntaxKind.NameOfKeyword,
        SyntaxKind.CheckedExpression,
        SyntaxKind.UncheckedExpression,
        SyntaxKind.SuppressNullableWarningExpression, // !
        SyntaxKind.WithExpression,
        SyntaxKind.SwitchExpression,                // x switch { ... }
        SyntaxKind.RangeExpression,                 // ..
        SyntaxKind.IfStatement,
        SyntaxKind.ForStatement,
        SyntaxKind.ForEachStatement,
        SyntaxKind.ForEachVariableStatement,
        SyntaxKind.WhileStatement,
        SyntaxKind.DoStatement,
        SyntaxKind.SwitchStatement,
        SyntaxKind.CaseSwitchLabel,
        SyntaxKind.CasePatternSwitchLabel,
        SyntaxKind.DefaultSwitchLabel,
        SyntaxKind.SwitchExpressionArm,
        SyntaxKind.BreakStatement,
        SyntaxKind.ContinueStatement,
        SyntaxKind.ReturnStatement,
        SyntaxKind.ThrowStatement,
        SyntaxKind.TryStatement,
        SyntaxKind.CatchClause,
        SyntaxKind.FinallyClause,
        SyntaxKind.GotoStatement,
        SyntaxKind.GotoCaseStatement,
        SyntaxKind.GotoDefaultStatement,
        SyntaxKind.YieldReturnStatement,
        SyntaxKind.YieldBreakStatement,
        SyntaxKind.UsingStatement,
        SyntaxKind.LockStatement,
        SyntaxKind.FixedStatement,
    };

    /// <summary>
    /// Literal token kinds that count as operands. Roslyn surfaces
    /// each of these as a LiteralExpressionSyntax with a specific
    /// `Kind`; we read the kind off the expression.
    /// </summary>
    private static readonly HashSet<SyntaxKind> LiteralOperandKinds = new()
    {
        SyntaxKind.NumericLiteralExpression,
        SyntaxKind.StringLiteralExpression,
        SyntaxKind.Utf8StringLiteralExpression,
        SyntaxKind.CharacterLiteralExpression,
        SyntaxKind.TrueLiteralExpression,
        SyntaxKind.FalseLiteralExpression,
        SyntaxKind.NullLiteralExpression,
        SyntaxKind.ThisExpression,
        SyntaxKind.BaseExpression,
        SyntaxKind.InterpolatedStringExpression,
    };

    public static HalsteadResult Extract(SyntaxNode body) => Extract(new[] { body });

    /// <summary>
    /// Extract over a sequence of bodies and merge into one Halstead
    /// result. Multi-body declarations (properties / indexers with
    /// both get and set accessors) call this to produce a single
    /// scalar set whose unique counts are the union across accessors,
    /// not the sum.
    /// </summary>
    public static HalsteadResult Extract(IEnumerable<SyntaxNode> bodies)
    {
        var operatorCount = 0;
        var operandCount = 0;
        var uniqueOperators = new HashSet<string>();
        var uniqueOperands = new HashSet<string>();

        void TickOperator(string text)
        {
            operatorCount += 1;
            uniqueOperators.Add(text);
        }
        void TickOperand(string text)
        {
            operandCount += 1;
            uniqueOperands.Add(text);
        }

        foreach (var body in bodies)
        foreach (var node in body.DescendantNodes())
        {
            // Binary expression — classify on operator-token kind.
            if (node is BinaryExpressionSyntax bin)
            {
                var op = bin.OperatorToken.Kind();
                if (TokenOperators.Contains(op))
                {
                    TickOperator(op.ToString());
                }
                continue;
            }

            // Prefix unary
            if (node is PrefixUnaryExpressionSyntax pre)
            {
                var op = pre.OperatorToken.Kind();
                if (TokenOperators.Contains(op))
                {
                    TickOperator($"pre-{op}");
                }
                continue;
            }

            // Postfix unary
            if (node is PostfixUnaryExpressionSyntax post)
            {
                var op = post.OperatorToken.Kind();
                if (TokenOperators.Contains(op))
                {
                    TickOperator($"post-{op}");
                }
                continue;
            }

            // Assignment expression (covers `=`, `+=`, etc.)
            if (node is AssignmentExpressionSyntax assign)
            {
                var op = assign.OperatorToken.Kind();
                if (TokenOperators.Contains(op))
                {
                    TickOperator(op.ToString());
                }
                continue;
            }

            // Node-level operators.
            if (NodeOperators.Contains(node.Kind()))
            {
                TickOperator(node.Kind().ToString());
                continue;
            }

            // Identifier-name references count as operand occurrences.
            if (node is IdentifierNameSyntax id)
            {
                TickOperand(id.Identifier.Text);
                continue;
            }

            // Literal expressions count as operand occurrences. The
            // text of the literal is the operand identity (so two
            // distinct literals with the same kind but different
            // values produce different unique operands).
            if (node is LiteralExpressionSyntax lit
                && LiteralOperandKinds.Contains(lit.Kind()))
            {
                TickOperand(lit.Token.Text);
                continue;
            }

            // ThisExpression / BaseExpression are LiteralOperandKinds
            // but Roslyn models them as their own node types, not as
            // LiteralExpressionSyntax. Fold them in here.
            if (node is ThisExpressionSyntax)
            {
                TickOperand("this");
                continue;
            }
            if (node is BaseExpressionSyntax)
            {
                TickOperand("base");
                continue;
            }
        }

        return new HalsteadResult(
            operatorCount,
            operandCount,
            uniqueOperators.Count,
            uniqueOperands.Count);
    }
}
