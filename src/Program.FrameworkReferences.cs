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
}
