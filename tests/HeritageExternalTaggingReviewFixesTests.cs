using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for the reviewer-round-2 fixes on top of the
/// `ResolveTypeRef` external-base fix (Fathom rows 3.1.0.15/3.1.0.16/3.1.0.17,
/// crit 4 — see <c>HeritageExternalTaggingTests</c> for the original defect).
/// The reviewer confirmed the core design (symbol-based in-source/external/
/// unresolved verdict, no in-source loss, no over-tagging) and flagged 4
/// follow-on defects in that implementation:
///
/// 1. The drop-path Limitation `kind` (`"unresolved-type-ref"`) was never
///    registered in the taxonomy (`nodegraph-limitations` schema/overlay,
///    `nodegraph-analysis` conformance types) — `insertLimitation` throws,
///    `ingestFromArtifacts` swallows the throw, and every drop-path
///    Limitation VANISHES silently at overlay ingest. Fixed by routing
///    through the already-registered `"unresolved-reference"` kind.
/// 2. A generic TYPE PARAMETER (`T` in `T Get&lt;T&gt;(T item)`, or `U` in
///    `where T : U`) resolved to an `ITypeParameterSymbol` — not an
///    `INamedTypeSymbol` — and fell into the SAME `null` verdict bucket as a
///    genuinely-unresolvable reference, wrongly emitting an
///    `unresolved-reference` Limitation for a reference shape that is
///    grammatically not-applicable (a type parameter is fully resolved; it
///    just isn't a type-reference EDGE target). Fixed by giving
///    `ResolveTypeRef` a 4th, distinct not-applicable verdict that emits
///    neither an edge nor a Limitation.
/// 3. An external GENERIC target's FQN carried a stray `global` token
///    (`List&lt;MyUserType&gt;` → `...list-global-myusertype`) because only a
///    LEADING `global::` was stripped from the display string, missing the
///    one Roslyn emits in front of a generic type ARGUMENT. Fixed by
///    building the FQN with
///    `SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(Omitted)`
///    so no `global::` is emitted anywhere in the string, not just stripped
///    from the front.
/// </summary>
public class HeritageExternalTaggingReviewFixesTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(HeritageExternalTaggingReviewFixesTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-heritage-ext-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private sealed record Edge(
        string ElementName, string Type, string? Subtype, string Target,
        bool External, string? Provenance);

    /// <summary>Run the analyzer on <paramref name="dir"/>; return every
    /// emitted edge (owning element name, type, subtype, targetName,
    /// external/provenance from metadata) plus every Limitation `kind`
    /// recorded anywhere in the run.</summary>
    private static (List<Edge> Edges, List<string> LimitationKinds) Analyze(string dir)
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

        var edges = new List<Edge>();
        var limitationKinds = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "artifact") continue;
                var artifact = root.GetProperty("artifact");

                if (artifact.TryGetProperty("limitations", out var lims) && lims.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lim in lims.EnumerateArray())
                    {
                        if (lim.TryGetProperty("kind", out var k)) limitationKinds.Add(k.GetString() ?? "");
                    }
                }

                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                foreach (var el in elements.EnumerateArray())
                {
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
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
                        edges.Add(new Edge(name, type, subtype, target, external, provenance));
                    }
                }
            }
        }
        return (edges, limitationKinds);
    }

    // ---------- FINDING #1: drop-path Limitation kind must be registered ----------

    [Fact]
    public void UnresolvableHeritageBase_RecordsRegisteredLimitationKind_UnresolvedReference()
    {
        // `TotallyUndefinedBase` is syntactically a base-type reference but
        // has no declaration anywhere and isn't a real BCL/NuGet symbol —
        // Roslyn resolves it to an `IErrorTypeSymbol` (`TypeKind.Error`),
        // the genuinely-UNRESOLVED verdict branch. Pre-fix this recorded
        // `kind: "unresolved-type-ref"`, which is NOT in the registered
        // Limitation taxonomy (`nodegraph-limitations` schema/overlay,
        // `nodegraph-analysis` conformance types) — `insertLimitation` throws
        // at ingest and `ingestFromArtifacts` swallows it, so the record
        // silently vanishes rather than surfacing as a conservation gap.
        var dir = MakeTempTree(("G.cs", @"
public class G : TotallyUndefinedBase {
}"));
        try
        {
            var (edges, limitationKinds) = Analyze(dir);
            Assert.DoesNotContain(edges, e => e.ElementName == "g" && (e.Type == "extends" || e.Type == "implements"));
            Assert.Contains("unresolved-reference", limitationKinds);
            Assert.DoesNotContain("unresolved-type-ref", limitationKinds);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- FINDING #2: type parameters are NOT-APPLICABLE, not unresolved ----------

    [Fact]
    public void MethodTypeParameter_ReturnAndParameterType_NoEdgeNoLimitation()
    {
        // `T` in `T Get<T>(T item)` resolves to an `ITypeParameterSymbol` — a
        // fully-resolved symbol, not a resolution failure. It is
        // not-applicable for a `references` return-type/parameter-type edge
        // (there is no element to point at) and must emit NEITHER an edge
        // NOR a Limitation. Pre-fix: `ResolveTypeRef` only recognized
        // `INamedTypeSymbol`, so a type parameter fell into the same `null`
        // verdict as a genuine resolution failure, minting 2 spurious
        // `unresolved-reference` Limitations (return-type + parameter-type).
        var dir = MakeTempTree(("H.cs", @"
public class H {
    public T Get<T>(T item) => item;
}"));
        try
        {
            var (edges, limitationKinds) = Analyze(dir);
            Assert.DoesNotContain(edges, e =>
                e.Type == "references" && (e.Subtype == "return-type" || e.Subtype == "parameter-type"));
            // `references-free-compilation` is unrelated pre-existing baseline
            // noise (no .csproj for this temp fixture, not a type-ref
            // resolution outcome) — the assertion pins the type-parameter
            // specifically: a resolved `ITypeParameterSymbol` is
            // not-applicable, never `unresolved-reference`.
            Assert.DoesNotContain("unresolved-reference", limitationKinds);
            Assert.DoesNotContain("unresolved-type-ref", limitationKinds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GenericConstraint_TypeParameterBound_NoEdgeNoLimitation()
    {
        // `where T : U` — the constraint type `U` is itself a type
        // parameter, not a real base type. Heritage can't syntactically
        // reference a type parameter (only the constraint-clause site can),
        // so this pins the generic-constraint call site specifically.
        var dir = MakeTempTree(("F.cs", @"
public class F<T, U> where T : U {
}"));
        try
        {
            var (edges, limitationKinds) = Analyze(dir);
            Assert.DoesNotContain(edges, e => e.Type == "references" && e.Subtype == "generic-constraint");
            Assert.DoesNotContain("unresolved-reference", limitationKinds);
            Assert.DoesNotContain("unresolved-type-ref", limitationKinds);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- FINDING #3: external generic target must not carry a stray `global` token ----------

    [Fact]
    public void ExternalGenericReturnType_TargetName_HasNoGlobalToken()
    {
        // `System.Collections.Generic.List<InSourceType>` is external (no
        // in-source declaration for `List<T>` itself) — Roslyn's
        // `FullyQualifiedFormat` display string prefixes the GENERIC TYPE
        // ARGUMENT with `global::` (`...List<global::InSourceType>`), not
        // just the outer name. Pre-fix only a LEADING `global::` was
        // stripped, so the canonicalized target carried a stray `global`
        // segment (`...list-global-insourcetype`).
        var dir = MakeTempTree(("K.cs", @"
public class InSourceType {}
public class K {
    public System.Collections.Generic.List<InSourceType> M() => new System.Collections.Generic.List<InSourceType>();
}"));
        try
        {
            var (edges, _) = Analyze(dir);
            var refEdge = Assert.Single(edges, e =>
                e.Type == "references" && e.Subtype == "return-type" && e.ElementName == "k/m");
            Assert.True(refEdge.External, "System.Collections.Generic.List<T> has no in-source declaration");
            Assert.DoesNotContain("global", refEdge.Target);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- FINDING #3a: external generic edges are KEPT, not suppressed ----------

    [Fact]
    public void ExternalGenericReturnType_EdgeIsKept_NotSuppressed()
    {
        // Explicit decision pin (not a regression per se): an external
        // generic `references` edge (`List<T>` return type) is a CORRECT
        // tagged external fact, consistent with a non-generic external
        // reference (`HttpClient`) — it must NOT be special-case-suppressed
        // as "redundant" with the identifier-path type-argument edge.
        var dir = MakeTempTree(("L.cs", @"
public class InSourceType2 {}
public class L {
    public System.Collections.Generic.List<InSourceType2> M() => new System.Collections.Generic.List<InSourceType2>();
}"));
        try
        {
            var (edges, _) = Analyze(dir);
            var refEdge = Assert.Single(edges, e =>
                e.Type == "references" && e.Subtype == "return-type" && e.ElementName == "l/m");
            Assert.True(refEdge.External);
            Assert.Equal("external-library", refEdge.Provenance);
        }
        finally { Directory.Delete(dir, true); }
    }
}
