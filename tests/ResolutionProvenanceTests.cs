using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Edge resolution-provenance (Fathom row edge-resolution-provenance 5.0.80;
/// H2 of the 2026-06-07 context-sufficiency audit). A non-in-source edge
/// carries <c>metadata.resolutionProvenance</c> so a library boundary is
/// distinguishable from an analyzer bug without a source-read.
///
/// In-process unit tests pin the classifier (<see cref="ProvenanceHelpers"/>);
/// the spawn tests pin the wire — one fixture per emitted provenance value
/// (external-library call, external-library named property, dynamic indexer)
/// plus the invariant that in-source edges stay UNTAGGED.
/// </summary>
public class ResolutionProvenanceTests
{
    // ---------- in-process classifier ----------

    private static ISymbol ResolveSymbol(string code, Func<SyntaxNode, bool> pick)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var comp = CSharpCompilation.Create("prov-test", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = comp.GetSemanticModel(tree);
        var node = tree.GetRoot().DescendantNodes().First(pick);
        var sym = model.GetSymbolInfo(node).Symbol;
        Assert.NotNull(sym);
        return sym!;
    }

    [Fact]
    public void ExternalCall_ClassifiesAsExternalLibrary()
    {
        const string code = @"
using System.Text;
class C { void M() { var sb = new StringBuilder(); sb.Append(""x""); } }";
        var sym = ResolveSymbol(code, n => n is InvocationExpressionSyntax);
        Assert.Equal("external-library", ProvenanceHelpers.ClassifyExternalCall((IMethodSymbol)sym));
    }

    [Fact]
    public void ExternalIndexer_ClassifiesAsDynamic()
    {
        const string code = @"
using System.Collections.Generic;
class C { void M() { var d = new Dictionary<string,int>(); var x = d[""k""]; } }";
        var sym = ResolveSymbol(code, n => n is ElementAccessExpressionSyntax);
        Assert.True(((IPropertySymbol)sym).IsIndexer);
        Assert.Equal("dynamic", ProvenanceHelpers.ClassifyExternalProperty((IPropertySymbol)sym));
    }

    [Fact]
    public void ExternalNamedProperty_ClassifiesAsExternalLibrary()
    {
        const string code = @"
using System.Text;
class C { void M() { var sb = new StringBuilder(); var n = sb.Capacity; } }";
        var sym = ResolveSymbol(code,
            n => n is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Capacity");
        Assert.False(((IPropertySymbol)sym).IsIndexer);
        Assert.Equal("external-library", ProvenanceHelpers.ClassifyExternalProperty((IPropertySymbol)sym));
    }

    // ---------- wire path (one fixture per emitted value + untagged invariant) ----------

    [Fact]
    public void Wire_ExternalCall_CarriesExternalLibraryProvenance()
    {
        var edges = AnalyzeProvenance(MakeTempTree(("Widget.cs", @"
using System.Text;
public class Widget {
    private StringBuilder _sb = new StringBuilder();
    public void Build() { _sb.Append(""hi""); }
}")), "Widget.cs");
        Assert.Contains(edges, e =>
            e.Type == "calls" && e.External && e.Target.Contains("append")
            && e.Provenance == "external-library");
    }

    [Fact]
    public void Wire_DynamicIndexerAndNamedProperty_CarryDistinctProvenance()
    {
        var edges = AnalyzeProvenance(MakeTempTree(("Writer.cs", @"
using System.Collections.Generic;
using System.Text;
public class Writer {
    private Dictionary<string,int> _map = new Dictionary<string,int>();
    private StringBuilder _sb = new StringBuilder();
    public void Write() {
        _map[""k""] = 1;     // string-keyed external indexer → dynamic
        _sb.Capacity = 16;  // named external property → external-library
    }
}")), "Writer.cs");
        // The indexer write is the irreducible dynamic tail.
        Assert.Contains(edges, e =>
            e.Type == "calls" && e.Subtype == "property-set" && e.External
            && e.Provenance == "dynamic");
        // The named external property write resolves to a library member.
        Assert.Contains(edges, e =>
            e.Type == "calls" && e.Subtype == "property-set" && e.External
            && e.Target.Contains("capacity") && e.Provenance == "external-library");
    }

    [Fact]
    public void Wire_InSourceEdges_StayUntagged()
    {
        // An in-source call resolves to a graph node → NO resolutionProvenance.
        // Invariant across the whole artifact: provenance is present IFF external.
        var edges = AnalyzeProvenance(MakeTempTree(("Pair.cs", @"
public class Pair {
    public int Helper() { return 1; }
    public int Use() { return Helper(); }   // in-source call
}")), "Pair.cs");
        Assert.All(edges, e =>
            Assert.Equal(e.External, e.Provenance != null));
        // And specifically: at least one in-source edge exists and is untagged.
        Assert.Contains(edges, e => !e.External && e.Provenance == null);
    }

    // ---------- harness ----------

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(ResolutionProvenanceTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static List<(string Type, string? Subtype, string Target, bool External, string? Provenance)>
        AnalyzeProvenance(string dir, string fileSuffix)
    {
        var dll = AnalyzerDll();
        Assert.True(dll != null, "analyzer DLL not built — run `dotnet build src -c Debug`");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll!);
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(dir);
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write("{}");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        var edges = new List<(string, string?, string, bool, string?)>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "artifact") continue;
                var artifact = root.GetProperty("artifact");
                var id = artifact.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (!id.Replace('\\', '/').EndsWith(fileSuffix)) continue;
                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                foreach (var el in elements.EnumerateArray())
                {
                    if (!el.TryGetProperty("edges", out var elEdges) || elEdges.ValueKind != JsonValueKind.Array) continue;
                    foreach (var e in elEdges.EnumerateArray())
                    {
                        var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                        var subtype = e.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        var target = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                        bool external = false;
                        string? provenance = null;
                        if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                        {
                            external = md.TryGetProperty("external", out var ext) && ext.ValueKind == JsonValueKind.True;
                            if (md.TryGetProperty("resolutionProvenance", out var rp))
                                provenance = rp.GetString();
                        }
                        edges.Add((type, subtype, target, external, provenance));
                    }
                }
            }
        }
        return edges;
    }
}
