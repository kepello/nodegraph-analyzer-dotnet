/// <summary>
/// References-free analysis observability (Fathom row
/// <c>dotnet-references-free-analysis-loud</c> 5.0.72).
///
/// When a <c>.cs</c> file isn't owned by any loaded <c>.csproj</c> it falls
/// to the System-runtime-only <c>sharedCompilation</c> — orphan files, an
/// ASP.NET Web Site Project (no project file by design), a <c>.csproj</c>
/// that failed to load, or a host without MSBuild. Every external-symbol fact
/// then degrades: framework base types (the <c>interfacer</c> signal),
/// external call resolution, and overrides go missing or unresolved.
///
/// That degradation MUST be observable, never silent (the no-silent-degradation
/// standard): each references-free file carries a per-element
/// <c>Limitation</c>, and the run emits ONE proportional summary problem so an
/// operator can see — without reading stderr — that a whole tier of the
/// analysis is suspect, and which tier.
/// </summary>

using System.Collections.Generic;
using System.IO;
using System.Linq;

static class ReferencesFreeReporting
{
    public const string LimitationKind = "references-free-compilation";

    /// <summary>
    /// Group-J <c>Limitation</c> attached to every element of a file analyzed
    /// without project references, so downstream consumers can filter or flag
    /// the degraded elements rather than trust them silently.
    /// </summary>
    public static Dictionary<string, object?> BuildLimitation(string filePath) => new()
    {
        ["kind"] = LimitationKind,
        ["severity"] = "significant",
        ["location"] = new Dictionary<string, object?>
        {
            ["file"] = filePath,
            ["startLine"] = 1,
            ["endLine"] = 1,
        },
        ["description"] = "Analyzed without project references (no .csproj owns this file — orphan, "
            + "ASP.NET Web Site Project, or a project that failed to load). External-symbol facts are "
            + "degraded: framework base types (interfacer), external call resolution, and overrides may "
            + "be missing or unresolved. Load the owning project / provide references for accurate "
            + "semantic analysis.",
        ["metadata"] = new Dictionary<string, object?>
        {
            ["compilation"] = "shared-references-free",
        },
    };

    /// <summary>
    /// Build the run-level summary problem, or <c>null</c> when nothing was
    /// references-free.
    ///
    /// Severity policy (operator-tunable): <c>error</c> when the analysis is
    /// WHOLLY references-free — MSBuild unavailable, or 0 projects loaded while
    /// <c>.cs</c> files exist — because the semantic layer is then
    /// fundamentally unreliable; <c>warning</c> otherwise, naming the largest
    /// references-free roots so the operator sees WHICH tier is blind (e.g. an
    /// entire Web Site Project) rather than a flat count.
    /// </summary>
    public static Dictionary<string, object?>? BuildSummaryProblem(
        int totalCs,
        int referencesFreeCount,
        int projectsLoaded,
        bool msbuildAvailable,
        IEnumerable<string> referencesFreeFiles,
        string repoRoot)
    {
        if (referencesFreeCount == 0) return null;

        var whollyDegraded = !msbuildAvailable || projectsLoaded == 0;
        var severity = whollyDegraded ? "error" : "warning";

        var topRoots = referencesFreeFiles
            .Select(f => TopRoot(f, repoRoot))
            .Where(r => r.Length > 0)
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();
        var rootsText = topRoots.Count > 0
            ? $" Largest references-free roots: {string.Join(", ", topRoots)}."
            : "";

        var message = whollyDegraded
            ? $".NET analyzer: ALL {referencesFreeCount} of {totalCs} .cs file(s) analyzed WITHOUT project "
                + $"references ({(msbuildAvailable ? "0 projects loaded" : "MSBuild unavailable")}) — semantic "
                + $"analysis is UNRELIABLE: framework base types (interfacer), call resolution, and overrides "
                + $"are degraded.{rootsText}"
            : $".NET analyzer: {referencesFreeCount} of {totalCs} .cs file(s) analyzed WITHOUT project references "
                + $"(references-free compilation) — external-symbol facts degraded for those files (base "
                + $"types/interfacer, external calls, overrides).{rootsText} Provide project references (a "
                + $".csproj, or Web Site Project support) for accurate resolution.";

        return new Dictionary<string, object?>
        {
            ["severity"] = severity,
            ["message"] = message,
        };
    }

    /// <summary>
    /// The first path segment of <paramref name="filePath"/> beneath
    /// <paramref name="repoRoot"/> — the "tier" a references-free file belongs
    /// to (e.g. <c>EnvisionAnywhere.com</c>). Empty when the file sits directly
    /// in the root or isn't under it.
    /// </summary>
    public static string TopRoot(string filePath, string repoRoot)
    {
        string rel;
        try { rel = Path.GetRelativePath(repoRoot, filePath); }
        catch { return ""; }
        if (rel.StartsWith("..")) return "";
        var first = rel.Split('/', '\\').FirstOrDefault(s => s.Length > 0) ?? "";
        // A file directly in the root has no tier segment (its only segment is
        // the filename) — don't report the filename as a "root".
        return first.Equals(Path.GetFileName(filePath)) ? "" : first;
    }
}
