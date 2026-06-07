/// <summary>
/// Per-TFM .NET Framework reference-assembly resolution (Fathom row
/// <c>dotnet-web-site-project-support</c> 5.0.73).
///
/// ASP.NET Web Site Projects have no `.csproj`, so MSBuildWorkspace can't
/// resolve their framework references. We resolve them the cross-platform way
/// MSBuild itself does on non-Windows: the
/// <c>Microsoft.NETFramework.ReferenceAssemblies.&lt;tfm&gt;</c> NuGet packs.
/// `ToolLocationHelper.GetPathToReferenceAssemblies` is Windows-registry-based
/// and returns nothing on macOS/Linux — verified — so it is NOT used here.
///
/// Each WSP resolves its OWN <c>TargetFrameworkMoniker</c> to its OWN pack
/// (e.g. `net48` ⇒ `…/microsoft.netframework.referenceassemblies.net48/&lt;ver&gt;/
/// build/.NETFramework/v4.8/`, which carries `System.Web.dll` et al.). When a
/// TFM's pack isn't restored on the host, this returns <c>null</c> and the
/// caller emits an observable `Limitation` — never a borrowed-dir fallback.
/// </summary>

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

static class FrameworkReferenceResolver
{
    // .NETFramework,Version=v4.8 → ("net48", "v4.8"); v4.7.2 → ("net472", "v4.7.2").
    static readonly Regex NetFrameworkTfm = new(
        @"^\.NETFramework,Version=v(?<v>\d+(?:\.\d+)+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Map a .NET Framework TargetFrameworkMoniker to its ReferenceAssemblies
    /// NuGet package id + the version directory inside it. Returns null for a
    /// non-.NETFramework TFM (e.g. `.NETCoreApp,Version=v8.0`) or unparsable input.
    /// </summary>
    public static (string PackageId, string VersionDir)? ParseNetFrameworkTfm(string? tfm)
    {
        if (string.IsNullOrWhiteSpace(tfm)) return null;
        var m = NetFrameworkTfm.Match(tfm.Trim());
        if (!m.Success) return null;
        var version = m.Groups["v"].Value;            // "4.8" / "4.7.2"
        var shortVer = version.Replace(".", "");       // "48"  / "472"
        return ($"microsoft.netframework.referenceassemblies.net{shortVer}", $"v{version}");
    }

    /// <summary>
    /// The NuGet global-packages directory: the <c>NUGET_PACKAGES</c> override
    /// if set, else the default <c>~/.nuget/packages</c>.
    /// </summary>
    public static string GlobalPackagesDir() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } env
            ? env
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

