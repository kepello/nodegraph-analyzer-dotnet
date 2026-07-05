using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for Fathom row <c>imports-resolution-near-total-dangling</c>
/// (5.0.113). The artifact-level `imports` edge (Program.cs:475-484, one edge
/// per `using` directive, `targetName` = the canonicalized namespace string)
/// carries no `targetRef` — it structurally cannot, since a namespace is not a
/// single declaring file — so EVERY `using` reads as a plain dangling edge to
/// downstream consumers (coupling metrics, callee surfaces), even though the
/// overwhelming majority target the BCL/framework (`System.*`, `Microsoft.*`)
/// and are honestly, permanently external.
///
/// Fix: `using` directives whose canonicalized namespace matches a known-
/// external prefix (<see cref="SemanticCatalog.IsKnownExternalNamespace"/> —
/// the analyzer-owned catalog, not engine vocabulary, per the 3.4-wave
/// principle) now carry the SAME `metadata.external` + `resolutionProvenance`
/// shape the `calls`/`references` external-edge path already established
/// (Fathom 5.0.80 / H2; mirrored via <see cref="ProvenanceHelpers.ExternalLibrary"/>).
/// App-own namespaces (unmatched) are UNCHANGED — they stay plainly dangling
/// (honest; resolving them is parked row 5.0.113.r2).
/// </summary>
public class ImportsExternalTaggingTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(ImportsExternalTaggingTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record ImportEdge(string TargetName, bool? External, string? ResolutionProvenance);

    /// <summary>Run the analyzer on <paramref name="dir"/> and return every
    /// `imports` edge (targetName + metadata.external + metadata.resolutionProvenance)
    /// found on any artifact.</summary>
    private static List<ImportEdge> AnalyzeImports(string dir)
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

        var imports = new List<ImportEdge>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "artifact") continue;
                var artifact = root.GetProperty("artifact");
                if (!artifact.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in edges.EnumerateArray())
                {
                    var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    if (type != "imports") continue;
                    var targetName = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                    bool? external = null;
                    string? provenance = null;
                    if (e.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        if (meta.TryGetProperty("external", out var ext) && ext.ValueKind == JsonValueKind.True) external = true;
                        if (meta.TryGetProperty("resolutionProvenance", out var prov)) provenance = prov.GetString();
                    }
                    imports.Add(new ImportEdge(targetName, external, provenance));
                }
            }
        }
        return imports;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-imports-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void UsingSystem_TagsExternalLibrary()
    {
        var dir = MakeTempTree(
            ("File.cs", @"
using System;
using System.Collections.Generic;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var system = Assert.Single(imports, i => i.TargetName == "system");
            Assert.True(system.External, "using System; must carry metadata.external: true");
            Assert.Equal(ProvenanceHelpers.ExternalLibrary, system.ResolutionProvenance);

            var collections = Assert.Single(imports, i => i.TargetName == "system-collections-generic");
            Assert.True(collections.External, "using System.Collections.Generic; must carry metadata.external: true");
            Assert.Equal(ProvenanceHelpers.ExternalLibrary, collections.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UsingMicrosoftNamespace_TagsExternalLibrary()
    {
        var dir = MakeTempTree(
            ("File.cs", @"
using Microsoft.AspNetCore.Something;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var edge = Assert.Single(imports, i => i.TargetName == "microsoft-aspnetcore-something");
            Assert.True(edge.External, "using Microsoft.AspNetCore.Something; must carry metadata.external: true");
            Assert.Equal(ProvenanceHelpers.ExternalLibrary, edge.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UsingAppOwnNamespace_StaysUntaggedAndDangling()
    {
        // Negative pin: an app-own namespace using must NOT carry
        // metadata.external — it stays plainly dangling (honest; resolving
        // it is parked row 5.0.113.r2).
        var dir = MakeTempTree(
            ("File.cs", @"
using Envision.Services;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var edge = Assert.Single(imports, i => i.TargetName == "envision-services");
            Assert.Null(edge.External);
            Assert.Null(edge.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UsingSubstringAdjacentToSystemRoot_StaysUntaggedAndDangling()
    {
        // Negative pin (5.0.113 reviewer F1): `Systematic.*` starts with the
        // literal characters "system" but is NOT a dash-segment match —
        // canonicalized it's "systematic-foo", not "system-..." — so it must
        // stay plainly dangling. A regression to a bare
        // `StartsWith(root)` (dropping the `+ "-"` segment-boundary check in
        // `SemanticCatalog.IsKnownExternalNamespace`) would silently
        // over-tag this as external.
        var dir = MakeTempTree(
            ("File.cs", @"
using Systematic.Foo;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var edge = Assert.Single(imports, i => i.TargetName == "systematic-foo");
            Assert.Null(edge.External);
            Assert.Null(edge.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UsingSubstringAdjacentToMicrosoftRoot_StaysUntaggedAndDangling()
    {
        // Negative pin (5.0.113 reviewer F1): `MicrosoftFoo.*` starts with
        // the literal characters "microsoft" but is NOT a dash-segment
        // match — canonicalized it's "microsoftfoo-bar", not
        // "microsoft-..." — so it must stay plainly dangling. Same
        // regression-class witness as the Systematic.* pin above.
        var dir = MakeTempTree(
            ("File.cs", @"
using MicrosoftFoo.Bar;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var edge = Assert.Single(imports, i => i.TargetName == "microsoftfoo-bar");
            Assert.Null(edge.External);
            Assert.Null(edge.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UsingAlias_ClassifiesByUnderlyingNamespace()
    {
        // `using Foo = System.Collections.Generic.Dictionary<string, int>;` —
        // the underlying (RHS) target is what Program.cs already canonicalizes
        // (usingDirective.Name is the alias TARGET, not the alias itself), so
        // this classifies exactly like a plain `using System.Collections.Generic;`.
        var dir = MakeTempTree(
            ("File.cs", @"
using Foo = System.Collections.Generic.Dictionary<string, int>;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var edge = Assert.Single(imports, i => i.TargetName.StartsWith("system-collections-generic-dictionary"));
            Assert.True(edge.External, "aliased using targeting System.* must carry metadata.external: true");
            Assert.Equal(ProvenanceHelpers.ExternalLibrary, edge.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UsingStatic_ClassifiesByUnderlyingNamespace()
    {
        // `using static System.Math;` — usingDirective.Name is "System.Math"
        // regardless of the `static` keyword, so this classifies identically
        // to a plain namespace using.
        var dir = MakeTempTree(
            ("File.cs", @"
using static System.Math;
namespace App {
    public class Widget { }
}"));
        try
        {
            var imports = AnalyzeImports(dir);
            var edge = Assert.Single(imports, i => i.TargetName == "system-math");
            Assert.True(edge.External, "using static System.Math; must carry metadata.external: true");
            Assert.Equal(ProvenanceHelpers.ExternalLibrary, edge.ResolutionProvenance);
        }
        finally { Directory.Delete(dir, true); }
    }
}
