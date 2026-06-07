using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// End-to-end call/constructor/delegate resolution tests (Fathom row
/// dotnet-l0-internal-call-resolution 5.0.68.1). These exercise the
/// orchestration in <c>Program.cs</c> — which is NOT compiled into this test
/// project (it's a top-level-statements program) — by spawning the built
/// analyzer DLL on a temp source tree and parsing its NDJSON output.
///
/// Pins the invariant the L0-.NET resolution gate enforces: every emitted
/// `calls` / `callsMethod` target is class-qualified + signatured so it binds
/// to the callee's element natural key; no bare-name targets are emitted, and
/// unresolvable shapes produce a structured limitation instead of a phantom
/// edge.
/// </summary>
public class CallResolutionIntegrationTests
{
    private static string? AnalyzerDll()
    {
        // tests/bin/Debug/net9.0/<asm>.dll → package root → src/bin/.../analyzer.dll
        var dir = Path.GetDirectoryName(typeof(CallResolutionIntegrationTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Run the analyzer on <paramref name="dir"/> and return the
    /// element-level edges (type, subtype, targetName, hasTargetRef, external)
    /// for the artifact whose path ends with <paramref name="fileSuffix"/>, plus
    /// that artifact's limitation kinds. <c>External</c> reflects the edge's
    /// <c>metadata.external == true</c> tag (the strict-emit marker for an
    /// unbindable external-member call/write).</summary>
    private static (List<(string Type, string? Subtype, string Target, bool HasTargetRef, bool External)> Edges,
        List<string> LimitationKinds)
        AnalyzeFile(string dir, string fileSuffix)
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
        // The analyzer always reads its config slice from stdin (orchestrator
        // contract). Empty object → all defaults.
        proc.StandardInput.Write("{}");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        var edges = new List<(string, string?, string, bool, bool)>();
        var limitations = new List<string>();
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

                if (artifact.TryGetProperty("limitations", out var lims) && lims.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lim in lims.EnumerateArray())
                    {
                        if (lim.TryGetProperty("kind", out var k)) limitations.Add(k.GetString() ?? "");
                    }
                }
                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                foreach (var el in elements.EnumerateArray())
                {
                    if (!el.TryGetProperty("edges", out var elEdges) || elEdges.ValueKind != JsonValueKind.Array) continue;
                    foreach (var e in elEdges.EnumerateArray())
                    {
                        var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                        var subtype = e.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        var target = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                        var hasRef = e.TryGetProperty("targetRef", out _);
                        var external = e.TryGetProperty("metadata", out var md)
                            && md.ValueKind == JsonValueKind.Object
                            && md.TryGetProperty("external", out var ext)
                            && ext.ValueKind == JsonValueKind.True;
                        edges.Add((type, subtype, target, hasRef, external));
                    }
                }
            }
        }
        return (edges, limitations);
    }

    /// <summary>Run the analyzer and return (elementName → entryPoint) for the
    /// artifact ending with <paramref name="fileSuffix"/>.</summary>
    private static Dictionary<string, string?> AnalyzeEntryPoints(string dir, string fileSuffix)
    {
        var dll = AnalyzerDll();
        Assert.True(dll != null, "analyzer DLL not built");
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

        var map = new Dictionary<string, string?>();
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
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string? ep = el.TryGetProperty("entryPoint", out var e) ? e.GetString() : null;
                    map[name] = ep;
                }
            }
        }
        return map;
    }

    [Fact]
    public void EntryPoint_PublicPropertyAccessors_AreLibraryExportMethod()
    {
        // Get/set accessors of a PUBLIC property on a library-export type are
        // externally callable (`new T().Prop` / `.Prop = v`) → library-export-
        // method, same as a public method (Fathom 5.0.68.2.1; TS parity 5.3.4.3.3).
        // Accessors of a private property stay `none` (inherit private — the
        // DeriveAccessibility fix; pre-fix they defaulted to private regardless).
        var dir = MakeTempTree(("Box.cs", @"
public class Box {
    public int Value { get; set; }
    private int Hidden { get; set; }
}"));
        try
        {
            var ep = AnalyzeEntryPoints(dir, "Box.cs");
            Assert.Equal("library-export-method", ep.GetValueOrDefault("box/value/get"));
            Assert.Equal("library-export-method", ep.GetValueOrDefault("box/value/set"));
            Assert.Equal("none", ep.GetValueOrDefault("box/hidden/get"));
            Assert.Equal("none", ep.GetValueOrDefault("box/hidden/set"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CaseOnlyNameCollision_KeepsBothElements_EmitsLimitationNotError()
    {
        // C# allows case-distinct siblings (`isAuto` vs `IsAuto`); both lowercase
        // to the same canonical key. Pre-fix the analyzer emitted a hard error
        // and DROPPED the second (lossy, Fathom 5.0.68.3). Now: both elements
        // emitted (second disambiguated `-casedup1`) + a structured limitation,
        // no dropped element.
        var dir = MakeTempTree(("Cam.cs", @"
public class CameraPropertyValue {
    public bool isAuto;
    public bool IsAuto { get; set; }
}"));
        try
        {
            var ep = AnalyzeEntryPoints(dir, "Cam.cs");
            Assert.True(ep.ContainsKey("camerapropertyvalue/isauto"), "first declaration kept");
            Assert.True(ep.ContainsKey("camerapropertyvalue/isauto-casedup1"), "second declaration kept (disambiguated), not dropped");
            var (_, limitations) = AnalyzeFile(dir, "Cam.cs");
            Assert.Contains("csharp-canonical-name-collision", limitations);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EntryPoint_EventHandlerSignature_IsEventHandler()
    {
        // A method matching the .NET event-callback convention
        // `(object sender, TEventArgs e)` is framework-invoked → entryPoint
        // event-handler (L1-.NET fix #2; the L1 derivation maps this to the
        // event-handler stereotype). Includes derived-EventArgs; excludes
        // non-handler signatures.
        var dir = MakeTempTree(("Form1.cs", @"
using System;
public class Form1 {
    private void button_Click(object sender, EventArgs e) { }
    private void grid_SelectionChanged(object sender, MySelectionEventArgs e) { }
    public void NotAHandler(int x, string y) { }
}
public class MySelectionEventArgs : EventArgs { }"));
        try
        {
            var ep = AnalyzeEntryPoints(dir, "Form1.cs");
            Assert.Equal("event-handler", ep.GetValueOrDefault("form1/button_click-object-eventargs"));
            Assert.Equal("event-handler", ep.GetValueOrDefault("form1/grid_selectionchanged-object-myselectioneventargs"));
            // Non-handler signature is NOT an event-handler.
            Assert.NotEqual("event-handler", ep.GetValueOrDefault("form1/notahandler-int-string"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EntryPoint_PublicMethodOnPublicType_IsLibraryExportMethod()
    {
        // L0-.NET Gate 3: a public method on a public top-level type is
        // externally reachable → library-export-method, not `none`. Private
        // methods and methods on internal types stay `none` (not externally
        // callable). Mirrors TS library-export-method (5.0.55 / 5.3.4.3.1).
        var dir = MakeTempTree(
            ("Pub.cs", @"
public class Pub {
    public void Reachable() { }
    private void Hidden() { }
}"),
            ("Internal.cs", @"
class Internal {
    public void NotExported() { }
}"));
        try
        {
            var ep = AnalyzeEntryPoints(dir, "Pub.cs");
            Assert.Equal("library-export-method", ep.GetValueOrDefault("pub/reachable"));
            Assert.Equal("none", ep.GetValueOrDefault("pub/hidden"));
            Assert.Equal("library-export", ep.GetValueOrDefault("pub"));

            var epInt = AnalyzeEntryPoints(dir, "Internal.cs");
            // Public method on an INTERNAL type is not externally library-callable.
            Assert.Equal("none", epInt.GetValueOrDefault("internal/notexported"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExternalMemberCall_ChainedAndQualified_EmitsExternalTaggedCallsEdge()
    {
        // Fathom row dotnet-l0-external-member-call-edges (5.0.75). A chained /
        // qualified call whose method resolves to an EXTERNAL (BCL / library)
        // declaration — `this.Items.Add(x)`, `_sb.Append("hi")` — used to emit
        // ONLY a generic `references` edge, leaving `calls == 0`. The (correct)
        // method-stereotype rules then saw a no-op → `unclassified` (the 326
        // EnvisionWeb method residual). Fix: emit a `calls` edge tagged
        // `metadata.external` so the behavioral count is non-zero, WITHOUT a
        // targetRef (strict-emit: the external target is observably unbindable,
        // not a phantom in-graph edge).
        var dir = MakeTempTree(("Widget.cs", @"
using System.Collections.Generic;
using System.Text;
public class Widget {
    private List<int> _items = new List<int>();
    public List<int> Items { get { return _items; } }
    private StringBuilder _sb = new StringBuilder();
    public void AddItem(int x) { this.Items.Add(x); }   // chained external call
    public void Build() { _sb.Append(""hi""); }          // qualified external call
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Widget.cs");
            // External-member calls emit a `calls` edge tagged external, no targetRef.
            Assert.Contains(edges, e => e.Type == "calls" && e.External
                && e.Target.Contains("add") && !e.HasTargetRef);
            Assert.Contains(edges, e => e.Type == "calls" && e.External
                && e.Target.Contains("append") && !e.HasTargetRef);
            // The external edge must NOT masquerade as a resolvable in-graph
            // target (no targetRef on any external-tagged edge).
            Assert.DoesNotContain(edges, e => e.External && e.HasTargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExternalPropertyAndIndexerWrite_EmitsExternalTaggedSetEdge()
    {
        // Fathom row dotnet-l0-external-member-call-edges (5.0.75). External
        // property / indexer WRITES — `Session[k]=v`, `ctrl.Text=x` — used to
        // emit only a generic `references` edge (`calls == 0`). Fix: emit a
        // `calls`/`property-set` edge tagged external (no targetRef) so the
        // mutation is counted as observable behavior. Mirrors the resolved
        // accessor-call path, just for unbindable external accessors.
        var dir = MakeTempTree(("Writer.cs", @"
using System.Collections.Generic;
using System.Text;
public class Writer {
    private Dictionary<string,int> _map = new Dictionary<string,int>();
    private StringBuilder _sb = new StringBuilder();
    public void Write() {
        _map[""k""] = 1;        // external indexer write
        _sb.Capacity = 16;     // external property write
    }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Writer.cs");
            // External indexer write → calls/property-set tagged external, no targetRef.
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-set"
                && e.External && !e.HasTargetRef);
            // External property write (Capacity) likewise.
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-set"
                && e.External && e.Target.Contains("capacity") && !e.HasTargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Spawn the analyzer on <paramref name="dir"/> and return every
    /// emitted artifact id, the run-level problems (severity, message), and the
    /// process stderr (operational log lines).</summary>
    private static (List<string> ArtifactIds, List<(string Severity, string Message)> Problems, string Stderr)
        AnalyzeDir(string dir)
    {
        var dll = AnalyzerDll();
        Assert.True(dll != null, "analyzer DLL not built");
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
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);

        var artifactIds = new List<string>();
        var problems = new List<(string, string)>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;
                var type = t.GetString();
                if (type == "artifact" && root.TryGetProperty("artifact", out var a)
                    && a.TryGetProperty("id", out var id))
                {
                    artifactIds.Add(id.GetString() ?? "");
                }
                else if (type == "problem" && root.TryGetProperty("problem", out var p))
                {
                    var sev = p.TryGetProperty("severity", out var s) ? s.GetString() ?? "" : "";
                    var msg = p.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    problems.Add((sev, msg));
                }
            }
        }
        return (artifactIds, problems, stderr);
    }

    // The analyzer logs "MSBuildWorkspace loaded N document(s)" — a load with
    // N>=1 confirms MSBuild was located + the project opened (so the exclusion
    // path is exercisable). Gates the build-excluded assertions off a real load.
    private static bool ProjectLoaded(string stderr)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stderr, @"loaded (\d+) document");
        return m.Success && int.Parse(m.Groups[1].Value) >= 1;
    }

    [Fact]
    public void BuildExcludedCsproj_FileNotInCompileSet_IsOmittedNotAnalyzedReferencesFree()
    {
        // Fathom row dotnet-csproj-compile-set-coverage (5.0.74). An old-style
        // csproj compiles only its explicit <Compile> items. A .cs sitting in the
        // project dir but NOT in <Compile> (a dead/generated/excluded file — the
        // EnvisionWeb orphaned typed-DataSet shape) is build-excluded: omitted
        // from the corpus + a `warning` emitted, NOT analyzed references-free.
        if (OperatingSystem.IsWindows()) return; // resolution/exclusion path is non-Windows
        var dir = MakeTempTree(
            ("Old.csproj",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Project ToolsVersion=\"12.0\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <Import Project=\"$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\" Condition=\"Exists('$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props')\" />\n" +
                "  <PropertyGroup>\n" +
                "    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>\n" +
                "    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>\n" +
                "    <OutputType>Library</OutputType>\n" +
                "    <RootNamespace>Old</RootNamespace>\n" +
                "    <AssemblyName>Old</AssemblyName>\n" +
                "    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n" +
                "  </PropertyGroup>\n" +
                "  <ItemGroup><Reference Include=\"System\" /></ItemGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Compile Include=\"Live.cs\" />\n" +  // Dead.cs intentionally NOT listed
                // Case-mismatched include: csproj says lowercase `cased.designer.cs`,
                // disk is `Cased.Designer.cs`. The build compiles it (case-insensitive
                // fs); it must NOT be build-excluded even though Documents may drop it.
                "    <Compile Include=\"cased.designer.cs\" />\n" +
                "  </ItemGroup>\n" +
                "  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />\n" +
                "</Project>\n"),
            ("Live.cs", "public class Live { public void M() { } }"),
            ("Cased.Designer.cs", "public class Cased { public void M() { } }"),
            ("Dead.cs", "public class Dead { public void M() { } }"));
        try
        {
            var (artifactIds, problems, stderr) = AnalyzeDir(dir);
            if (!ProjectLoaded(stderr)) return; // no MSBuild on host — exclusion path not exercisable
            var ids = artifactIds.Select(s => s.Replace('\\', '/')).ToList();
            // Sanity: the project loaded and Live.cs (in <Compile>) WAS analyzed.
            Assert.Contains(ids, s => s.EndsWith("/Live.cs"));
            // Dead.cs (not in <Compile> under any casing) must be OMITTED.
            Assert.DoesNotContain(ids, s => s.EndsWith("/Dead.cs"));
            // Cased.Designer.cs IS in <Compile> (case-mismatched) → COMPILED →
            // must NOT be excluded (regression: pre-fix, the Ordinal/Documents
            // miss wrongly excluded it, orphaning its members → 402 dangling
            // edges on EnvisionWeb). It's analyzed (project or references-free).
            Assert.Contains(ids, s => s.EndsWith("/Cased.Designer.cs"));
            // And the omission is observable: a build-excluded warning fired.
            Assert.Contains(problems, p => p.Severity == "warning"
                && p.Message.Contains("EXCLUDED from analysis")
                && p.Message.Contains("<Compile>"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PartialClass_CrossFileCallsMethod_EmitsCrossFileTargetRef()
    {
        // Fathom row dotnet-msbuildworkspace-documents-completeness (5.0.77). A
        // partial type spans files: a constructor in Widget.cs calls Setup()
        // declared in Widget.Designer.cs. The intra-class callsMethod edge must
        // carry a cross-file targetRef to the .Designer.cs element — else the
        // overlay resolves the bare `widget/setup` name WITHIN Widget.cs and the
        // edge dangles (the 529 strict-edge-check failures on EnvisionWeb's
        // report partials: ctor → InitializeComponent). Same-file calls stay
        // bare (the overlay resolves them intra-artifact).
        var dir = MakeTempTree(
            ("Widget.cs", @"
public partial class Widget {
    public Widget() { this.Setup(); }       // cross-file → Widget.Designer.cs
    public void Local() { this.Helper(); }  // same-file
    public void Helper() { }
}"),
            ("Widget.Designer.cs", @"
public partial class Widget {
    public void Setup() { }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Widget.cs");
            // Cross-file callsMethod (Setup lives in Widget.Designer.cs) carries a targetRef.
            Assert.Contains(edges, e => e.Type == "callsMethod"
                && e.Target.Contains("setup") && e.HasTargetRef);
            // Same-file callsMethod (Helper in Widget.cs) stays bare — overlay resolves intra-artifact.
            Assert.Contains(edges, e => e.Type == "callsMethod"
                && e.Target.Contains("helper") && !e.HasTargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-callres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void CallsMethod_SameClassWithParams_EmitsSignaturedQualifiedTarget()
    {
        var dir = MakeTempTree(("Service.cs", @"
public class Service {
    public void Process(int a, string b) { }
    public void Run() { Process(1, ""x""); }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Service.cs");
            // callsMethod target must be class-qualified + signatured so it binds
            // to the method element key `service:process-int-string`.
            Assert.Contains(edges, e => e.Type == "callsMethod" && e.Target == "service/process-int-string");
            // No bare/unsignatured callsMethod target.
            Assert.DoesNotContain(edges, e => e.Type == "callsMethod" && e.Target == "service/process");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Constructor_WorkspaceType_ResolvesWithTargetRef_ExternalTypeEmitsNoEdge()
    {
        var dir = MakeTempTree(
            ("App.cs", @"
public class App {
    public void Build() {
        var w = new Worker();
        var sb = new System.Text.StringBuilder();
    }
}"),
            ("Worker.cs", "public class Worker { }"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "App.cs");
            // Workspace type → constructor edge bound to the type element.
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "constructor" && e.Target == "worker");
            // External BCL type (StringBuilder) → no edge (no false-positive,
            // no dangling) — the whole point of resolving the type.
            Assert.DoesNotContain(edges, e => e.Type == "calls" && e.Subtype == "constructor"
                && e.Target.Contains("stringbuilder"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PropertyAccess_EmitsAccessorCallEdges_GetAndSet()
    {
        // C# property access is a method call on the get/set accessor — must
        // emit `calls`/property-get (read) + property-set (write) to the
        // accessor elements, not just a generic reference (Gate 5 E2 closer;
        // mirrors TS 5.0.66). Plain fields must NOT (they have no accessor).
        var dir = MakeTempTree(("Box.cs", @"
public class Box {
    public int Value { get; set; }
    public int Plain;
    public void Use(Box other) {
        var x = other.Value;   // read → get-accessor
        other.Value = 5;       // write → set-accessor
        var y = other.Plain;   // field read → NOT an accessor call
    }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Box.cs");
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-get" && e.Target == "box/value/get");
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-set" && e.Target == "box/value/set");
            // A plain field access emits no accessor-call edge.
            Assert.DoesNotContain(edges, e => e.Type == "calls" && e.Target.StartsWith("box/plain"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IntraClassCall_NestedType_QualifiedWithFullPath()
    {
        // Intra-class callsMethod in a NESTED type must qualify with the full
        // nested path (Outer/Inner/...) so it binds to the nested member's
        // element key — the typed-DataSet (nested DataTable) pattern. Immediate-
        // class-only qualification left these dangling (Gate 5 corpus sweep).
        var dir = MakeTempTree(("Nested.cs", @"
public class Outer {
    public class Inner {
        public void Helper(int a) { }
        public void Run() { Helper(1); }
    }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Nested.cs");
            Assert.Contains(edges, e => e.Type == "callsMethod"
                && e.Target == "outer/inner/helper-int");
            Assert.DoesNotContain(edges, e => e.Type == "callsMethod" && e.Target == "inner/helper-int");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IndexerAccess_WithExplicitAccessorBlock_TargetsAccessorWithoutParamSig()
    {
        // An indexer with an explicit `get { }` block emits a separate accessor
        // element whose key OMITS the indexer's parameter signature
        // (`store:indexer:get`, not `store:indexer-string:get`). The accessor-
        // call target must match that — appending the indexer param sig left
        // these dangling (surfaced on EnvisionWeb, Fathom 5.0.68.4).
        var dir = MakeTempTree(("Store.cs", @"
public class Store {
    private int _v;
    public int this[string key] { get { return _v; } }
    public int Read(Store other, string k) { return other[k]; }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Store.cs");
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-get"
                && e.Target == "store/indexer/get");
            // Must NOT carry the indexer param signature on the accessor target.
            Assert.DoesNotContain(edges, e => e.Type == "calls" && e.Target.Contains("indexer-string"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Delegate_EventHandlerSubscription_ResolvesToSignaturedHandler()
    {
        var dir = MakeTempTree(("Wiring.cs", @"
using System;
public class Wiring {
    public event EventHandler Tick;
    public void Hook() { this.Tick += OnTick; }
    private void OnTick(object s, EventArgs e) { }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Wiring.cs");
            // The `+= OnTick` handler resolves to the signatured method element.
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "delegates"
                && e.Target.StartsWith("wiring/ontick"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
