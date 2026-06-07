/// <summary>
/// Unit tests for build-excluded `.cs` detection (Fathom row
/// <c>dotnet-csproj-compile-set-coverage</c> 5.0.74).
/// </summary>

namespace NodegraphAnalyzerDotnet.Tests;

public class CompileSetCoverageTests
{
    private static readonly string Sep = System.IO.Path.DirectorySeparatorChar.ToString();

    [Fact]
    public void IsUnderAnyProjectDir_true_when_under_a_dir()
    {
        var dirs = new[] { $"{Sep}repo{Sep}ReportLib{Sep}", $"{Sep}repo{Sep}CloudCore{Sep}" };
        Assert.True(CompileSetCoverage.IsUnderAnyProjectDir($"{Sep}repo{Sep}ReportLib{Sep}sub{Sep}Foo.cs", dirs));
        Assert.True(CompileSetCoverage.IsUnderAnyProjectDir($"{Sep}repo{Sep}CloudCore{Sep}Bar.cs", dirs));
    }

    [Fact]
    public void IsUnderAnyProjectDir_false_for_sibling_prefix_collision()
    {
        // Trailing separator prevents `/repo/ReportLib/` from matching
        // `/repo/ReportLibTests/...`.
        var dirs = new[] { $"{Sep}repo{Sep}ReportLib{Sep}" };
        Assert.False(CompileSetCoverage.IsUnderAnyProjectDir($"{Sep}repo{Sep}ReportLibTests{Sep}Foo.cs", dirs));
    }

    [Fact]
    public void IsUnderAnyProjectDir_false_when_not_under_any_or_no_dirs()
    {
        var dirs = new[] { $"{Sep}repo{Sep}ReportLib{Sep}" };
        Assert.False(CompileSetCoverage.IsUnderAnyProjectDir($"{Sep}elsewhere{Sep}Orphan.cs", dirs));
        Assert.False(CompileSetCoverage.IsUnderAnyProjectDir($"{Sep}repo{Sep}ReportLib{Sep}Foo.cs", System.Array.Empty<string>()));
    }

    [Fact]
    public void BuildSummaryProblem_null_when_none_excluded()
    {
        Assert.Null(CompileSetCoverage.BuildSummaryProblem(100, System.Array.Empty<string>(), $"{Sep}repo"));
    }

    [Fact]
    public void BuildSummaryProblem_warning_names_largest_roots()
    {
        var root = $"{Sep}repo";
        var excluded = new[]
        {
            $"{Sep}repo{Sep}ReportLib{Sep}A.Designer.cs",
            $"{Sep}repo{Sep}ReportLib{Sep}B.Designer.cs",
            $"{Sep}repo{Sep}CloudCore{Sep}C.cs",
        };
        var p = CompileSetCoverage.BuildSummaryProblem(500, excluded, root);
        Assert.NotNull(p);
        Assert.Equal("warning", p!["severity"]);
        var msg = (string)p["message"]!;
        Assert.Contains("3 of 500", msg);
        Assert.Contains("EXCLUDED from analysis", msg);
        Assert.Contains("<Compile>", msg);
        Assert.Contains("ReportLib (2)", msg); // largest root first
    }
}
