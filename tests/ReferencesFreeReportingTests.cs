/// <summary>
/// Tests for references-free analysis observability (Fathom row
/// <c>dotnet-references-free-analysis-loud</c> 5.0.72). A file analyzed without
/// project references degrades every external-symbol fact; that MUST surface as
/// a per-file Limitation + a proportional run-level problem, never silently.
///
/// Coverage per `feedback_test_fixture_pattern_catalog`:
///   - per-file Limitation shape (kind / severity / location / metadata)
///   - summary: none references-free → no problem
///   - summary: partial fallback (projects loaded) → WARNING + names the tier
///   - summary: 0 projects loaded → ERROR
///   - summary: MSBuild unavailable → ERROR
/// </summary>

namespace NodegraphAnalyzerDotnet.Tests;

public class ReferencesFreeReportingTests
{
    [Fact]
    public void Limitation_carries_kind_significant_severity_and_file()
    {
        var lim = ReferencesFreeReporting.BuildLimitation("/repo/EnvisionAnywhere.com/Portal/Foo.aspx.cs");

        Assert.Equal("references-free-compilation", lim["kind"]);
        Assert.Equal("significant", lim["severity"]);
        var loc = Assert.IsType<Dictionary<string, object?>>(lim["location"]);
        Assert.Equal("/repo/EnvisionAnywhere.com/Portal/Foo.aspx.cs", loc["file"]);
        var meta = Assert.IsType<Dictionary<string, object?>>(lim["metadata"]);
        Assert.Equal("shared-references-free", meta["compilation"]);
    }

    [Fact]
    public void Summary_is_null_when_nothing_references_free()
    {
        var problem = ReferencesFreeReporting.BuildSummaryProblem(
            totalCs: 100, referencesFreeCount: 0, projectsLoaded: 3,
            msbuildAvailable: true, referencesFreeFiles: Array.Empty<string>(), repoRoot: "/repo");

        Assert.Null(problem);
    }

    [Fact]
    public void Summary_partial_fallback_is_warning_and_names_the_tier()
    {
        var files = new[]
        {
            "/repo/EnvisionAnywhere.com/Portal/A.aspx.cs",
            "/repo/EnvisionAnywhere.com/App_Code/B.cs",
            "/repo/EnvisionAnywhere.com/api/C.aspx.cs",
            "/repo/Misc/D.cs",
        };
        var problem = ReferencesFreeReporting.BuildSummaryProblem(
            totalCs: 1864, referencesFreeCount: files.Length, projectsLoaded: 3,
            msbuildAvailable: true, referencesFreeFiles: files, repoRoot: "/repo");

        Assert.NotNull(problem);
        Assert.Equal("warning", problem!["severity"]);
        var msg = (string)problem["message"]!;
        Assert.Contains("4 of 1864", msg);
        // Groups by top-level tier and reports the biggest (3 under EnvisionAnywhere.com).
        Assert.Contains("EnvisionAnywhere.com (3)", msg);
    }

    [Fact]
    public void Summary_zero_projects_loaded_is_error()
    {
        var problem = ReferencesFreeReporting.BuildSummaryProblem(
            totalCs: 50, referencesFreeCount: 50, projectsLoaded: 0,
            msbuildAvailable: true, referencesFreeFiles: new[] { "/repo/src/A.cs" }, repoRoot: "/repo");

        Assert.NotNull(problem);
        Assert.Equal("error", problem!["severity"]);
        Assert.Contains("UNRELIABLE", (string)problem["message"]!);
        Assert.Contains("0 projects loaded", (string)problem["message"]!);
    }

    [Fact]
    public void Summary_msbuild_unavailable_is_error()
    {
        var problem = ReferencesFreeReporting.BuildSummaryProblem(
            totalCs: 50, referencesFreeCount: 50, projectsLoaded: 0,
            msbuildAvailable: false, referencesFreeFiles: new[] { "/repo/src/A.cs" }, repoRoot: "/repo");

        Assert.NotNull(problem);
        Assert.Equal("error", problem!["severity"]);
        Assert.Contains("MSBuild unavailable", (string)problem["message"]!);
    }
}
