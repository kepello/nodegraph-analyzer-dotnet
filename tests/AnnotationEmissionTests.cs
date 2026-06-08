using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Group A8 attribute/annotation emission (Fathom row dotnet-l0-attribute-
/// emission 5.0.79; H1 of the 2026-06-07 context-sufficiency audit).
///
/// In-process unit tests over <see cref="AnnotationHelpers.Extract"/> cover
/// one fixture per attribute CATEGORY the audit named (auth / ORM / service-
/// contract / generated-provenance / test / serialization — per
/// feedback_test_fixture_pattern_catalog) plus the full A8 arg-kind matrix
/// (string / number / boolean / identifier / named-args / expression-fallback)
/// and the J1 fallback-limitation contract. One end-to-end NDJSON test pins
/// the wire path (element <c>annotations</c> facet + the limitation flow
/// through the built analyzer).
/// </summary>
public class AnnotationEmissionTests
{
    // ---------- in-process harness ----------

    private static AnnotationHelpers.AnnotationExtractResult? ExtractFor(
        string code, Func<SyntaxNode, bool> selector)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var comp = CSharpCompilation.Create("anno-test", new[] { tree }, refs);
        var model = comp.GetSemanticModel(tree);
        var node = tree.GetRoot().DescendantNodes().First(selector);
        return AnnotationHelpers.Extract(node, model);
    }

    private static AnnotationHelpers.AnnotationExtractResult ExtractClass(string code, string className)
        => ExtractFor(code, n => n is ClassDeclarationSyntax c && c.Identifier.Text == className)!;

    private static AnnotationHelpers.AnnotationExtractResult ExtractMethod(string code, string methodName)
        => ExtractFor(code, n => n is MethodDeclarationSyntax m && m.Identifier.Text == methodName)!;

    private static AnnotationHelpers.AnnotationExtractResult ExtractProperty(string code, string propName)
        => ExtractFor(code, n => n is PropertyDeclarationSyntax p && p.Identifier.Text == propName)!;

    private static AnnotationHelpers.AnnotationExtractResult ExtractInterface(string code, string ifaceName)
        => ExtractFor(code, n => n is InterfaceDeclarationSyntax i && i.Identifier.Text == ifaceName)!;

    private static Dictionary<string, object?> Anno(AnnotationHelpers.AnnotationExtractResult r, string name)
        => r.Annotations.Single(a => (string)a["name"]! == name);

    private static bool Has(AnnotationHelpers.AnnotationExtractResult r, string name)
        => r.Annotations.Any(a => (string)a["name"]! == name);

    private static List<object> Args(Dictionary<string, object?> a) => (List<object>)a["args"]!;
    private static Dictionary<string, object?> Arg(List<object> args, int i) => (Dictionary<string, object?>)args[i];
    private static string Kind(Dictionary<string, object?> arg) => (string)arg["kind"]!;
    private static Dictionary<string, object?> Named(Dictionary<string, object?> a, string key)
        => (Dictionary<string, object?>)((Dictionary<string, object?>)a["namedArgs"]!)[key]!;

    // ---------- category fixtures (one per audit category) ----------

    [Fact]
    public void Auth_AuthorizeAttribute_OnMethod_Emitted()
    {
        var r = ExtractMethod("class C { [Authorize] void Secure() {} }", "Secure");
        Assert.True(Has(r, "Authorize"));
    }

    [Fact]
    public void Orm_TableAndColumnAndKey_Emitted()
    {
        const string code = @"
[Table(""Customers"")]
class Customer {
    [Key] public int Id { get; set; }
    [Column(""first_name"")] public string First { get; set; }
}";
        var cls = ExtractClass(code, "Customer");
        Assert.Equal("Customers", (string)Arg(Args(Anno(cls, "Table")), 0)["value"]!);

        var id = ExtractProperty(code, "Id");
        Assert.True(Has(id, "Key"));
        var first = ExtractProperty(code, "First");
        Assert.Equal("first_name", (string)Arg(Args(Anno(first, "Column")), 0)["value"]!);
    }

    [Fact]
    public void ServiceContract_OnInterfaceAndOperation_Emitted()
    {
        const string code = @"
[ServiceContract]
interface ISvc {
    [OperationContract] void Do();
}
class Web { [WebMethod] public void Ping() {} }";
        Assert.True(Has(ExtractInterface(code, "ISvc"), "ServiceContract"));
        Assert.True(Has(ExtractMethod(code, "Do"), "OperationContract"));
        Assert.True(Has(ExtractMethod(code, "Ping"), "WebMethod"));
    }

    [Fact]
    public void GeneratedProvenance_GeneratedCodeAndDebuggerNonUserCode_Emitted()
    {
        const string code = @"
class G {
    [GeneratedCode(""tool"", ""1.0"")] [DebuggerNonUserCode] void Gen() {}
}";
        var m = ExtractMethod(code, "Gen");
        var gc = Anno(m, "GeneratedCode");
        Assert.Equal("tool", (string)Arg(Args(gc), 0)["value"]!);
        Assert.Equal("1.0", (string)Arg(Args(gc), 1)["value"]!);
        Assert.True(Has(m, "DebuggerNonUserCode"));
    }

    [Fact]
    public void TestMembership_FactAndTestMethod_Emitted()
    {
        const string code = @"
class T {
    [Fact] public void A() {}
    [TestMethod] public void B() {}
}";
        Assert.True(Has(ExtractMethod(code, "A"), "Fact"));
        Assert.True(Has(ExtractMethod(code, "B"), "TestMethod"));
    }

    [Fact]
    public void Serialization_DataContractDataMemberSerializable_Emitted()
    {
        const string code = @"
[DataContract] [Serializable]
class Dto { [DataMember] public int X { get; set; } }";
        var cls = ExtractClass(code, "Dto");
        Assert.True(Has(cls, "DataContract"));
        Assert.True(Has(cls, "Serializable"));
        Assert.True(Has(ExtractProperty(code, "X"), "DataMember"));
    }

    // ---------- arg-kind matrix ----------

    [Fact]
    public void Arg_String_TypedAsString()
    {
        var r = ExtractMethod("class C { [A(\"hi\")] void M() {} }", "M");
        var arg = Arg(Args(Anno(r, "A")), 0);
        Assert.Equal("string", Kind(arg));
        Assert.Equal("hi", (string)arg["value"]!);
    }

    [Fact]
    public void Arg_Number_TypedAsNumber()
    {
        var r = ExtractMethod("class C { [A(42)] void M() {} }", "M");
        var arg = Arg(Args(Anno(r, "A")), 0);
        Assert.Equal("number", Kind(arg));
        Assert.Equal(42, Convert.ToInt64(arg["value"]));
    }

    [Fact]
    public void Arg_Boolean_TypedAsBoolean()
    {
        var r = ExtractMethod("class C { [A(true)] void M() {} }", "M");
        var arg = Arg(Args(Anno(r, "A")), 0);
        Assert.Equal("boolean", Kind(arg));
        Assert.Equal(true, arg["value"]);
    }

    [Fact]
    public void Arg_Identifier_DottedMemberAccess()
    {
        var r = ExtractMethod("class C { [A(SomeEnum.Never)] void M() {} }", "M");
        var arg = Arg(Args(Anno(r, "A")), 0);
        Assert.Equal("identifier", Kind(arg));
        Assert.Equal("SomeEnum.Never", (string)arg["value"]!);
    }

    [Fact]
    public void NamedArgs_TypedByValue()
    {
        var r = ExtractMethod("class C { [A(Name = \"x\", Count = 3)] void M() {} }", "M");
        var a = Anno(r, "A");
        Assert.Equal("string", Kind(Named(a, "Name")));
        Assert.Equal("x", (string)Named(a, "Name")["value"]!);
        Assert.Equal("number", Kind(Named(a, "Count")));
        Assert.Equal(3, Convert.ToInt64(Named(a, "Count")["value"]));
    }

    [Fact]
    public void BareAttribute_HasEmptyArgsNoNamedArgs()
    {
        var r = ExtractClass("[Serializable] class C {}", "C");
        var a = Anno(r, "Serializable");
        Assert.Empty(Args(a));
        Assert.False(a.ContainsKey("namedArgs"));
    }

    [Fact]
    public void ExpressionFallback_EmitsExpressionKindAndFlagsLimitation()
    {
        var r = ExtractMethod("class C { [A(1 + 2)] void M() {} }", "M");
        var arg = Arg(Args(Anno(r, "A")), 0);
        Assert.Equal("expression", Kind(arg));
        Assert.Equal("1 + 2", (string)arg["source"]!);
        Assert.NotEmpty(r.Fallbacks);
        Assert.Contains(r.Fallbacks, f => f.Source == "1 + 2");
    }

    [Fact]
    public void TypeOfArg_FallsBackToExpression_DeferredTypeRef()
    {
        // H1 SCOPE: typeof(T) is the deferred type-ref case → honest expression
        // fallback + limitation (NOT a silent drop). Upgraded by the type-ref
        // follow-on row.
        var r = ExtractMethod("class C { [A(typeof(string))] void M() {} }", "M");
        var arg = Arg(Args(Anno(r, "A")), 0);
        Assert.Equal("expression", Kind(arg));
        Assert.NotEmpty(r.Fallbacks);
    }

    [Fact]
    public void QualifiedName_ResolvedForBclAttribute()
    {
        // [Obsolete] resolves against the core reference → qualifiedName present.
        var r = ExtractMethod("using System; class C { [Obsolete] void M() {} }", "M");
        Assert.Equal("System.ObsoleteAttribute", (string?)Anno(r, "Obsolete").GetValueOrDefault("qualifiedName"));
    }

    [Fact]
    public void NoAttributes_ReturnsNull()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() {} }");
        var comp = CSharpCompilation.Create("a", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = comp.GetSemanticModel(tree);
        var m = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        Assert.Null(AnnotationHelpers.Extract(m, model));
    }

    // ---------- end-to-end wire path ----------

    [Fact]
    public void EndToEnd_AnnotationsFacetAndFallbackLimitation_OnWire()
    {
        var dir = MakeTempTree(("Dto.cs", @"
using System;
[Serializable]
public class Dto {
    [Obsolete(""x"")] public void Old() {}
    [SomeAttr(1 + 2)] public void Weird() {}
}"));
        try
        {
            var (annotationsByElement, limitations) = AnalyzeAnnotations(dir, "Dto.cs");
            // Class carries the Serializable annotation.
            Assert.Contains(annotationsByElement, kv =>
                kv.Value.Any(a => a == "Serializable"));
            // A method carries Obsolete.
            Assert.Contains(annotationsByElement, kv =>
                kv.Value.Any(a => a == "Obsolete"));
            // The expression-fallback arg surfaced a J1 limitation on the wire.
            Assert.Contains("fallback-annotation-arg", limitations);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-anno-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(AnnotationEmissionTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Run the built analyzer; return (elementName → annotation names)
    /// + the artifact's limitation kinds for the artifact ending with the suffix.</summary>
    private static (Dictionary<string, List<string>> Annotations, List<string> Limitations)
        AnalyzeAnnotations(string dir, string fileSuffix)
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

        var byElement = new Dictionary<string, List<string>>();
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
                    foreach (var lim in lims.EnumerateArray())
                        if (lim.TryGetProperty("kind", out var k)) limitations.Add(k.GetString() ?? "");
                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                foreach (var el in elements.EnumerateArray())
                {
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var names = new List<string>();
                    if (el.TryGetProperty("annotations", out var anns) && anns.ValueKind == JsonValueKind.Array)
                        foreach (var a in anns.EnumerateArray())
                            if (a.TryGetProperty("name", out var an)) names.Add(an.GetString() ?? "");
                    if (names.Count > 0) byElement[name] = names;
                }
            }
        }
        return (byElement, limitations);
    }
}
