/// <summary>
/// Builds a referenced `Compilation` for an ASP.NET Web Site Project (Fathom
/// row <c>dotnet-web-site-project-support</c> 5.0.73), so its `App_Code` +
/// `.aspx.cs` files flow through the SAME `BuildArtifact`→`SemanticModel` path
/// as csproj files — unified, no second-class path.
///
/// References are all DECLARED, resolved the way VS's web-site project system
/// does, none borrowed/guessed:
///   • Framework — the WSP's OWN `TargetFrameworkMoniker` → its
///     `Microsoft.NETFramework.ReferenceAssemblies.&lt;tfm&gt;` pack
///     (per-TFM correct, cross-platform; `FrameworkReferenceResolver`).
///   • ProjectReferences — `{guid}|CloudCore.dll` → the sibling project's
///     already-built `Compilation` (matched by assembly name).
///   • web.config `<assemblies>` — declared 3rd-party (Telerik, …), located
///     by name in the WSP's `bin/` (a declared assembly, not a blind manifest).
///
/// When the framework pack for the WSP's TFM isn't restored on the host, this
/// emits an observable `problem` and resolves NOTHING for the WSP — its files
/// stay references-free (row 5.0.72 flags them per-element). No borrowed-dir
/// fallback.
/// </summary>

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

static class WebSiteProjectLoader
{
    /// <summary>
    /// Build the WSP's Compilation and return a per-file map
    /// (absolute path → (Compilation, SyntaxTree)) for merging into the
    /// orchestrator's projectMap. Empty when the framework pack is missing.
    /// </summary>
    public static Dictionary<string, (Compilation Compilation, SyntaxTree SyntaxTree)> Load(
        WebSiteProject wsp,
        IReadOnlyList<string> wspCsFiles,
        IReadOnlyDictionary<string, string> fileContents,
        IReadOnlyDictionary<string, Compilation> compilationsByAssemblyName,
        string globalPackagesDir,
        List<object> problems)
    {
        var map = new Dictionary<string, (Compilation, SyntaxTree)>();
        if (wspCsFiles.Count == 0) return map;

        // Framework references (per-TFM). No pack ⇒ loud problem, resolve
        // nothing (files remain references-free, flagged by row 5.0.72).
        var frameworkDir = FrameworkReferenceResolver.ResolveReferenceAssemblyDir(
            wsp.TargetFrameworkMoniker, globalPackagesDir);
        if (frameworkDir == null)
        {
            problems.Add(new
            {
                severity = "warning",
                message = $".NET analyzer: Web Site Project \"{wsp.Name}\" — framework reference assemblies for "
                    + $"TargetFrameworkMoniker \"{wsp.TargetFrameworkMoniker ?? "(none)"}\" are not restored "
                    + $"(install Microsoft.NETFramework.ReferenceAssemblies.<tfm>); its {wspCsFiles.Count} file(s) "
                    + $"remain references-free.",
            });
            return map;
        }

        var references = new List<MetadataReference>();
        foreach (var dll in FrameworkReferenceResolver.ReferenceAssemblyDlls(frameworkDir))
            references.Add(MetadataReference.CreateFromFile(dll));

        // ProjectReferences → already-built sibling compilations (CloudCore…),
        // matched by assembly name (output "CloudCore.dll" → "CloudCore").
        foreach (var pr in wsp.ProjectReferences)
        {
            var asmName = Path.GetFileNameWithoutExtension(pr.OutputName);
            if (compilationsByAssemblyName.TryGetValue(asmName, out var siblingCompilation))
                references.Add(siblingCompilation.ToMetadataReference());
            else
                problems.Add(new
                {
                    severity = "warning",
                    message = $".NET analyzer: Web Site Project \"{wsp.Name}\" references project "
                        + $"\"{pr.OutputName}\" but no loaded project produced assembly \"{asmName}\" — "
                        + $"cross-project symbols into it will not resolve.",
                });
        }

        // web.config <assemblies> → declared 3rd-party, located by name in bin/.
        var binDir = Path.Combine(wsp.PhysicalPath, "bin");
        var webConfig = Path.Combine(wsp.PhysicalPath, "web.config");
        if (File.Exists(webConfig))
        {
            string content;
            try { content = File.ReadAllText(webConfig); }
            catch { content = ""; }
            foreach (var asm in WebConfigParser.ParseAssemblyNames(content))
            {
                // Framework assemblies are already covered by the pack; only
                // locate non-framework ones (Telerik, …) in bin/.
                if (File.Exists(Path.Combine(frameworkDir, asm + ".dll"))) continue;
                var binDll = Path.Combine(binDir, asm + ".dll");
                if (File.Exists(binDll))
                    references.Add(MetadataReference.CreateFromFile(binDll));
                // A declared assembly we can't locate is left out → calls into
                // it dangle (resolved by the unresolved-base/limitation path),
                // never silently faked.
            }
        }

        // Parse the WSP's files into one Compilation. Each tree is the one the
        // SemanticModel binds against — return THESE trees so downstream uses
        // the referenced compilation, not the shared references-free one.
        var trees = new List<SyntaxTree>(wspCsFiles.Count);
        var treeByPath = new Dictionary<string, SyntaxTree>();
        foreach (var path in wspCsFiles)
        {
            if (!fileContents.TryGetValue(path, out var content)) continue;
            var tree = CSharpSyntaxTree.ParseText(content, path: path);
            trees.Add(tree);
            treeByPath[path] = tree;
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: SanitizeAssemblyName(wsp.Name),
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var (path, tree) in treeByPath)
            map[path] = (compilation, tree);

        return map;
    }

    static string SanitizeAssemblyName(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
