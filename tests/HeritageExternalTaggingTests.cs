using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for Fathom rows 3.1.0.15 + 3.1.0.16 (unified, crit 4):
/// C# heritage (`extends`/`implements`), `overrides`, `references`
/// (return-type/parameter-type/generic-constraint) emission decided emission
/// on `allNames` — a name-EXISTENCE set — instead of the Roslyn symbol
/// already in scope. An edge to an EXTERNAL (BCL/NuGet/framework) base type
/// then emitted with a bare `targetName`, no external tag: ingest synthesizes
/// a bogus self-file `targetRef` that neither resolves nor is exempt from the
/// strict dangler gate, hard-failing `fathom analyze` on real C# apps
/// (EnvisionWeb, MyPatientNow).
///
/// Fix: `ResolveTypeRef` (Program.cs) resolves every type-position reference
/// to a 3-way symbol-based verdict — in-source / external / unresolved —
/// replacing the `allNames` gate at all 4 type-reference sites (heritage,
/// references return-type/parameter-type, generic-constraint) plus the
/// `overrides` block's ungated bare emit (RC2, the majority defect shape).
/// RC1 (the escape mechanism — an in-file attribute polluting `allNames`
/// with a short name that collided with an unrelated external base) is
/// closed at its source: attribute names no longer enter `allNames`.
///
/// One fixture per failure-shape category named in the design, plus an
/// in-source control guarding against over-tagging.
/// </summary>
public class HeritageExternalTaggingTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(HeritageExternalTaggingTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-heritage-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private sealed record Edge(
        string ElementName, string Type, string? Subtype, string Target, string? TargetRef,
        bool External, string? Provenance);

    /// <summary>Run the analyzer on <paramref name="dir"/>; return every
    /// emitted element's naturalKey (keyed by "artifactId#name") alongside
    /// every edge found anywhere in the run (owning element name, type,
    /// subtype, targetName, targetRef, external/provenance from
    /// metadata) — so a test can assert an edge's targetRef equals the
    /// actual target element's own naturalKey.</summary>
    private static (Dictionary<string, string> NaturalKeysByQualifiedName, List<Edge> Edges) Analyze(string dir)
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

        var naturalKeys = new Dictionary<string, string>();
        var edges = new List<Edge>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "artifact") continue;
                var artifact = root.GetProperty("artifact");
                var artifactId = artifact.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                foreach (var el in elements.EnumerateArray())
                {
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var naturalKey = el.TryGetProperty("naturalKey", out var nk) ? nk.GetString() ?? "" : "";
                    naturalKeys[$"{artifactId}#{name}"] = naturalKey;

                    if (!el.TryGetProperty("edges", out var elEdges) || elEdges.ValueKind != JsonValueKind.Array) continue;
                    foreach (var e in elEdges.EnumerateArray())
                    {
                        var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                        var subtype = e.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        var target = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                        var targetRef = e.TryGetProperty("targetRef", out var tr) ? tr.GetString() : null;
                        bool external = false;
                        string? provenance = null;
                        if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                        {
                            external = md.TryGetProperty("external", out var ext) && ext.ValueKind == JsonValueKind.True;
                            if (md.TryGetProperty("resolutionProvenance", out var rp))
                                provenance = rp.GetString();
                        }
                        edges.Add(new Edge(name, type, subtype, target, targetRef, external, provenance));
                    }
                }
            }
        }
        return (naturalKeys, edges);
    }

    // ---------- 1. attribute-polluted external base (RC1) ----------

    [Fact]
    public void AttributePollutedExternalBase_ExtendsEdge_ExternalTagged_NoBogusTargetRef()
    {
        // RC1: an in-file attribute usage `[Exception]` (resolving to the
        // custom `ExceptionAttribute` below, via the C# attribute-suffix
        // fallback — bare "Exception" is NOT `using System;`-imported here,
        // so it isn't ambiguous) feeds the bare name "Exception" into
        // `allNames`. The base type `System.Exception` (external — CoreLib,
        // no in-source declaration) shares that short name. Pre-fix: the
        // `allNames` gate `!allNames.Contains(name) && targetFile == null`
        // evaluated false (allNames DOES contain "Exception") — so the
        // gate did NOT skip, and the bare `AddWithTargetRef` emitted an
        // untagged `extends` edge with no targetRef and no metadata: ingest
        // then synthesizes a bogus self-file targetRef that neither
        // resolves nor is gate-exempt (a dangler). Post-fix: the base
        // resolves via the symbol to EXTERNAL and carries the tag.
        var dir = MakeTempTree(("X.cs", @"
public class ExceptionAttribute : System.Attribute {}

[Exception]
public class X : System.Exception {
}"));
        try
        {
            var (_, edges) = Analyze(dir);
            // Filter to X specifically — the in-file `ExceptionAttribute :
            // System.Attribute` declaration ALSO emits its own (correctly
            // external-tagged) `extends` edge; that's not what this test pins.
            var extendsEdge = Assert.Single(edges, e => e.Type == "extends" && e.ElementName == "x");
            Assert.True(extendsEdge.External, "external base must carry metadata.external");
            Assert.Equal("external-library", extendsEdge.Provenance);
            Assert.Null(extendsEdge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 2. external override (RC2 — the majority shape) ----------

    [Fact]
    public void ExternalOverride_OverridesEdge_ExternalTagged()
    {
        // RC2 (33/42 measured danglers — the majority defect shape): the
        // `overrides` block had NO gate and NO external tag at all. An
        // override of a resolved-EXTERNAL member (here: `object.Equals` /
        // `object.GetHashCode`, both CoreLib — no declaring syntax
        // reference) fell to the bare `else` emit with neither targetRef
        // nor metadata.
        var dir = MakeTempTree(("P.cs", @"
public class P {
    public override bool Equals(object obj) => true;
    public override int GetHashCode() => 0;
}"));
        try
        {
            var (_, edges) = Analyze(dir);
            var overrides = edges.Where(e => e.Type == "overrides").ToList();
            Assert.NotEmpty(overrides);
            Assert.All(overrides, e =>
            {
                Assert.True(e.External, $"override of external parent '{e.Target}' must carry metadata.external");
                Assert.Equal("external-library", e.Provenance);
                Assert.Null(e.TargetRef);
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 3. implicit external-interface impl ----------

    [Fact]
    public void ImplicitExternalInterfaceImpl_ImplementsEdge_ExternalTagged()
    {
        var dir = MakeTempTree(("C.cs", @"
public class C : System.IDisposable {
    public void Dispose() {}
}"));
        try
        {
            var (_, edges) = Analyze(dir);
            var implementsEdge = Assert.Single(edges, e => e.Type == "implements");
            Assert.True(implementsEdge.External);
            Assert.Equal("external-library", implementsEdge.Provenance);
            Assert.Null(implementsEdge.TargetRef);

            // The implicit interface-method implementation ALSO overrides
            // (RC2) the external interface member — bonus coverage of the
            // interface-implementation `parents` path (not just the
            // class-override `OverriddenMethod` path).
            var disposeOverride = Assert.Single(edges, e => e.Type == "overrides");
            Assert.True(disposeOverride.External);
            Assert.Equal("external-library", disposeOverride.Provenance);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 4. nested same-file heritage (RC3) ----------

    [Fact]
    public void NestedSameFileHeritage_TargetRefResolvesToOuterInnerElementKey()
    {
        var dir = MakeTempTree(("D.cs", @"
public class Outer {
    public class Inner {}
}
public class D : Outer.Inner {}"));
        try
        {
            var (naturalKeys, edges) = Analyze(dir);
            var extendsEdge = Assert.Single(edges, e => e.Type == "extends" && e.ElementName == "d");
            Assert.False(extendsEdge.External);
            Assert.NotNull(extendsEdge.TargetRef);

            var artifactId = Path.Combine(dir, "D.cs");
            var innerKey = naturalKeys[$"{artifactId}#outer/inner"];
            Assert.False(string.IsNullOrEmpty(innerKey));
            Assert.Equal(innerKey, extendsEdge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 5. `I`+digit interface ----------

    [Fact]
    public void IPlusDigitInterface_EmitsImplements_NotExtends()
    {
        // The old heuristic `name[0]=='I' && char.IsUpper(name[1])` treats
        // `I3D` as NOT interface-shaped (`'3'` is not upper) and so
        // mis-classifies it `extends`. The symbol's actual `TypeKind`
        // (Interface) is unambiguous regardless of naming convention.
        var dir = MakeTempTree(("E.cs", @"
public interface I3D {}
public class E : I3D {}"));
        try
        {
            var (_, edges) = Analyze(dir);
            Assert.Single(edges, e => e.Type == "implements" && e.ElementName == "e");
            Assert.DoesNotContain(edges, e => e.Type == "extends" && e.ElementName == "e");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 6. cross-file nested ----------

    [Fact]
    public void CrossFileNestedHeritage_Resolves_NoDangler()
    {
        var dir = MakeTempTree(
            ("Base.cs", @"
public class Outer2 {
    public class Inner2 {}
}"),
            ("Derived.cs", @"
public class D2 : Outer2.Inner2 {}"));
        try
        {
            var (naturalKeys, edges) = Analyze(dir);
            var extendsEdge = Assert.Single(edges, e => e.Type == "extends" && e.ElementName == "d2");
            Assert.False(extendsEdge.External);
            Assert.NotNull(extendsEdge.TargetRef);

            var baseArtifactId = Path.Combine(dir, "Base.cs");
            var innerKey = naturalKeys[$"{baseArtifactId}#outer2/inner2"];
            Assert.False(string.IsNullOrEmpty(innerKey));
            Assert.Equal(innerKey, extendsEdge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 7. in-source control (guard against over-tagging) ----------

    [Fact]
    public void InSourceHeritage_SameFileAndCrossFile_ResolvesWithRealTargetRef_NeverTaggedExternal()
    {
        var dir = MakeTempTree(
            ("Animal.cs", @"
public class Animal {}"),
            ("Dog.cs", @"
public class Dog : Animal {}
public class Cat : Animal {}"));
        try
        {
            var (naturalKeys, edges) = Analyze(dir);

            var dogExtends = Assert.Single(edges, e => e.Type == "extends" && e.ElementName == "dog");
            Assert.False(dogExtends.External, "in-source cross-file base must NOT be tagged external");
            Assert.Null(dogExtends.Provenance);
            Assert.NotNull(dogExtends.TargetRef);

            var catExtends = Assert.Single(edges, e => e.Type == "extends" && e.ElementName == "cat");
            Assert.False(catExtends.External);
            Assert.NotNull(catExtends.TargetRef);

            var animalArtifactId = Path.Combine(dir, "Animal.cs");
            var animalKey = naturalKeys[$"{animalArtifactId}#animal"];
            Assert.False(string.IsNullOrEmpty(animalKey));
            Assert.Equal(animalKey, dogExtends.TargetRef);
            Assert.Equal(animalKey, catExtends.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }
}
