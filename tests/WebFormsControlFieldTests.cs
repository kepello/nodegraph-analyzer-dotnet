using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Pins TWO mechanical facts behind the 107 WebForms residual (H4b / Fathom
/// 2026-06-07 sufficiency audit). A page handler accesses control fields
/// declared in its `.designer.cs` partial (`lbl.Text = "x"`, `cb.Items.Add(...)`).
///
///   Variant A (field declared + control type resolves) → the bindings RESOLVE:
///     accessesField to the field + calls/property-set to the control property.
///     (Cross-file partial resolution holds — NOT a 5.0.77-class gap.)
///   Variant B (field declared, control type an error symbol) → the field
///     access still resolves, but the member-access edges DROP.
///
/// DIAGNOSIS CORRECTED 2026-06-08 (deeper verify-first on the real corpus —
/// see work row `dotnet-system-web-framework-ref-resolution` 5.0.87): on
/// EnvisionWeb the controls live in `EnvisionAnywhere.com`, a WEB SITE PROJECT
/// with NO `.designer.cs` at all (390 `.ascx`, 0 designer files) — the field
/// (`lbl`) is declared ONLY in the `.ascx` markup, so the codebehind identifier
/// is UNDECLARED and the access never reaches Variant B's situation. System.Web
/// itself is present (the WAP references it; the WSP loader adds the framework
/// pack). The real fix is generated-companion control-field SYNTHESIS: parse
/// the `.ascx` markup, synthesize the field declarations, inject them into the
/// WSP compilation — after which Variant A here proves the bindings emit.
/// These tests remain the regression pins for that fix's two halves
/// (declaration present → bindings emit; type unresolved → honest drop).
/// The synthesis itself SHIPPED with 5.0.87 (Program.WebFormsMarkup.cs); its
/// end-to-end pins live in WebFormsCompanionIntegrationTests. These two
/// variants keep pinning the Web APPLICATION Project shape (real
/// `.designer.cs` on disk), which the synthesis must never disturb.
/// </summary>
public class WebFormsControlFieldTests
{
    private const string HandlerFile = @"
public partial class ReportPage {
    public void SetLabels() {
        lbl.Text = ""Title"";        // control-field property write
        cb.Items.Add(""x"");          // control-field method call
    }
}";

    [Fact]
    public void InSourceControlTypes_ControlBindingsResolve()
    {
        var dir = MakeTempTree(
            ("ReportPage.aspx.cs", HandlerFile),
            ("ReportPage.aspx.designer.cs", @"
public partial class ReportPage {
    protected Label lbl;
    protected ComboBox cb;
}"),
            ("Controls.cs", @"
public class Label { public string Text { get; set; } }
public class ItemList { public void Add(object o) { } }
public class ComboBox { public ItemList Items { get { return null; } } }"));
        try
        {
            var edges = AnalyzeEdges(dir, "ReportPage.aspx.cs");
            // Cross-file partial field access resolves (5.0.77).
            Assert.Contains(edges, e => e.Type == "accessesField" && e.Target.Contains("lbl"));
            Assert.Contains(edges, e => e.Type == "accessesField" && e.Target.Contains("cb"));
            // The control binding resolves when the control TYPE resolves.
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-set"
                && e.Target.Contains("label") && e.Target.Contains("text"));
            Assert.Contains(edges, e => e.Type == "calls" && e.Target.Contains("itemlist") && e.Target.Contains("add"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnresolvedControlTypes_FieldResolvesButMemberAccessDrops()
    {
        // Control types NOT defined (mimics System.Web.UI.WebControls unresolved)
        // → `lbl`/`cb` fields are typed by an error symbol.
        var dir = MakeTempTree(
            ("ReportPage.aspx.cs", HandlerFile),
            ("ReportPage.aspx.designer.cs", @"
public partial class ReportPage {
    protected Label lbl;     // Label undefined → error symbol
    protected ComboBox cb;   // ComboBox undefined → error symbol
}"));
        try
        {
            var edges = AnalyzeEdges(dir, "ReportPage.aspx.cs");
            // The FIELD access still resolves (the field symbol exists).
            Assert.Contains(edges, e => e.Type == "accessesField" && e.Target.Contains("lbl"));
            // But the member-access bindings DROP — this IS the 107 residual.
            Assert.DoesNotContain(edges, e => e.Type == "calls" && e.Subtype == "property-set"
                && e.Target.Contains("text"));
            Assert.DoesNotContain(edges, e => e.Type == "calls" && e.Target.Contains("add"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(WebFormsControlFieldTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static List<(string Type, string? Subtype, string Target, bool External)>
        AnalyzeEdges(string dir, string fileSuffix)
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

        var edges = new List<(string, string?, string, bool)>();
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
                        var ty = e.TryGetProperty("type", out var tyl) ? tyl.GetString() ?? "" : "";
                        var st = e.TryGetProperty("subtype", out var stl) ? stl.GetString() : null;
                        var tn = e.TryGetProperty("targetName", out var tnl) ? tnl.GetString() ?? "" : "";
                        var ext = e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
                            && md.TryGetProperty("external", out var ex) && ex.ValueKind == JsonValueKind.True;
                        edges.Add((ty, st, tn, ext));
                    }
                }
            }
        }
        return edges;
    }
}
