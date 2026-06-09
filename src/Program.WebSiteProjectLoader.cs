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
        var addedAssemblies = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        string webConfigContent = "";
        if (File.Exists(webConfig))
        {
            try { webConfigContent = File.ReadAllText(webConfig); }
            catch { webConfigContent = ""; }
            foreach (var asm in WebConfigParser.ParseAssemblyNames(webConfigContent))
            {
                // Framework assemblies are already covered by the pack; only
                // locate non-framework ones (Telerik, …) in bin/.
                if (File.Exists(Path.Combine(frameworkDir, asm + ".dll"))) continue;
                var binDll = Path.Combine(binDir, asm + ".dll");
                if (File.Exists(binDll) && addedAssemblies.Add(asm))
                    references.Add(MetadataReference.CreateFromFile(binDll));
                // A declared assembly we can't locate is left out → calls into
                // it dangle (resolved by the unresolved-base/limitation path),
                // never silently faked.
            }
        }

        // WebForms control-field synthesis (Fathom row 5.0.87): parse the
        // WSP's markup, and FIRST harvest the additional DECLARED assemblies —
        // web.config <pages><controls> + per-file <%@ Register Assembly= %>.
        // On EnvisionWeb that (not <assemblies>) is what declares
        // telerik: → Telerik.Web.UI.dll, so without this the synthesized
        // Telerik fields could never type-resolve. Same declared-not-globbed
        // policy as <assemblies>.
        var globalRegisters = WebFormsMarkupParser.ParsePagesControls(webConfigContent);
        var markupByPath = new Dictionary<string, WebFormsMarkupFile>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in new[] { "*.ascx", "*.aspx", "*.master" })
        {
            string[] markupPaths;
            try { markupPaths = Directory.GetFiles(wsp.PhysicalPath, pattern, SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var markupPath in markupPaths)
            {
                try { markupByPath[markupPath] = WebFormsMarkupParser.Parse(File.ReadAllText(markupPath), markupPath); }
                catch { /* unreadable markup → no fields from it; codebehind stays Variant-B honest */ }
            }
        }
        var registeredAssemblies = markupByPath.Values.SelectMany(m => m.Registers)
            .Concat(globalRegisters)
            .Select(r => r.Assembly)
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => a!.Split(',')[0].Trim());
        foreach (var asm in registeredAssemblies)
        {
            if (File.Exists(Path.Combine(frameworkDir, asm + ".dll"))) continue;
            var binDll = Path.Combine(binDir, asm + ".dll");
            if (File.Exists(binDll) && addedAssemblies.Add(asm))
                references.Add(MetadataReference.CreateFromFile(binDll));
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

        // WebForms control-field synthesis (Fathom row 5.0.87): map each
        // markup file's runat="server" controls to types and inject ONE
        // generated-companion partial per codebehind class into the
        // Compilation ONLY — the companion trees are NEVER added to the
        // returned per-file map, so they produce no artifacts/elements and
        // edges into them stay observably generated-companion (H2).
        var companionProblems = new List<string>();
        var companions = WebFormsCompanion.BuildCompanions(
            markupByPath.Values.ToList(),
            globalRegisters,
            typeExists: fqn => compilation.GetTypeByMetadataName(fqn) != null,
            srcInheritsLookup: srcPath =>
                markupByPath.TryGetValue(srcPath, out var target) ? target.Inherits : null,
            declaredMembersOfClass: BuildDeclaredMemberLookup(trees),
            wspRoot: wsp.PhysicalPath,
            problems: companionProblems);
        if (companions.Count > 0)
        {
            compilation = compilation.AddSyntaxTrees(companions.Select(c =>
                CSharpSyntaxTree.ParseText(c.Source, path: c.Path)));
        }
        if (companionProblems.Count > 0)
        {
            // One proportional problem per WSP (not one per control) — loud
            // but not flooding. Every unresolved control is named, capped.
            const int maxDetail = 10;
            var detail = string.Join("; ", companionProblems.Take(maxDetail));
            var more = companionProblems.Count > maxDetail
                ? $" (+{companionProblems.Count - maxDetail} more)" : "";
            problems.Add(new
            {
                severity = "warning",
                message = $".NET analyzer: Web Site Project \"{wsp.Name}\" — {companionProblems.Count} markup "
                    + $"control(s) could not be fully type-resolved; their member-access edges drop honestly "
                    + $"(generated-companion synthesis, row 5.0.87): {detail}{more}",
            });
        }

        foreach (var (path, tree) in treeByPath)
            map[path] = (compilation, tree);

        return map;
    }

    /// <summary>
    /// Member names per type, unioned across the WSP's REAL trees, keyed by
    /// the dotted type name (namespace-qualified when namespaced — WSP
    /// codebehind classes are typically global). Used to skip synthesizing a
    /// control field the codebehind already declares (ASP.NET's own rule —
    /// a duplicate would be a CS0102 error symbol poisoning resolution).
    /// </summary>
    static System.Func<string, IReadOnlyCollection<string>> BuildDeclaredMemberLookup(
        IReadOnlyList<SyntaxTree> trees)
    {
        var membersByClass = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax root;
            try { root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)tree.GetRoot(); }
            catch { continue; }
            foreach (var typeDecl in root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
            {
                var name = typeDecl.Identifier.Text;
                for (var parent = typeDecl.Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax nsDecl)
                        name = nsDecl.Name + "." + name;
                }
                if (!membersByClass.TryGetValue(name, out var members))
                    membersByClass[name] = members = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var member in typeDecl.Members)
                {
                    switch (member)
                    {
                        case Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax fd:
                            foreach (var v in fd.Declaration.Variables) members.Add(v.Identifier.Text);
                            break;
                        case Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd:
                            members.Add(pd.Identifier.Text);
                            break;
                        case Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax md:
                            members.Add(md.Identifier.Text);
                            break;
                    }
                }
            }
        }
        return cls => membersByClass.TryGetValue(cls, out var m)
            ? m : (IReadOnlyCollection<string>)System.Array.Empty<string>();
    }

    static string SanitizeAssemblyName(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
