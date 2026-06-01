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
    /// element-level edges (type, subtype, targetName, hasTargetRef) for the
    /// artifact whose path ends with <paramref name="fileSuffix"/>, plus that
    /// artifact's limitation kinds.</summary>
    private static (List<(string Type, string? Subtype, string Target, bool HasTargetRef)> Edges,
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

        var edges = new List<(string, string?, string, bool)>();
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
                        edges.Add((type, subtype, target, hasRef));
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