    /// <summary>
    /// Resolve the reference-assembly directory for a TFM under
    /// <paramref name="globalPackagesDir"/>, choosing the newest restored
    /// pack version. Returns null when the TFM isn't a resolvable .NET
    /// Framework moniker or its pack isn't restored on the host (→ caller
    /// emits a Limitation; no fallback).
    /// </summary>
    public static string? ResolveReferenceAssemblyDir(string? tfm, string globalPackagesDir)
    {
        var parsed = ParseNetFrameworkTfm(tfm);
        if (parsed == null) return null;
        var (packageId, versionDir) = parsed.Value;

        var packRoot = Path.Combine(globalPackagesDir, packageId);
        if (!Directory.Exists(packRoot)) return null;

        // Newest pack version first (string-descending is fine for the
        // simple "1.0.3" SemVer these packs use).
        foreach (var versioned in Directory.GetDirectories(packRoot).OrderByDescending(d => d))
        {
            var dir = Path.Combine(versioned, "build", ".NETFramework", versionDir);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>
    /// All reference-assembly DLL paths in a resolved framework directory.
    /// </summary>
    public static string[] ReferenceAssemblyDlls(string referenceAssemblyDir) =>
        Directory.Exists(referenceAssemblyDir)
            ? Directory.GetFiles(referenceAssemblyDir, "*.dll")
            : Array.Empty<string>();

    // .NET Framework reference-assembly NuGet pack id prefix.
    const string RefAssembliesPackPrefix = "microsoft.netframework.referenceassemblies.net";

    /// <summary>
    /// Discover every restored `Microsoft.NETFramework.ReferenceAssemblies.netXX`
    /// pack under <paramref name="globalPackagesDir"/> and return its per-TFM
    /// reference-assembly directory as a (versionDir, absolutePath) pair —
    /// e.g. ("v4.8", "…/net48/1.0.3/build/.NETFramework/v4.8"). Newest pack
    /// version per TFM; deduped by versionDir. Pure (no side effects) — backs
    /// {@link BuildCombinedReferenceAssemblyRoot} and is independently testable.
    ///
    /// Fathom row dotnet-l0-external-symbol-resolution-residual (5.0.76.a).
    /// </summary>
    public static IReadOnlyList<(string VersionDir, string Path)> DiscoverReferenceAssemblyDirs(
        string globalPackagesDir)
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(globalPackagesDir)) return result;
        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packDir in Directory.GetDirectories(globalPackagesDir))
        {
            var packName = System.IO.Path.GetFileName(packDir);
            if (!packName.StartsWith(RefAssembliesPackPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            // Newest restored pack version first.
            foreach (var versioned in Directory.GetDirectories(packDir).OrderByDescending(d => d))
            {
                var netFxDir = System.IO.Path.Combine(versioned, "build", ".NETFramework");
                if (!Directory.Exists(netFxDir)) continue;
                // Each pack carries exactly one `vX.X` reference-assembly dir.
                var versionDirs = Directory.GetDirectories(netFxDir);
                if (versionDirs.Length == 0) continue;
                var vDir = versionDirs[0];
                var versionName = System.IO.Path.GetFileName(vDir);
                if (seenVersions.Add(versionName)) result.Add((versionName, vDir));
                break; // newest version of this pack only
            }
        }
        return result;
    }

    /// <summary>
    /// Build a single combined reference-assembly ROOT that MSBuild's
    /// `ResolveAssemblyReference` task can use via the `TargetFrameworkRootPath`
    /// property to resolve BARE framework references (`&lt;Reference
    /// Include="System.Data" /&gt;`) in OLD-STYLE (non-SDK) .NET Framework
    /// csprojs on macOS/Linux — where there is no GAC and old-style projects
    /// don't auto-import the ReferenceAssemblies pack. RAR resolves
    /// `&lt;root&gt;/.NETFramework/&lt;projectTFM&gt;/&lt;assembly&gt;.dll`, so the
    /// root holds one `.NETFramework/vX.X` entry per restored pack (symlinked to
    /// the pack's own dir), letting a single workspace-global property serve
    /// projects of any TFM.
    ///
    /// Returns the combined root path, or null when: on Windows (the GAC /
    /// on-disk Reference Assemblies resolve bare refs natively — no override
    /// needed); no packs are restored (caller's analysis stays references-free,
    /// flagged by the 5.0.72 Limitation — NSD: observable, never a borrowed-dir
    /// guess); or the root couldn't be assembled (IO/permission failure → fall
    /// back to default resolution). Per-process temp dir, rebuilt each call.
    ///
    /// Fathom row dotnet-l0-external-symbol-resolution-residual (5.0.76.a).
    /// This is the MS-aligned mechanism: the same `TargetFrameworkRootPath`
    /// the ReferenceAssemblies pack's own targets set for SDK-style projects.
    /// </summary>
    public static string? BuildCombinedReferenceAssemblyRoot(string globalPackagesDir)
    {
        // Windows resolves bare framework references from the GAC / on-disk
        // Reference Assemblies; overriding the root there is unnecessary and
        // would require symlink privileges. The fix targets macOS/Linux.
        if (OperatingSystem.IsWindows()) return null;

        var dirs = DiscoverReferenceAssemblyDirs(globalPackagesDir);
        if (dirs.Count == 0) return null;

        try
        {
            // Per-process AND per-packages-dir, so two distinct packages dirs in
            // one process (e.g. parallel tests: a synthetic cache vs the real one)
            // don't collide on the same dir's delete/rebuild. Production calls this
            // once per analyzer process.
            var packagesToken = Math.Abs(
                System.IO.Path.GetFullPath(globalPackagesDir).GetHashCode()).ToString();
            var combinedRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"fathom-dotnet-refasm-{Environment.ProcessId}-{packagesToken}");
            var netFx = System.IO.Path.Combine(combinedRoot, ".NETFramework");
            // Rebuild fresh each call so a removed/updated pack can't leave a
            // stale symlink behind.
            if (Directory.Exists(combinedRoot)) Directory.Delete(combinedRoot, recursive: true);
            Directory.CreateDirectory(netFx);
            foreach (var (versionDir, path) in dirs)
            {
                var link = System.IO.Path.Combine(netFx, versionDir);
                Directory.CreateSymbolicLink(link, path);
            }
            return combinedRoot;
        }
        catch
        {
            // IO / permission failure — fall back to default resolution (the
            // references-free Limitation still surfaces the degradation).
            return null;
        }
    }
}
