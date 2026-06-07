/// <summary>
/// Build-excluded `.cs` detection (Fathom row
/// <c>dotnet-csproj-compile-set-coverage</c> 5.0.74).
///
/// The analyzer discovers `.cs` files by walking the source tree. A file that
/// sits inside a SUCCESSFULLY-LOADED project's directory but is NOT in that
/// project's compiled document set (not in the csproj's <c>&lt;Compile&gt;</c>
/// items) is BUILD-EXCLUDED: the build doesn't compile it on ANY platform —
/// dead generated output (a typed-DataSet `.Designer.cs` whose `.xsd` was
/// dropped), files under an excluded subfolder, leftovers. Analyzing them via
/// the references-free fallback both pollutes the corpus with non-built code and
/// reports unresolved-symbol noise.
///
/// These are DISTINCT from a true orphan (a `.cs` under no loaded project at
/// all — handled by the references-free path 5.0.72): a build-excluded file IS
/// owned by a loaded project's tree, the project just doesn't compile it. We
/// skip build-excluded files from analysis and emit ONE proportional warning
/// (no-silent-degradation: the exclusion is observable, naming the largest
/// roots), never dropping them silently.
///
/// Generated-at-build files whose source IS in the csproj (e.g. an `.xsd`/`.tt`
/// with a generator) are part of the build and already appear in the project's
/// Documents — so they are NOT build-excluded and stay analyzed.
/// </summary>

using System.Collections.Generic;
using System.Linq;

static class CompileSetCoverage
{
    /// <summary>
    /// True iff <paramref name="filePath"/> sits under one of the loaded
    /// project directories (each passed WITH a trailing separator so a prefix
    /// can't false-match a sibling dir). Ordinal compare matches the
    /// case-sensitive macOS/Linux filesystem the rest of the analyzer assumes.
    /// </summary>
    public static bool IsUnderAnyProjectDir(string filePath, IReadOnlyCollection<string> projectDirsWithSep)
    {
        foreach (var dir in projectDirsWithSep)
        {
            if (filePath.StartsWith(dir, System.StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Run-level warning naming the build-excluded files, or <c>null</c> when
    /// none. Always a <c>warning</c> (the build excludes these by design; it is
    /// not the unreliable-semantics situation references-free `error` covers) —
    /// names the largest roots so the operator sees WHICH tier the dead code is
    /// in (e.g. a `ReportLib` typed-DataSet) rather than a flat count.
    /// </summary>
    public static Dictionary<string, object?>? BuildSummaryProblem(
        int totalCs,
        IReadOnlyCollection<string> buildExcludedFiles,
        string repoRoot)
    {
        if (buildExcludedFiles.Count == 0) return null;

        var topRoots = buildExcludedFiles
            .Select(f => ReferencesFreeReporting.TopRoot(f, repoRoot))
            .Where(r => r.Length > 0)
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();
        var rootsText = topRoots.Count > 0
            ? $" Largest roots: {string.Join(", ", topRoots)}."
            : "";

        return new Dictionary<string, object?>
        {
            ["severity"] = "warning",
            ["message"] = $".NET analyzer: {buildExcludedFiles.Count} of {totalCs} .cs file(s) EXCLUDED from "
                + "analysis — in a loaded project's directory but NOT in its <Compile> set, so the build does "
                + "not compile them on any platform (dead/generated/excluded code: e.g. an orphaned typed-DataSet "
                + $".Designer.cs whose .xsd was removed).{rootsText} They are omitted from the corpus rather than "
                + "analyzed references-free.",
        };
    }
}
