using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for the partial-class member-index gap (Fathom
/// <c>dotnet-l0-partial-class-field-index</c>). The intra-class member index
/// (<c>Program.IntraClass.BuildIndex</c>) was built from a SINGLE
/// <c>TypeDeclarationSyntax</c> — the partial declaration in the file being
/// walked — so a member declared in ANOTHER partial file (the canonical
/// WinForms case: controls live in <c>Form.Designer.cs</c>, handlers in
/// <c>Form.cs</c>) was invisible to the index. Consequences:
///   • <c>accessesField</c> reads/writes to those fields were never emitted →
///     LCOM4 cohesion + field-coupling silently too optimistic, and the
///     <c>mutator</c>/<c>command</c> stereotypes under-fired.
///   • The <c>returnsField</c> fact (fed by the same index) missed getters that
///     return a field declared in another partial → <c>accessor</c> under-fired.
///
/// The fix unions the member index across ALL partial declarations of the type
/// (via the type symbol's <c>DeclaringSyntaxReferences</c>, which span the
/// shared multi-file compilation). These tests spawn the built analyzer over a
/// two-file temp tree, exactly the WinForms split, and assert the cross-file
/// member access is now seen.
/// </summary>
public class PartialClassMemberIndexTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(PartialClassMemberIndexTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record ElementInfo(
        string Name,
        List<(string Type, string? Subtype, string Target)> Edges,
        bool? ReturnsField);

    private static Dictionary<string, ElementInfo> Analyze(string dir, string fileSuffix)
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

        var map = new Dictionary<string, ElementInfo>();
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
                    var edges = new List<(string, string?, string)>();
                    if (el.TryGetProperty("edges", out var elEdges) && elEdges.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in elEdges.EnumerateArray())
                        {
                            var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                            var subtype = e.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                            var target = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                            edges.Add((type, subtype, target));
                        }
                    }
                    bool? returnsField = el.TryGetProperty("returnsField", out var rf) && rf.ValueKind == JsonValueKind.True ? true
                        : el.TryGetProperty("returnsField", out var rf2) && rf2.ValueKind == JsonValueKind.False ? false
                        : (bool?)null;
                    map[name] = new ElementInfo(name, edges, returnsField);
                }
            }
        }
        return map;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void AccessesField_CrossPartialFile_IsRecorded()
    {
        // The WinForms split: control field in Form.Designer.cs, handler that
        // touches it in Form.cs. Pre-fix: the index built for Form.cs never saw
        // BtnRun, so `BtnRun.Enabled = ...` recorded NO accessesField edge.
        var dir = MakeTempTree(
            ("Form1.Designer.cs", @"
namespace App {
    partial class Form1 {
        private System.Windows.Forms.Button BtnRun;
        private System.Windows.Forms.Label LblStatus;
    }
}"),
            ("Form1.cs", @"
namespace App {
    public partial class Form1 {
        private void EnableDisableControls(bool bEnable) { BtnRun.Enabled = bEnable; }
        private void ProcessEnd() { LblStatus.Text = ""done""; }
    }
}"));
        try
        {
            var elems = Analyze(dir, "Form1.cs");
            Assert.True(elems.ContainsKey("form1/enabledisablecontrols-bool"), "handler element emitted");
            var ed = elems["form1/enabledisablecontrols-bool"];
            Assert.Contains(ed.Edges, e => e.Type == "accessesField" && e.Target.EndsWith("btnrun"));
            var pe = elems["form1/processend"];
            Assert.Contains(pe.Edges, e => e.Type == "accessesField" && e.Target.EndsWith("lblstatus"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReturnsField_CrossPartialFile_IsDetected()
    {
        // A getter in one partial returns a field declared in another partial.
        // The returnsField fact (same member index) must see the field.
        var dir = MakeTempTree(
            ("Holder.State.cs", @"
namespace App {
    partial class Holder {
        private string _name;
    }
}"),
            ("Holder.cs", @"
namespace App {
    public partial class Holder {
        public string Name { get { return _name; } }
    }
}"));
        try
        {
            var elems = Analyze(dir, "Holder.cs");
            Assert.True(elems.ContainsKey("holder/name/get"), "getter accessor emitted");
            Assert.Equal(true, elems["holder/name/get"].ReturnsField);
        }
        finally { Directory.Delete(dir, true); }
    }
}
