/// <summary>
/// ASP.NET Web Site Project (WSP) detection from a `.sln` (Fathom row
/// <c>dotnet-web-site-project-support</c> 5.0.73).
///
/// WSPs have no `.csproj` by design — Roslyn's MSBuildWorkspace can't load
/// them — so they fall to the references-free `sharedCompilation` and every
/// external-symbol fact degrades (row 5.0.72 made that loud). To analyze them
/// with real references we resolve their DECLARED references (not bin-globbing),
/// exactly as Visual Studio's web-site project system does. The authoritative
/// declaration is the `.sln` entry: a project-type GUID
/// <c>{E24C65DC-7377-472B-9ABA-BC803B73C61A}</c> with a
/// <c>ProjectSection(WebsiteProperties)</c> carrying the physical path,
/// <c>TargetFrameworkMoniker</c>, and <c>ProjectReferences</c>.
///
/// This file owns the PARSE (deterministic, fully unit-tested). Reference
/// resolution + Compilation construction layer on top.
/// </summary>

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>One resolved sibling project reference of a WSP (`{guid}|Name.dll`).</summary>
record WebSiteProjectReference(string ProjectGuid, string OutputName);

/// <summary>A Web Site Project declared in a `.sln`.</summary>
record WebSiteProject(
    string Name,
    string PhysicalPath,
    string? TargetFrameworkMoniker,
    IReadOnlyList<WebSiteProjectReference> ProjectReferences);

static class WebSiteProjectParser
{
    /// <summary>The ASP.NET Web Site Project type GUID (case-insensitive).</summary>
    public const string WebSiteProjectTypeGuid = "E24C65DC-7377-472B-9ABA-BC803B73C61A";

    // Project("{type-guid}") = "Name", "RelativePath", "{project-guid}"
    static readonly Regex ProjectHeader = new(
        @"Project\(""\{(?<type>[0-9A-Fa-f-]+)\}""\)\s*=\s*""(?<name>[^""]*)""\s*,\s*""(?<path>[^""]*)""\s*,\s*""\{(?<guid>[0-9A-Fa-f-]+)\}""",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse all Web Site Project entries from `.sln` content. Paths are
    /// resolved to absolute against <paramref name="slnDir"/> (the directory
    /// containing the `.sln`). Non-WSP projects (regular `.csproj`, solution
    /// folders) are ignored.
    /// </summary>
    public static IReadOnlyList<WebSiteProject> Parse(string slnContent, string slnDir)
    {
        var result = new List<WebSiteProject>();
        // Normalize line endings; the .sln body is line-oriented.
        var lines = slnContent.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var header = ProjectHeader.Match(lines[i]);
            if (!header.Success) continue;
            if (!header.Groups["type"].Value.Equals(WebSiteProjectTypeGuid, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var name = header.Groups["name"].Value;
            // Header path is the WSP folder (often with a trailing backslash).
            var declaredPath = header.Groups["path"].Value;
            string? tfm = null;
            string? physicalPath = null;
            var projectRefs = new List<WebSiteProjectReference>();

            // Scan the ProjectSection(WebsiteProperties) block until EndProject.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (line.StartsWith("EndProject")) break;

                var tfmM = Regex.Match(line, @"^TargetFrameworkMoniker\s*=\s*""?(?<v>[^""]+)""?");
                if (tfmM.Success) { tfm = DecodeSlnValue(tfmM.Groups["v"].Value); continue; }

                var ppM = Regex.Match(line, @"AspNetCompiler\.PhysicalPath\s*=\s*""(?<v>[^""]*)""");
                if (ppM.Success && physicalPath == null) { physicalPath = ppM.Groups["v"].Value; continue; }

                var prM = Regex.Match(line, @"^ProjectReferences\s*=\s*""(?<v>[^""]*)""");
                if (prM.Success) { projectRefs.AddRange(ParseProjectReferences(prM.Groups["v"].Value)); continue; }
            }

            var rawPath = physicalPath ?? declaredPath;
            var absPath = Path.GetFullPath(Path.Combine(slnDir, rawPath.Replace('\\', Path.DirectorySeparatorChar)));
            // WSP folder paths carry a trailing separator in the .sln; normalize
            // it off so downstream Path.Combine / prefix checks behave.
            var trimmed = absPath.TrimEnd(Path.DirectorySeparatorChar, '/');
            if (trimmed.Length > 0) absPath = trimmed;
            result.Add(new WebSiteProject(name, absPath, tfm, projectRefs));
        }

        return result;
    }

    /// <summary>
    /// Parse a WSP `ProjectReferences` value: `{guid}|Output.dll;{guid2}|Other.dll;`.
    /// </summary>
    public static IReadOnlyList<WebSiteProjectReference> ParseProjectReferences(string value)
    {
        var refs = new List<WebSiteProjectReference>();
        foreach (var entry in value.Split(';'))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;
            var bar = trimmed.IndexOf('|');
            if (bar <= 0) continue;
            var guid = trimmed.Substring(0, bar).Trim().Trim('{', '}');
            var output = trimmed.Substring(bar + 1).Trim();
            if (guid.Length > 0 && output.Length > 0)
                refs.Add(new WebSiteProjectReference(guid, output));
        }
        return refs;
    }

    /// <summary>
    /// `.sln` values URL-encode a few characters (notably `%3D` for `=` in the
    /// TargetFrameworkMoniker `.NETFramework,Version%3Dv4.7.2`). Decode them.
    /// </summary>
    static string DecodeSlnValue(string v) =>
        v.Replace("%3D", "=").Replace("%3d", "=");
}
