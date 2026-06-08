using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Pins the diagnosis of the 107 WebForms residual (H4b / Fathom 2026-06-07
/// sufficiency audit). A page handler accesses control fields declared in its
/// `.designer.cs` partial (`lbl.Text = "x"`, `cb.Items.Add(...)`).
///
/// Finding (verify-first, 2026-06-07): the residual is NOT a structural /
/// cross-file-partial gap — that path resolves (5.0.77). It is the control
/// field TYPES not resolving (`System.Web.UI.WebControls.*` / Telerik), exactly
/// like 5.0.76.a's System.Data bare-GAC refs.
///
///   Variant A (control types in-source) → the control bindings RESOLVE:
///     accessesField to the field + calls/property-set to the control property.
///   Variant B (control types undefined, mimicking System.Web unresolved) →
///     the field access still resolves, but the member-access edges DROP
///     (the field type is an error symbol).
///
/// So the fix family is framework-reference resolution (resolve System.Web like
/// 5.0.76.a resolved System.Data), not a new structural capability.
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
