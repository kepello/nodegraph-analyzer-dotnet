using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// F6 return-shape facets — `returnKind` + `returnsField`
/// (Fathom row l1a-stereotype-derivation-precise 3.1.1.1, Stage 2).
///
/// Spawns the built analyzer DLL on a temp source tree (Program.cs is a
/// top-level-statements program, not compiled into this test project) and
/// asserts the emitted facets. Covers every `returnKind` category + both
/// `returnsField` polarities, including the C# `return _backingField;`
/// convention the SemanticModel-backed extractor must handle.
/// </summary>
public class ReturnShapeTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(ReturnShapeTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Run the analyzer; return elementName → (returnKind, returnsField)
    /// for the artifact ending with <paramref name="fileSuffix"/>.</summary>
    private static Dictionary<string, (string? ReturnKind, bool? ReturnsField)>
        AnalyzeReturnShapes(string dir, string fileSuffix)
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

        var map = new Dictionary<string, (string?, bool?)>();
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
                    string? rk = el.TryGetProperty("returnKind", out var r) ? r.GetString() : null;
                    bool? rf = el.TryGetProperty("returnsField", out var f) ? f.GetBoolean() : null;
                    map[name] = (rk, rf);
                }
            }
        }
        return map;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-returnshape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void ReturnKind_CoversEveryCategory()
    {
        var dir = MakeTempTree(("Sample.cs", @"
using System.Collections.Generic;
using System.Threading.Tasks;
public enum Color { Red, Green }
public class Sample {
    private string _name = """";
    public bool IsReady() { return _name.Length > 0; }
    public async Task<bool> CheckAsync() { return _name != """"; }
    public void Reset() { _name = """"; }
    public int Count() { return 1; }
    public string Name2() { return _name; }
    public List<int> Items() { return new List<int>(); }
    public int[] Arr() { return new int[0]; }
    public Sample Clone() { return new Sample(); }
    public Color Hue() { return Color.Red; }
}"));
        try
        {
            var s = AnalyzeReturnShapes(dir, "Sample.cs");
            Assert.Equal("boolean", s["sample/isready"].ReturnKind);
            Assert.Equal("boolean", s["sample/checkasync"].ReturnKind); // Task<bool> unwrapped
            Assert.Equal("void", s["sample/reset"].ReturnKind);
            Assert.Equal("primitive", s["sample/count"].ReturnKind);
            Assert.Equal("primitive", s["sample/name2"].ReturnKind);
            Assert.Equal("collection", s["sample/items"].ReturnKind);
            Assert.Equal("collection", s["sample/arr"].ReturnKind);
            Assert.Equal("reference", s["sample/clone"].ReturnKind);
            Assert.Equal("primitive", s["sample/hue"].ReturnKind); // enum → primitive (semantic)
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReturnsField_DetectsBackingFieldAndThisReturns_NotComputed()
    {
        var dir = MakeTempTree(("Holder.cs", @"
public class Holder {
    private string _name = """";
    private int _count;
    // bare backing-field return (C# convention)
    public string GetName() { return _name; }
    // this.<field> return
    public int GetCount() { return this._count; }
    // computed — not a direct field return
    public string Upper() { return _name.ToUpper(); }
}"));
        try
        {
            var s = AnalyzeReturnShapes(dir, "Holder.cs");
            Assert.True(s["holder/getname"].ReturnsField, "return _name; is a direct field return");
            Assert.True(s["holder/getcount"].ReturnsField, "return this._count; is a direct field return");
            Assert.False(s["holder/upper"].ReturnsField, "return _name.ToUpper() is computed, not a field");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PropertyGetter_ExpressionBodied_BackingField_IsReturnsField()
    {
        var dir = MakeTempTree(("Person.cs", @"
public class Person {
    private string _first = """";
    private string _last = """";
    // expression-bodied getter over a backing field
    public string First => _first;
    // computed property (concatenation) — not a direct field return
    public string Full => _first + "" "" + _last;
}"));
        try
        {
            var s = AnalyzeReturnShapes(dir, "Person.cs");
            // getter elements carry the `:get` accessor suffix in the .NET analyzer.
            var first = s.First(kv => kv.Key.Contains("first") && kv.Value.ReturnKind != null);
            Assert.Equal("primitive", first.Value.ReturnKind);
            Assert.True(first.Value.ReturnsField, "First => _first is a direct field return");
            var full = s.First(kv => kv.Key.Contains("full") && kv.Value.ReturnKind != null);
            Assert.False(full.Value.ReturnsField, "Full => _first + _last is computed");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Setter_IsVoid_AndNotReturnsField()
    {
        var dir = MakeTempTree(("Box.cs", @"
public class Box {
    private int _v;
    public int V { get { return _v; } set { _v = value; } }
}"));
        try
        {
            var s = AnalyzeReturnShapes(dir, "Box.cs");
            var setter = s.First(kv => kv.Key.Contains("v") && kv.Key.Contains("set"));
            Assert.Equal("void", setter.Value.ReturnKind);
            Assert.False(setter.Value.ReturnsField);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Constructor_IsReference_AndNotReturnsField()
    {
        var dir = MakeTempTree(("Widget.cs", @"
public class Widget {
    private int _x;
    public Widget(int x) { _x = x; }
}"));
        try
        {
            var s = AnalyzeReturnShapes(dir, "Widget.cs");
            var ctor = s.First(kv => kv.Value.ReturnKind != null && kv.Key.Contains("constructor"));
            Assert.Equal("reference", ctor.Value.ReturnKind);
            Assert.False(ctor.Value.ReturnsField);
        }
        finally { Directory.Delete(dir, true); }
    }
}
