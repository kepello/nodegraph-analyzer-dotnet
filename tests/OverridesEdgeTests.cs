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
    public void OverridesEdge_IsSourcedAtTheOVERRIDING_METHOD_notTheClass()
    {
        // Fathom row `overrides-edge-source-kind-diverges` (3.1.1.6, crit 4).
        //
        // The contract is stated in Program.cs directly above the emission block:
        //     "Direction: source = OVERRIDING method (this class's member);
        //                target = OVERRIDDEN method (parent class or interface member).
        //      Same convention as TS analyzer."
        //
        // It was documented and then violated. ExtractRelationships is called ONCE PER
        // ELEMENT NODE and its return value is sourced AT THAT NODE; the block was gated on
        // `node is TypeDeclarationSyntax`, so every `overrides` edge came out sourced at the
        // CLASS. TS emits method->method; .NET emitted class->method.
        //
        // Blast radius: scenario entries are METHODS. A class-sourced edge can never match
        // one, so every `overrides`-keyed feature (L7a alternate-flow grouping, the
        // interface-rooted merge bound) was a SILENT NO-OP on .NET while passing every TS
        // test. Proven on EnvisionWeb: 132 overrides edges, ZERO usable polymorphic families.
        //
        // This test pins the SOURCE. Nothing did before — the pre-existing tests assert only
        // on the TARGET key, which is why the divergence survived.
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
            var (_, overrides) = AnalyzeOverrides(dir);

            // The edge MUST be owned by the overriding METHOD element.
            Assert.Single(overrides, o => o.ElementName == "loginmodal/oninit-eventargs");

            // And MUST NOT be owned by the class element — a class does not override a method.
            Assert.DoesNotContain(overrides, o => o.ElementName == "loginmodal");
        }
        finally { Directory.Delete(dir, recursive: true); }
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

            // The `overrides` edge is owned by the overriding METHOD element.
            // (This assertion previously read `ElementName == "loginmodal"` — the CLASS —
            // and so PINNED Fathom row `overrides-edge-source-kind-diverges` (3.1.1.6) as
            // if it were intended behaviour. A prior author noticed the class-sourcing,
            // wrote it down as an observation, and locked it in, rather than recognising it
            // contradicted the contract stated in Program.cs directly above the emission
            // block. That is why a 3rd-instance cross-analyzer divergence survived: the bug
            // had a test defending it.)
            var edge = Assert.Single(overrides, o => o.ElementName == "loginmodal/oninit-eventargs");
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

            // Owned by the overriding METHOD, not the class (row 3.1.1.6 — see above).
            var edge = Assert.Single(overrides, o => o.ElementName == "widget/render-customeventargs");
            Assert.NotNull(edge.TargetRef);

            var baseArtifactId = Path.Combine(dir, "Base.cs");
            var expectedTargetKey = naturalKeys[$"{baseArtifactId}#widgetbase/render-customeventargs"];
            Assert.False(string.IsNullOrEmpty(expectedTargetKey), "target element must exist and carry a naturalKey");

            Assert.Equal(expectedTargetKey, edge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NestedInterfaceImplementation_SameFile_TargetNameCarriesOuterQualifier()
    {
        // Fathom row 3.1.0.15 (follow-on) — the SMTP.cs survivor, the last RC3
        // instance: a clean rebuild of a real C# app under the armed strict
        // edge gate went from 34 unresolvable edges down to exactly 1, this
        // shape. The implementing method lives in a NESTED type
        // (`Smtp.SmtpWrapper`) and implements an interface (`Smtp.ISmtpClient`)
        // ALSO nested under the same outer scope. The target element's own
        // key carries the outer qualifier (`smtp/ismtpclient/send-mailmessage`)
        // because `GetQualifiedRawName` walks the nesting; the `overrides`
        // emitter built its `parentQualified` from the parent TYPE's SHORT
        // `.Name` (`ismtpclient`) instead, so the emitted `targetName` came
        // out unqualified (`ismtpclient/send-mailmessage`).
        //
        // Same-file edges emit NO targetRef — the ingest layer synthesizes a
        // self-file key from `targetName` alone — so the unqualified emit was
        // a PERMANENT dangler, not just a superficial mismatch.
        var dir = MakeTempTree(("Smtp.cs", @"
public class MailMessage {}
public class Smtp {
    public interface ISmtpClient {
        void Send(MailMessage message);
    }
    public class SmtpWrapper : ISmtpClient {
        public void Send(MailMessage message) { }
    }
}"));
        try
        {
            var (naturalKeys, overrides) = AnalyzeOverrides(dir);

            var edge = Assert.Single(overrides, o => o.ElementName == "smtp/smtpwrapper/send-mailmessage");

            // Same-file: no targetRef by convention (bare targetName resolves
            // within the emitting artifact via ingest's self-file synthesis).
            Assert.Null(edge.TargetRef);

            var artifactId = Path.Combine(dir, "Smtp.cs");
            var expectedTargetKey = naturalKeys[$"{artifactId}#smtp/ismtpclient/send-mailmessage"];
            Assert.False(string.IsNullOrEmpty(expectedTargetKey), "target element must exist and carry a naturalKey");

            // The self-file key-synthesis contract: targetName itself must
            // carry the outer qualifier, or ingest lands on the wrong key.
            Assert.Equal("smtp/ismtpclient/send-mailmessage", edge.TargetName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NestedBaseType_CrossFile_TargetRefMatchesTargetElementNaturalKey()
    {
        // Fathom row 3.1.0.15 (follow-on): the cross-file half of the same
        // defect. The overridden method's parent TYPE (`WidgetBase`) is
        // nested inside `Container`, declared in a DIFFERENT file than the
        // override. The `targetRef` is built via
        // `MakeNaturalKey(parentTargetFile, canonical)` — `canonical` must
        // carry the `container/widgetbase` qualifier or the ref permanently
        // dangles cross-file, same failure shape as the same-file case but
        // through the targetRef path instead of ingest's self-file synthesis.
        var dir = MakeTempTree(
            ("Base.cs", @"
public class Container {
    public abstract class WidgetBase {
        public virtual void Render() { }
    }
}"),
            ("Derived.cs", @"
public class Widget : Container.WidgetBase {
    public override void Render() { }
}"));
        try
        {
            var (naturalKeys, overrides) = AnalyzeOverrides(dir);

            var edge = Assert.Single(overrides, o => o.ElementName == "widget/render");
            Assert.NotNull(edge.TargetRef);

            var baseArtifactId = Path.Combine(dir, "Base.cs");
            var expectedTargetKey = naturalKeys[$"{baseArtifactId}#container/widgetbase/render"];
            Assert.False(string.IsNullOrEmpty(expectedTargetKey), "target element must exist and carry a naturalKey");

            // Byte-identical to the target element's own naturalKey, not
            // just superficially similar.
            Assert.Equal(expectedTargetKey, edge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ClassOverride_CsprojCompileIncludeCasingDivergesFromDisk_TargetRefStillMatchesTargetElementNaturalKey()
    {
        // Fathom row 3.1.0.15 (follow-on): every OTHER cross-file targetRef
        // site in Program.cs routes the Roslyn declaring path through
        // `canonicalizeFilePath` (the on-disk-casing normalizer) before
        // keying/comparing; the `overrides` emitter was the one outlier,
        // building its targetRef straight off the RAW
        // `SyntaxTree.FilePath`.
        //
        // The divergence this pins is REAL, not hypothetical, even on a
        // case-preserving macOS filesystem: MSBuild's `<Compile Include>`
        // path resolution is purely textual (Path.Combine over the
        // declared string), never filesystem-queried, so a project that
        // declares `<Compile Include="base.cs" />` against an on-disk
        // `Base.cs` loads and compiles FINE (case-insensitive FS) while
        // `document.FilePath` — and therefore every declaring
        // `SyntaxTree.FilePath` resolved off that project's Compilation —
        // carries the LOWERCASE declared spelling. The element natural key
        // for `Base.cs` (built from the directory-walked, on-disk-case
        // path) still reads `Base.cs`. Pre-fix, the raw-keyed `overrides`
        // targetRef read `...:base.cs#...` — a permanent dangle against the
        // `...:Base.cs#...` naturalKey. (Witnessed RED against the
        // pre-fix build: the `extends` edge on this same fixture — which
        // already routed through `ResolveTypeRef`/`canonicalizeFilePath` —
        // resolved correctly, isolating `overrides` as the sole outlier.)
        //
        // Requires MSBuildWorkspace to actually load the project (not just
        // the references-free sharedCompilation fallback), so `Derived.cs`
        // — the overriding file — is spelled identically in both
        // `<Compile Include>` and on disk (it must Ordinal-match the
        // directory walk to land in the project map); only `Base.cs`, the
        // OVERRIDDEN parent's file, carries the diverging csproj casing.
        // No PackageReferences, so no `dotnet restore` is required for
        // MSBuildWorkspace to open it.
        var dir = MakeTempTree(
            ("App.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove=""**/*.cs"" />
    <Compile Include=""base.cs"" />
    <Compile Include=""Derived.cs"" />
  </ItemGroup>
</Project>"),
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

            var edge = Assert.Single(overrides, o => o.ElementName == "loginmodal/oninit-eventargs");
            Assert.NotNull(edge.TargetRef);

            // The target element's own naturalKey is keyed by the
            // DIRECTORY-WALKED (on-disk-case) path — "Base.cs", not the
            // csproj's declared "base.cs".
            var baseArtifactId = Path.Combine(dir, "Base.cs");
            var expectedTargetKey = naturalKeys[$"{baseArtifactId}#authenticatedmodalbase/oninit-eventargs"];
            Assert.False(string.IsNullOrEmpty(expectedTargetKey), "target element must exist and carry a naturalKey");

            // Byte-identical, not just case-insensitively equal — a
            // case-insensitive Assert here would pass for the pre-fix
            // bug too (storage keys are case-sensitive strings).
            Assert.Equal(expectedTargetKey, edge.TargetRef);
            Assert.Contains("Base.cs#", edge.TargetRef);
            Assert.DoesNotContain("base.cs#", edge.TargetRef);
        }
        finally { Directory.Delete(dir, true); }
    }
}
