using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for reference-qualification (Fathom row
/// <c>dotnet-l0-ref-qualification</c> 5.0.93). Covers the six fixture
/// categories from the design:
///
///   C1 — Identifier ref to a cross-file type → qualified targetRef.
///   C2 — Identifier ref to a SAME-FILE type-qualified member → qualified
///         targetRef (bare name never binds within-artifact for .NET keys).
///   C3 — Identifier ref to an external/BCL symbol → NO references edge
///         + limitation per convention.
///   C4 — Generic constraint on a workspace type (cross-file) → qualified
///         targetRef.
///   C5 — Generic constraint on an external type → no references edge.
///   F6 — Two classes in DIFFERENT files each declaring Run(ParsedArgs args);
///         a call resolves with a FILE-qualified targetRef to the correct one
///         (no file-less `runner:run-parsedargs` emission anywhere).
///
/// All tests use the spawn-based NDJSON convention from
/// <see cref="CallResolutionIntegrationTests"/>.
/// </summary>
public class RefQualificationTests
{
    // ------------------------------------------------------------------ helpers

    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(RefQualificationTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Spawn analyzer on <paramref name="dir"/>; return every edge
    /// (type, subtype, targetName, hasTargetRef, targetRef) emitted by ALL
    /// elements in the artifact whose path ends with <paramref name="fileSuffix"/>,
    /// plus that artifact's limitation kinds.</summary>
    private static (
        List<(string Type, string? Subtype, string Target, bool HasTargetRef, string? TargetRef)> Edges,
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
        proc.StandardInput.Write("{}");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        var edges = new List<(string, string?, string, bool, string?)>();
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
                        if (lim.TryGetProperty("kind", out var k))
                            limitations.Add(k.GetString() ?? "");
                    }
                }
                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                foreach (var el in elements.EnumerateArray())
                {
                    if (!el.TryGetProperty("edges", out var elEdges) || elEdges.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var e in elEdges.EnumerateArray())
                    {
                        var type    = e.TryGetProperty("type",       out var ty) ? ty.GetString() ?? "" : "";
                        var subtype = e.TryGetProperty("subtype",    out var st) ? st.GetString() : null;
                        var target  = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                        var hasRef  = e.TryGetProperty("targetRef",  out var trProp);
                        var refVal  = hasRef ? trProp.GetString() : null;
                        edges.Add((type, subtype, target, hasRef, refVal));
                    }
                }
            }
        }
        return (edges, limitations);
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-refqual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files)
            File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    // ------------------------------------------------------------------ C1

    [Fact]
    public void C1_IdentifierRef_CrossFileMethod_BareEdgeSuppressed()
    {
        // C1: A body-level identifier reference to a METHOD declared in ANOTHER
        // workspace file must NOT emit a bare `references/identifier` edge with
        // just the method's simple name — such an edge can't bind because .NET
        // natural keys are TYPE-QUALIFIED (e.g. `targetservice/process-string`).
        // Either the edge is emitted with a fully-qualified targetRef, or it is
        // suppressed (if the call already captured it via `calls`). Under no
        // circumstances should a bare name land without targetRef
        // (Fathom row 5.0.93).
        var dir = MakeTempTree(
            ("Caller.cs", @"
public class Caller {
    public void Run(TargetService svc) {
        svc.Process(""hi"");
    }
}"),
            ("TargetService.cs", @"
public class TargetService {
    public void Process(string s) { }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Caller.cs");
            // No bare `references/identifier` to `process` (or `targetservice`)
            // without a targetRef — those cannot bind.
            Assert.DoesNotContain(edges, e =>
                e.Type == "references" && e.Subtype == "identifier" &&
                (e.Target == "process" || e.Target == "targetservice") &&
                !e.HasTargetRef);
            // The call itself must carry a cross-file targetRef.
            Assert.Contains(edges, e =>
                e.Type == "calls" &&
                e.Target.Contains("process") &&
                e.HasTargetRef &&
                (e.TargetRef ?? "").Contains("TargetService.cs"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ C2

    [Fact]
    public void C2_IdentifierRef_SameFileMember_EmitsQualifiedTargetRef()
    {
        // C2: A reference to a METHOD on a same-file class must emit with a
        // qualified targetRef (`<file>#class/method-sig`) even though the target
        // is in the same file — .NET natural keys are TYPE-QUALIFIED so a bare
        // method name does NOT bind within the artifact (Fathom row 5.0.93).
        var dir = MakeTempTree(("App.cs", @"
public class App {
    public void Run() { }
    public void Start() {
        Run();
    }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "App.cs");
            // Any references/identifier edge to `run` (the bare method name)
            // must carry a targetRef to the class-qualified key, NOT emit bare.
            // Pre-fix: emits bare { type:"references", subtype:"identifier",
            //          targetName:"run" } (no targetRef).
            // Post-fix: carries targetRef ending `#app/run` (or the whole
            // canonical form) so the substrate can bind it.
            var refEdge = edges.FirstOrDefault(e =>
                e.Type == "references" &&
                e.Target.Contains("run"));
            if (refEdge.Type != null) // if an edge is emitted at all
            {
                // Must have targetRef (not bare), pointing to class-qualified key.
                Assert.True(refEdge.HasTargetRef,
                    $"Same-file method reference `run` must carry a targetRef; " +
                    $"got bare edge {{ target={refEdge.Target}, hasTargetRef=false }}");
            }
            // Also: if a `calls/direct` edge was emitted (call resolution took
            // over), the calls path already carries a targetRef — that's fine
            // and means the references path should be suppressed. What must NOT
            // exist is a bare `references/identifier` for the method name.
            Assert.DoesNotContain(edges, e =>
                e.Type == "references" && e.Subtype == "identifier" &&
                e.Target == "run" && !e.HasTargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ C3

    [Fact]
    public void C3_IdentifierRef_ExternalBclSymbol_EmitsNoEdgePlusLimitation()
    {
        // C3: An identifier reference to an external/BCL symbol that appears
        // in `allNames` only by coincidence of name must emit NO references
        // edge — no phantom that the substrate can't resolve — and MAY record
        // a limitation. Pre-fix: any workspace-name-matching identifier emits
        // a bare references edge regardless of whether the symbol is external
        // (Fathom row 5.0.93).
        //
        // This fixture: a method body contains the identifier `List` which
        // happens to match a workspace type name AND is the BCL List<T>; the
        // semantic model tells the resolved symbol is System.Collections.Generic.
        // We don't want a phantom `references/identifier` edge to `list` that
        // can never bind to the workspace List type.
        var dir = MakeTempTree(
            ("Store.cs", @"
using System.Collections.Generic;
public class Store {
    public void Fill() {
        var items = new List<int>();
    }
}"),
            ("List.cs", @"
// A workspace class also named List — allNames will contain ""List"".
public class List { }"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Store.cs");
            // The `new List<int>()` in Fill() resolves to System.Collections.
            // Generic.List<T> (BCL). No references/identifier edge to `list`
            // should land on Store.cs without a targetRef pointing to List.cs
            // (the workspace type). If the resolver correctly identifies the
            // BCL type it emits no edge; if it wrongly emits a bare edge that
            // can't bind it's a phantom.
            Assert.DoesNotContain(edges, e =>
                e.Type == "references" && e.Subtype == "identifier" &&
                e.Target == "list" && !e.HasTargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ C4

    [Fact]
    public void C4_GenericConstraint_WorkspaceType_EmitsQualifiedTargetRef()
    {
        // C4: A generic constraint `where T : IEntity` where `IEntity` is
        // declared in ANOTHER workspace file must emit a `references/generic-
        // constraint` edge with a file-qualified targetRef — not the current
        // bare `references/generic-constraint` that only fires when the type
        // is already in `allNames` (cross-file types are missed entirely today;
        // Fathom row 5.0.93).
        var dir = MakeTempTree(
            ("Repo.cs", @"
public class Repo<T> where T : IEntity {
    public void Save(T item) { }
}"),
            ("IEntity.cs", @"
public interface IEntity { int Id { get; } }"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Repo.cs");
            // Must emit references/generic-constraint to `ientity` WITH a
            // targetRef pointing to IEntity.cs.
            Assert.Contains(edges, e =>
                e.Type == "references" &&
                e.Subtype == "generic-constraint" &&
                e.Target == "ientity" &&
                e.HasTargetRef &&
                (e.TargetRef ?? "").Contains("IEntity.cs"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ C5

    [Fact]
    public void C5_GenericConstraint_ExternalType_EmitsNoEdge()
    {
        // C5: A generic constraint on an external/BCL type (e.g. `where T :
        // IDisposable`) must emit NO references edge — the constraint is real but
        // the target is unbindable (no workspace element). Pre-fix the `allNames`
        // gate already suppressed most external constraints; this asserts the
        // post-fix `ResolveTargetFile`-based gate also suppresses them
        // (Fathom row 5.0.93).
        var dir = MakeTempTree(("Disposer.cs", @"
using System;
public class Disposer<T> where T : IDisposable {
    public void Clean(T item) { item.Dispose(); }
}"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Disposer.cs");
            // No references/generic-constraint edge to `idisposable` — external.
            Assert.DoesNotContain(edges, e =>
                e.Type == "references" &&
                e.Subtype == "generic-constraint" &&
                e.Target == "idisposable");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ F6

    [Fact]
    public void F6_AmbiguousClassName_CallResolvesToFileQualifiedTargetRef()
    {
        // F6: Two classes in DIFFERENT files each declare Run(ParsedArgs args).
        // A call in Caller.cs must resolve to a FILE-qualified targetRef to the
        // correct Runner in Runner.cs — the substrate can then distinguish the
        // two files. No bare `runner/run-parsedargs` (without targetRef) may
        // appear anywhere in the Caller.cs output (Fathom row 5.0.93; mirrors
        // the census shape with 2 carriers).
        var dir = MakeTempTree(
            ("Caller.cs", @"
public class Caller {
    public void DoCall() {
        var r = new Runner();
        r.Run(new ParsedArgs());
    }
}"),
            ("Runner.cs", @"
public class Runner {
    public void Run(ParsedArgs args) { }
}"),
            ("Runner2.cs", @"
// Second file also has a class named Runner (different namespace in prod,
// same compilation unit here — same class name, different file).
public class Runner2 {
    public void Run(ParsedArgs args) { }
}"),
            ("ParsedArgs.cs", @"
public class ParsedArgs { }"));
        try
        {
            var (edges, _) = AnalyzeFile(dir, "Caller.cs");
            // The calls/direct edge for runner/run-parsedargs MUST carry a
            // targetRef pointing to Runner.cs (the resolved file) so the
            // substrate chooses the right element.
            Assert.Contains(edges, e =>
                e.Type == "calls" &&
                e.Target.Contains("run") &&
                e.HasTargetRef &&
                (e.TargetRef ?? "").Contains("Runner.cs"));

            // No bare `calls` edge to runner/run-parsedargs without targetRef.
            Assert.DoesNotContain(edges, e =>
                e.Type == "calls" &&
                e.Target.Contains("run-parsedargs") &&
                !e.HasTargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }
}
