/// <summary>
/// Structural analysis for .csproj (MSBuild project) and .sln (solution)
/// files. Emits one artifact per file with a minimal element + edge set:
///
///   .csproj artifact (language: "csproj"):
///     - One element representing the project itself (kind "project").
///       Metadata: targetFramework(s), outputType, sdk, packageReferences
///       (count), projectReferences (count).
///     - Artifact-level edges:
///         project `references` (subtype "project") → resolved absolute
///           path of each <ProjectReference Include="..."/>
///         project `references` (subtype "package") → "<package>@<version>"
///           pseudo-target for each <PackageReference Include=".."/>
///
///   .sln artifact (language: "sln"):
///     - One element representing the solution (kind "solution").
///       Metadata: projectCount, formatVersion (when present).
///     - Artifact-level edges:
///         solution `contains` → resolved absolute path of each project
///           file referenced in the .sln's `Project(...)` lines.
///
/// Cross-artifact edges resolve via the substrate's dangling-edge
/// mechanism: when a .csproj artifact lands and a .sln has already been
/// processed (or will be), the targetRef path matches and the substrate
/// links them automatically.
/// </summary>

using System.Text.RegularExpressions;
using System.Xml.Linq;

static class ProjectFileHelpers
{
    public static object? BuildCsprojArtifact(string content, string filePath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = filePath,
                ["filePath"] = filePath,
                ["language"] = "csproj",
                ["contentHash"] = ComputeHash(content),
                ["elements"] = Array.Empty<object>(),
                ["problems"] = new[] { new { message = $"Failed to parse .csproj XML: {ex.Message}" } },
            };
        }

        var root = doc.Root;
        var sdk = root?.Attribute("Sdk")?.Value;

        // <TargetFramework> or <TargetFrameworks> (multi-target). MSBuild
        // doesn't namespace project files (Sdk-style projects), so look
        // by local-name and ignore namespace.
        var targetFrameworks = new List<string>();
        if (root != null)
        {
            foreach (var el in root.Descendants().Where(e => e.Name.LocalName == "TargetFramework"))
            {
                var v = el.Value.Trim();
                if (!string.IsNullOrEmpty(v)) targetFrameworks.Add(v);
            }
            foreach (var el in root.Descendants().Where(e => e.Name.LocalName == "TargetFrameworks"))
            {
                foreach (var part in el.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    targetFrameworks.Add(part);
            }
        }

        var outputType = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value.Trim();

        // PackageReferences. Both Include="X" Version="Y" attribute form
        // and the rarer <Version>Y</Version> child-element form.
        var packageRefs = new List<(string name, string? version)>();
        if (root != null)
        {
            foreach (var el in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var name = el.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                var version = el.Attribute("Version")?.Value
                    ?? el.Descendants().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value;
                packageRefs.Add((name, version));
            }
        }

        // ProjectReferences. Resolve relative paths against the .csproj's
        // own directory so the resulting targetRef matches the absolute
        // path we'd emit for the referenced .csproj's own artifact.
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var projectRefs = new List<string>();
        if (root != null)
        {
            foreach (var el in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var rel = el.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(rel)) continue;
                // .csproj paths use Windows-style backslashes; normalize.
                var normalized = rel.Replace('\\', '/');
                var resolved = Path.GetFullPath(Path.Combine(projectDir, normalized));
                projectRefs.Add(resolved);
            }
        }

        var projectName = Path.GetFileNameWithoutExtension(filePath);
        var elementName = projectName;

        var elementMetadata = new Dictionary<string, object?>();
        if (sdk != null) elementMetadata["sdk"] = sdk;
        if (targetFrameworks.Count > 0) elementMetadata["targetFrameworks"] = targetFrameworks.ToArray();
        if (outputType != null) elementMetadata["outputType"] = outputType;
        if (packageRefs.Count > 0) elementMetadata["packageReferenceCount"] = packageRefs.Count;
        if (projectRefs.Count > 0) elementMetadata["projectReferenceCount"] = projectRefs.Count;

        var element = new Dictionary<string, object?>
        {
            ["name"] = elementName,
            ["kind"] = "project",
        };
        if (elementMetadata.Count > 0) element["metadata"] = elementMetadata;

        // Artifact-level edges: source is the project element name; the
        // dotnet engine's edge resolver matches sourceName against
        // declared element names within this artifact.
        var artifactEdges = new List<object>();
        foreach (var (pkgName, pkgVersion) in packageRefs)
        {
            var pseudoTarget = pkgVersion != null ? $"{pkgName}@{pkgVersion}" : pkgName;
            artifactEdges.Add(new
            {
                sourceName = elementName,
                type = "references",
                subtype = "package",
                targetName = pseudoTarget,
            });
        }
        foreach (var refPath in projectRefs)
        {
            // Cross-artifact edge: the referenced .csproj produces an
            // artifact whose `id`/`filePath` is its absolute path. The
            // substrate matches by that path when the target artifact
            // also lands in the same orchestrate run.
            artifactEdges.Add(new
            {
                sourceName = elementName,
                type = "references",
                subtype = "project",
                targetName = refPath,
            });
        }

        var artifact = new Dictionary<string, object?>
        {
            ["id"] = filePath,
            ["filePath"] = filePath,
            ["language"] = "csproj",
            ["contentHash"] = ComputeHash(content),
            ["elements"] = new[] { element },
        };
        if (artifactEdges.Count > 0) artifact["edges"] = artifactEdges.ToArray();
        return artifact;
    }

    // .sln Project(...) line:
    //   Project("{TYPE-GUID}") = "Name", "Path\To\Project.csproj", "{PROJECT-GUID}"
    // We only care about column 2 (the project file path).
    private static readonly Regex SlnProjectLine = new(
        @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]+""\s*,\s*""([^""]+)""\s*,\s*""\{[^}]+\}""\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SlnFormatVersion = new(
        @"^Microsoft Visual Studio Solution File, Format Version\s+(\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static object? BuildSlnArtifact(string content, string filePath)
    {
        var slnDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;

        var projectPaths = new List<string>();
        foreach (Match m in SlnProjectLine.Matches(content))
        {
            var rel = m.Groups[1].Value.Replace('\\', '/');
            // Solution-folder entries reference themselves (no .csproj
            // extension); only emit `contains` edges to actual project
            // files we know how to analyze.
            var ext = Path.GetExtension(rel);
            if (string.IsNullOrEmpty(ext)) continue;
            var resolved = Path.GetFullPath(Path.Combine(slnDir, rel));
            projectPaths.Add(resolved);
        }

        var formatVersion = SlnFormatVersion.Match(content).Groups[1].Value;
        var solutionName = Path.GetFileNameWithoutExtension(filePath);

        var elementMetadata = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(formatVersion)) elementMetadata["formatVersion"] = formatVersion;
        if (projectPaths.Count > 0) elementMetadata["projectCount"] = projectPaths.Count;

        var element = new Dictionary<string, object?>
        {
            ["name"] = solutionName,
            ["kind"] = "solution",
        };
        if (elementMetadata.Count > 0) element["metadata"] = elementMetadata;

        var artifactEdges = new List<object>();
        foreach (var refPath in projectPaths)
        {
            artifactEdges.Add(new
            {
                sourceName = solutionName,
                type = "contains",
                targetName = refPath,
            });
        }

        var artifact = new Dictionary<string, object?>
        {
            ["id"] = filePath,
            ["filePath"] = filePath,
            ["language"] = "sln",
            ["contentHash"] = ComputeHash(content),
            ["elements"] = new[] { element },
        };
        if (artifactEdges.Count > 0) artifact["edges"] = artifactEdges.ToArray();
        return artifact;
    }

    private static string ComputeHash(string content)
    {
        // Mirror Program.cs's ComputeHash — SHA-256 hex over UTF-8 bytes.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
