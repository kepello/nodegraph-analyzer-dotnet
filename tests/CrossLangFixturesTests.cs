/**
 * Cross-language metric parity fixtures.
 *
 * These fixtures verify that .NET analyzer produces expected metric values
 * for a shared set of simple programs. The same fixtures exist for TypeScript and Swift,
 * allowing cross-language comparison of cyclomatic, cognitive, and Halstead metrics.
 */

using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace NodegraphAnalyzerDotnet.Tests;

public class CrossLangFixturesTests
{
    private static readonly string FixturesDir = Path.GetFullPath(
        Path.Combine(
            Path.GetDirectoryName(typeof(CrossLangFixturesTests).Assembly.Location)!,
            "../../../../../fathom-test-fixtures/cross-lang-metrics"
        )
    );

    private record ExpectedMetrics(
        CyclomaticMetrics Cyclomatic,
        CognitiveMetrics Cognitive,
        HalsteadMetrics Halstead
    );

    private record CyclomaticMetrics(int BranchCount);
    private record CognitiveMetrics(int SonarBranchCount, int SonarNestingDepthSum, string? Notes);
    private record HalsteadMetrics(
        int Operators,
        int UniqueOperators,
        int Operands,
        int UniqueOperands
    );

    private static (string Code, ExpectedMetrics Expected) LoadFixture(string name)
    {
        var code = File.ReadAllText(Path.Combine(FixturesDir, "dotnet", $"{name}.cs"));
        var expectedJson = File.ReadAllText(
            Path.Combine(FixturesDir, "dotnet", $"{name}.expected.json")
        );
        var expected = JsonSerializer.Deserialize<ExpectedMetrics>(
            expectedJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        )!;
        return (code, expected);
    }

    private static (
        CyclomaticResult Cyclomatic,
        CognitiveResult Cognitive,
        HalsteadResult Halstead
    ) AnalyzeFixture(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First();

        var cyclomatic = CyclomaticHelpers.CountBranches(method.Body!);
        var cognitive = CognitiveHelpers.Extract(method.Body!);
        var halstead = HalsteadHelpers.Extract(method.Body!);

        return (
            new CyclomaticResult(cyclomatic),
            new CognitiveResult(cognitive.SonarBranchCount, cognitive.SonarNestingDepthSum),
            new HalsteadResult(
                halstead.OperatorCount,
                halstead.UniqueOperators,
                halstead.OperandCount,
                halstead.UniqueOperands
            )
        );
    }

    private record CyclomaticResult(int BranchCount);
    private record CognitiveResult(int SonarBranchCount, int SonarNestingDepthSum);
    private record HalsteadResult(
        int Operators,
        int UniqueOperators,
        int Operands,
        int UniqueOperands
    );

    [Theory]
    [InlineData("01-empty")]
    [InlineData("02-simple-if")]
    [InlineData("03-nested-if")]
    [InlineData("04-logical-chain")]
    [InlineData("05-string-literals")]
    public void CrossLangFixture(string name)
    {
        var (code, expected) = LoadFixture(name);
        var actual = AnalyzeFixture(code);

        // Cyclomatic
        Assert.Equal(expected.Cyclomatic.BranchCount, actual.Cyclomatic.BranchCount);

        // Cognitive
        Assert.Equal(expected.Cognitive.SonarBranchCount, actual.Cognitive.SonarBranchCount);
        Assert.Equal(
            expected.Cognitive.SonarNestingDepthSum,
            actual.Cognitive.SonarNestingDepthSum
        );

        // Halstead
        Assert.Equal(expected.Halstead.Operators, actual.Halstead.Operators);
        Assert.Equal(expected.Halstead.UniqueOperators, actual.Halstead.UniqueOperators);
        Assert.Equal(expected.Halstead.Operands, actual.Halstead.Operands);
        Assert.Equal(expected.Halstead.UniqueOperands, actual.Halstead.UniqueOperands);
    }
}
