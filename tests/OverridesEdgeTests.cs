using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for Fathom row
/// <c>dotnet-overrides-targetref-param-qualification</c> (5.0.112). The
/// `overrides` edge emitter (Program.cs, `TypeDeclarationSyntax overrideContainer`
/// block) built its target method key by hand from the overridden
/// <see cref="Microsoft.CodeAnalysis.IMethodSymbol"/>'s parameters via
/// <c>ToDisplayString()</c> — which fully-qualifies BCL/namespaced types
/// (`System.EventArgs`) — while the method-element natural key
/// (<see cref="NamingHelpers.GetParamSignature"/>, the single source of truth
/// shared with intra-class call resolution) uses the SHORT type name as
/// written in source (`EventArgs`). The two constructions diverged, so every
/// `overrides` edge whose overridden parameter type resolves to a
/// namespace-qualified display name pointed at a targetRef the substrate could
/// never resolve — 121 of 166 (73%) `overrides` edges dangled on the
/// EnvisionWeb corpus (probe-traced 2026-07-06), 116 of them at INTERNAL
/// targets (the `OnInit(EventArgs)` override family alone: 58 edges on
/// `system-eventargs` vs `eventargs`).
///
/// Fix: the overrides emitter now builds the overridden method's param
/// signature from its DECLARING SYNTAX via the same
/// <see cref="NamingHelpers.GetParamSignature"/> single source of truth used
/// for element natural-key construction, instead of re-deriving it from the
/// symbol's display string.
/// </summary>
public class OverridesEdgeTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(OverridesEdgeTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record OverrideEdge(string ElementName, string TargetName, string? TargetRef);

    /// <summary>Run the analyzer on <paramref name="dir"/> and return
    /// (every element's naturalKey, keyed by "artifactId#name") alongside every
    /// `overrides` edge (owning element name, targetName, targetRef) found
    /// anywhere in the run — so a test can assert an edge's targetRef equals
    /// the actual target element's own naturalKey, not just that a targetRef
    /// is present.</summary>
    private static (Dictionary<string, string> NaturalKeysByQualifiedName, List<OverrideEdge> Overrides)
        AnalyzeOverrides(string dir)
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
        var overrides = new List<OverrideEdge>();
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
                        if (type != "overrides") continue;
                        var targetName = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                        var targetRef = e.TryGetProperty("targetRef", out var tr) ? tr.GetString() : null;
                        overrides.Add(new OverrideEdge(name, targetName, targetRef));
                    }
                }
            }
        }
        return (naturalKeys, overrides);
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void ClassOverride_BclParameterType_TargetRefMatchesTargetElementNaturalKey()
    {
        // The EnvisionWeb `OnInit(EventArgs)` shape: a cross-file override where
        // the overridden parameter is a BCL type. Pre-fix the emitted targetRef
        // qualified the param with its namespace (`...oninit-system-eventargs`)
        // while the target element's own natural key uses the short form
        // written in source (`...oninit-eventargs`) — the edge dangles.
        var dir = MakeTempTree(
            ("Base.cs", @"
using System;
public abstract class AuthenticatedModalBase {
    public virtual void OnInit(EventArgs e) { }
}"),
            ("Derived.cs", @"
using System;
public class LoginModal : AuthenticatedModalBase {
    public override void OnInit(EventArgs e) { }
}"));
        try
        {
            var (naturalKeys, overrides) = AnalyzeOverrides(dir);

            // The `overrides` edge is attached to the CLASS element (the type
            // declaration is what ExtractRelationships walks members of), not
            // the per-method element.
            var edge = Assert.Single(overrides, o => o.ElementName == "loginmodal");
            Assert.NotNull(edge.TargetRef);

            var baseArtifactId = Path.Combine(dir, "Base.cs");
            var expectedTargetKey = naturalKeys[$"{baseArtifactId}#authenticatedmodalbase/oninit-eventargs"];
            Assert.False(string.IsNullOrEmpty(expectedTargetKey), "target element must exist and carry a naturalKey");

            // The resolvability contract: the emitted targetRef must be
            // byte-identical to the target element's own naturalKey, not just
            // superficially similar.
            Assert.Equal(expectedTargetKey, edge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ClassOverride_InternalNamespacedParameterType_TargetRefMatchesTargetElementNaturalKey()
    {
        // Pin the other direction: an INTERNAL parameter type declared inside a
        // namespace. The overridden parameter's symbol display name is
        // namespace-qualified (`MyApp.Events.CustomEventArgs`); the source-written
        // short form (`CustomEventArgs`) is what the element natural key uses.
        var dir = MakeTempTree(
            ("Events.cs", @"
namespace MyApp.Events {
    public class CustomEventArgs : System.EventArgs { }
}"),
            ("Base.cs", @"
using MyApp.Events;
namespace MyApp {
    public abstract class WidgetBase {
        public virtual void Render(CustomEventArgs e) { }
    }
}"),
            ("Derived.cs", @"
using MyApp.Events;
namespace MyApp {
    public class Widget : WidgetBase {
        public override void Render(CustomEventArgs e) { }
    }
}"));
        try
        {
            var (naturalKeys, overrides) = AnalyzeOverrides(dir);

            var edge = Assert.Single(overrides, o => o.ElementName == "widget");
            Assert.NotNull(edge.TargetRef);

            var baseArtifactId = Path.Combine(dir, "Base.cs");
            var expectedTargetKey = naturalKeys[$"{baseArtifactId}#widgetbase/render-customeventargs"];
            Assert.False(string.IsNullOrEmpty(expectedTargetKey), "target element must exist and carry a naturalKey");

            Assert.Equal(expectedTargetKey, edge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }
}
