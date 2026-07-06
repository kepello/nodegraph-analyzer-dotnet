using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// C# 9+ <c>record</c> / <c>record struct</c> coverage (Fathom row
/// <c>dotnet-record-elements-not-emitted</c> 5.0.124.2b). Three dispatch
/// sites (<see cref="GetDeclarationName"/>-equivalent naming,
/// <c>GetElementType</c>, and <c>MapToDeclarable</c>) switched on Roslyn
/// syntax-node TYPE with no <c>RecordDeclarationSyntax</c> case, so a
/// record/record-struct declaration was gated out before an element was ever
/// created (`GetElementType(node) == null` in the canonical-naming pass) —
/// not mislabeled, simply invisible.
/// </summary>
public class RecordDeclarationTests
{
    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(RecordDeclarationTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static List<JsonElement> AnalyzeElements(string dir, string fileSuffix)
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

        var elements = new List<JsonElement>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "artifact") continue;
            var artifact = root.GetProperty("artifact");
            var id = artifact.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (!id.Replace('\\', '/').EndsWith(fileSuffix)) continue;
            if (!artifact.TryGetProperty("elements", out var els)) continue;
            foreach (var el in els.EnumerateArray()) elements.Add(el.Clone());
        }
        return elements;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    [Fact]
    public void RecordClass_Positional_EmitsClassElement()
    {
        // Pre-fix: GetElementType(RecordDeclarationSyntax) falls to `_ => null`,
        // gating the node out at the canonical-naming pass — ZERO elements for
        // `Person`, not a mislabeled one.
        var dir = MakeTempTree(("Person.cs", @"
public record Person(string Name, int Age);"));
        try
        {
            var elements = AnalyzeElements(dir, "Person.cs");
            var person = elements.FirstOrDefault(e =>
                e.TryGetProperty("bareName", out var bn) && bn.GetString() == "Person");
            Assert.True(person.ValueKind != JsonValueKind.Undefined, "no element emitted for record `Person` at all");
            Assert.Equal("class", person.GetProperty("kind").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RecordClass_ExplicitBody_EmitsElementAndMemberChild()
    {
        var dir = MakeTempTree(("Point.cs", @"
public record class Point { public int X { get; init; } }"));
        try
        {
            var elements = AnalyzeElements(dir, "Point.cs");
            var point = elements.FirstOrDefault(e =>
                e.TryGetProperty("bareName", out var bn) && bn.GetString() == "Point");
            Assert.True(point.ValueKind != JsonValueKind.Undefined, "no element emitted for `record class Point`");
            Assert.Equal("class", point.GetProperty("kind").GetString());

            var x = elements.FirstOrDefault(e =>
                e.TryGetProperty("bareName", out var bn) && bn.GetString() == "X");
            Assert.True(x.ValueKind != JsonValueKind.Undefined, "explicit member `X` not captured as a child element");
            Assert.Equal("property", x.GetProperty("kind").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RecordStruct_Positional_MapsToStructNotClass()
    {
        var dir = MakeTempTree(("Coordinates.cs", @"
public record struct Coordinates(double Lat, double Lng);"));
        try
        {
            var elements = AnalyzeElements(dir, "Coordinates.cs");
            var coords = elements.FirstOrDefault(e =>
                e.TryGetProperty("bareName", out var bn) && bn.GetString() == "Coordinates");
            Assert.True(coords.ValueKind != JsonValueKind.Undefined, "no element emitted for `record struct Coordinates`");
            Assert.Equal("struct", coords.GetProperty("kind").GetString());

            var metadata = coords.GetProperty("metadata");
            Assert.Equal("type", metadata.GetProperty("role").GetString());
            var flavors = metadata.GetProperty("flavors");
            Assert.Equal("struct", flavors.GetProperty("typeKind").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RecordClass_Positional_ParametersSurfaceAsParameterElements()
    {
        // Out of scope per the row: positional record params are ParameterSyntax
        // nodes in Roslyn's syntax tree (no synthesized PropertyDeclarationSyntax
        // exists without the semantic model), so they surface as "parameter"
        // elements, not "property" — documented here as a factual pin, not a bug.
        var dir = MakeTempTree(("Person.cs", @"
public record Person(string Name, int Age);"));
        try
        {
            var elements = AnalyzeElements(dir, "Person.cs");
            var nameParam = elements.FirstOrDefault(e =>
                e.TryGetProperty("bareName", out var bn) && bn.GetString() == "Name");
            Assert.True(nameParam.ValueKind != JsonValueKind.Undefined, "positional param `Name` not captured as an element");
            Assert.Equal("parameter", nameParam.GetProperty("kind").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }
}
