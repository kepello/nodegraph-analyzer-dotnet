using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// F7 `baseTypes` facet (Fathom row l1a-stereotype-derivation-precise
/// 3.1.1.1, Stage 5). The simple names of a type's base list, INCLUDING
/// external/framework bases the `extends`/`implements` edges drop because
/// they resolve to no workspace node — the signal the L1 `interfacer` rule
/// needs for functionally-named boundary classes.
/// </summary>
public class BaseTypesTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(BaseTypesTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Dictionary<string, string[]> AnalyzeBaseTypes(string dir, string fileSuffix)
    {
        var dll = AnalyzerDll();
        Assert.True(dll != null, "analyzer DLL not built — run `dotnet build src -c Debug`");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll!);
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(dir);
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write("{}");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        var map = new Dictionary<string, string[]>();
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
                    if (el.TryGetProperty("baseTypes", out var bt) && bt.ValueKind == JsonValueKind.Array)
                    {
                        map[name] = bt.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                    }
                }
            }
        }
        return map;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-basetypes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void BaseTypes_IncludesExternalFrameworkBase_AndInterfaces()
    {
        // `Form` is external (System.Windows.Forms) — the extends EDGE is dropped
        // (no workspace target), but the NAME must still surface as a facet.
        var dir = MakeTempTree(("OrderEntry.cs", @"
public class OrderEntry : System.Windows.Forms.Form, System.IDisposable {
    public void Dispose() {}
}
public class Plain {}"));
        try
        {
            var bt = AnalyzeBaseTypes(dir, "OrderEntry.cs");
            var entry = bt.First(kv => kv.Key.EndsWith("orderentry"));
            Assert.Contains("Form", entry.Value);          // external base, edge-dropped, facet-kept
            Assert.Contains("IDisposable", entry.Value);
            // A class with no base list emits no baseTypes facet.
            Assert.DoesNotContain(bt.Keys, k => k.EndsWith("plain"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
