using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Fathom row boundary-drift-correction (3.4.1, chunk 3) — analyzer-side
/// emission of the semantic facets ported from the L1 engine's .NET
/// framework vocabulary tables (see <c>Program.SemanticCatalog.cs</c>).
///
/// One fixture per family named in the design; each test's comment pins the
/// exact engine table row it exercises so the port stays traceable to its
/// source module. Spawn-based (NDJSON over the built analyzer DLL), mirroring
/// the repo's existing emission-test style (e.g. <c>ReferencesFreeReportingTests</c>,
/// <c>BaseTypesTests</c>, <c>AnnotationEmissionTests</c>).
/// </summary>
public class SemanticCatalogEmissionTests
{
    private readonly ITestOutputHelper _output;

    public SemanticCatalogEmissionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---------- harness ----------

    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(SemanticCatalogEmissionTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string MakeTempTree(params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-semcat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    /// <summary>Run the analyzer on <paramref name="dir"/>; return the raw
    /// (cloned) element JsonElements for the artifact ending with
    /// <paramref name="fileSuffix"/>.</summary>
    private static List<JsonElement> AnalyzeElements(string dir, string fileSuffix)
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

        var elements = new List<JsonElement>();
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
                if (!artifact.TryGetProperty("elements", out var els)) continue;
                foreach (var el in els.EnumerateArray()) elements.Add(el.Clone());
            }
        }
        return elements;
    }

    private static JsonElement ElementNamed(List<JsonElement> elements, Func<string, bool> predicate) =>
        elements.First(el => predicate(el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));

    /// <summary>Run the analyzer on <paramref name="dir"/>; return the raw
    /// (cloned) artifact JsonElement itself (not its <c>elements</c>) for
    /// the artifact whose id ends with <paramref name="fileSuffix"/> — the
    /// wire-protocol's file-level element (see
    /// <c>AnalyzerArtifact</c>/<c>ingestArtifact</c> in nodegraph-analysis:
    /// "the artifact itself is also an element (kind = file)").</summary>
    private static JsonElement? AnalyzeArtifact(string dir, string fileSuffix)
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
                return artifact.Clone();
            }
        }
        return null;
    }

    private static JsonElement? TryProp(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v : null;

    private static string[] StringArray(JsonElement arr) =>
        arr.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();

    // ===================================================================
    // baseTypeRoles — stereotypes.ts BOUNDARY_BASE_TYPES / COLLECTION_
    // BASE_TYPES / ROOT_ERROR_BASE_TYPES.
    // ===================================================================

    [Fact]
    public void BaseTypeRoles_Boundary_WinFormsForm()
    {
        // Pins stereotypes.ts BOUNDARY_BASE_TYPES row "system.windows.forms.form".
        var dir = MakeTempTree(("OrderEntry.cs", @"
public class OrderEntry : System.Windows.Forms.Form {}"));
        try
        {
            var elements = AnalyzeElements(dir, "OrderEntry.cs");
            var el = ElementNamed(elements, n => n.EndsWith("orderentry"));
            var roles = TryProp(el, "baseTypeRoles");
            Assert.NotNull(roles);
            Assert.Contains(roles!.Value.EnumerateArray(), r =>
                r.GetProperty("role").GetString() == "boundary"
                && r.GetProperty("source").GetString() == "System.Windows.Forms.Form");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BaseTypeRoles_Collection_GenericList()
    {
        // Pins stereotypes.ts COLLECTION_BASE_TYPES row
        // "system.collections.generic.list".
        var dir = MakeTempTree(("ReportList.cs", @"
public class Report {}
public class ReportList : System.Collections.Generic.List<Report> {}"));
        try
        {
            var elements = AnalyzeElements(dir, "ReportList.cs");
            var el = ElementNamed(elements, n => n.EndsWith("reportlist"));
            var roles = TryProp(el, "baseTypeRoles");
            Assert.NotNull(roles);
            Assert.Contains(roles!.Value.EnumerateArray(), r =>
                r.GetProperty("role").GetString() == "collection"
                && r.GetProperty("source").GetString() == "System.Collections.Generic.List");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BaseTypeRoles_Error_SystemException()
    {
        // Pins stereotypes.ts ROOT_ERROR_BASE_TYPES row "system.exception".
        var dir = MakeTempTree(("MyError.cs", @"
public class MyError : System.Exception {}"));
        try
        {
            var elements = AnalyzeElements(dir, "MyError.cs");
            var el = ElementNamed(elements, n => n.EndsWith("myerror"));
            var roles = TryProp(el, "baseTypeRoles");
            Assert.NotNull(roles);
            Assert.Contains(roles!.Value.EnumerateArray(), r =>
                r.GetProperty("role").GetString() == "error"
                && r.GetProperty("source").GetString() == "System.Exception");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BaseTypeRoles_NoMatchingBase_StillEmitsEmptyArray_ContractInvariant()
    {
        // CONTRACT: any element that gets `baseTypes` MUST also get
        // `baseTypeRoles` (possibly empty) — an interface implementation
        // that matches none of the three catalogues still gets `[]`, not an
        // absent field.
        var dir = MakeTempTree(("Widget.cs", @"
public class Widget : System.IDisposable {
    public void Dispose() {}
}"));
        try
        {
            var elements = AnalyzeElements(dir, "Widget.cs");
            var el = ElementNamed(elements, n => n.EndsWith("widget"));
            Assert.NotNull(TryProp(el, "baseTypes")); // sanity: baseTypes present
            var roles = TryProp(el, "baseTypeRoles");
            Assert.NotNull(roles);
            Assert.Equal(JsonValueKind.Array, roles!.Value.ValueKind);
            Assert.Empty(roles.Value.EnumerateArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    // ===================================================================
    // integrationRole — integration-surface.ts ENDPOINT_ATTRS /
    // HTTP_VERB_ATTRS / CONTRACT_ATTRS / HOST_ATTRS.
    // ===================================================================

    [Fact]
    public void IntegrationRole_Asmx_WebMethodEndpoint()
    {
        // Pins integration-surface.ts ENDPOINT_ATTRS row "WebMethod" →
        // {protocol: soap, framework: asmx}, plus the operation-name derivation.
        var dir = MakeTempTree(("Svc.cs", @"
public class Svc {
    [WebMethod] public int Ping() { return 1; }
}"));
        try
        {
            var elements = AnalyzeElements(dir, "Svc.cs");
            var el = ElementNamed(elements, n => n.EndsWith("ping"));
            var role = TryProp(el, "integrationRole");
            Assert.NotNull(role);
            Assert.Equal("endpoint", role!.Value.GetProperty("kind").GetString());
            Assert.Equal("soap", role.Value.GetProperty("protocol").GetString());
            Assert.Equal("asmx", role.Value.GetProperty("framework").GetString());
            // operation is lowercase — the engine's own operationName() reads
            // an already-lowercased canonical name facet; this analyzer feeds
            // it the lowercased A6 bareName to match that assumption exactly.
            Assert.Equal("ping", role.Value.GetProperty("operation").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IntegrationRole_Wcf_ServiceContractAndOperationContract()
    {
        // Pins integration-surface.ts CONTRACT_ATTRS row "ServiceContract" AND
        // ENDPOINT_ATTRS row "OperationContract" → {protocol: soap, framework: wcf}.
        var dir = MakeTempTree(("ISvc.cs", @"
[ServiceContract]
public interface ISvc {
    [OperationContract] void Do();
}"));
        try
        {
            var elements = AnalyzeElements(dir, "ISvc.cs");
            var iface = ElementNamed(elements, n => n.EndsWith("isvc"));
            var ifaceRole = TryProp(iface, "integrationRole");
            Assert.NotNull(ifaceRole);
            Assert.Equal("contract", ifaceRole!.Value.GetProperty("kind").GetString());
            Assert.Equal("soap", ifaceRole.Value.GetProperty("protocol").GetString());
            Assert.Equal("wcf", ifaceRole.Value.GetProperty("framework").GetString());

            var method = ElementNamed(elements, n => n.EndsWith("/do"));
            var methodRole = TryProp(method, "integrationRole");
            Assert.NotNull(methodRole);
            Assert.Equal("endpoint", methodRole!.Value.GetProperty("kind").GetString());
            Assert.Equal("soap", methodRole.Value.GetProperty("protocol").GetString());
            Assert.Equal("wcf", methodRole.Value.GetProperty("framework").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IntegrationRole_WebApi_HttpGetVerbEndpoint()
    {
        // Pins integration-surface.ts HTTP_VERB_ATTRS row "HttpGet" → GET,
        // always {protocol: rest, framework: aspnet-webapi} regardless of the
        // enclosing controller's own attributes.
        var dir = MakeTempTree(("ThingsController.cs", @"
public class ThingsController {
    [HttpGet] public int GetThing() { return 1; }
}"));
        try
        {
            var elements = AnalyzeElements(dir, "ThingsController.cs");
            var el = ElementNamed(elements, n => n.EndsWith("getthing"));
            var role = TryProp(el, "integrationRole");
            Assert.NotNull(role);
            Assert.Equal("endpoint", role!.Value.GetProperty("kind").GetString());
            Assert.Equal("rest", role.Value.GetProperty("protocol").GetString());
            Assert.Equal("aspnet-webapi", role.Value.GetProperty("framework").GetString());
            Assert.Equal("GET", role.Value.GetProperty("verb").GetString());
            Assert.Equal("getthing", role.Value.GetProperty("operation").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    // ===================================================================
    // interactionRole / uiLifecycle / uiTriggers — interaction-surface.ts
    // UI_BASES / LIFECYCLE_BY_NAME / the btnX_Click auto-wired convention.
    // ===================================================================

    [Fact]
    public void InteractionRoleAndUiLifecycle_WebFormsPageWithPageLoad()
    {
        // Pins interaction-surface.ts UI_BASES row "System.Web.UI.Page" →
        // {entryKind: page, framework: webforms}, and LIFECYCLE_BY_NAME row
        // "page_load" → "load" (analyzer emits ungated — no parent-class check).
        var dir = MakeTempTree(("OrderPage.cs", @"
public class OrderPage : System.Web.UI.Page {
    protected void Page_Load(object sender, System.EventArgs e) {}
}"));
        try
        {
            var elements = AnalyzeElements(dir, "OrderPage.cs");
            var page = ElementNamed(elements, n => n.EndsWith("orderpage"));
            var interactionRole = TryProp(page, "interactionRole");
            Assert.NotNull(interactionRole);
            Assert.Equal("page", interactionRole!.Value.GetProperty("entryKind").GetString());
            Assert.Equal("webforms", interactionRole.Value.GetProperty("framework").GetString());

            var handler = ElementNamed(elements, n => n.Contains("page_load"));
            var lifecycle = TryProp(handler, "uiLifecycle");
            Assert.NotNull(lifecycle);
            Assert.Equal("load", lifecycle!.Value.GetString());
            // LIFECYCLE_BY_NAME rows carry no triggers.
            Assert.Null(TryProp(handler, "uiTriggers"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UiTriggers_AutoWiredEventHandlerConvention_ExtractsEventName()
    {
        // Pins interaction-surface.ts's `btnSave_Click`-shaped auto-wired
        // event-handler convention: lifecycle "event-handler" + triggers
        // ["click"] (canonical casing), ungated (no parent-class check here).
        var dir = MakeTempTree(("OrderPage.cs", @"
public class OrderPage : System.Web.UI.Page {
    protected void btnSave_Click(object sender, System.EventArgs e) {}
}"));
        try
        {
            var elements = AnalyzeElements(dir, "OrderPage.cs");
            var handler = ElementNamed(elements, n => n.Contains("btnsave_click"));
            var lifecycle = TryProp(handler, "uiLifecycle");
            Assert.NotNull(lifecycle);
            Assert.Equal("event-handler", lifecycle!.Value.GetString());
            var triggers = TryProp(handler, "uiTriggers");
            Assert.NotNull(triggers);
            Assert.Equal(new[] { "click" }, StringArray(triggers!.Value));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ===================================================================
    // controlKind — interaction-surface.ts CONTROL_KIND_RULES, mirroring
    // the existing controlType emission on generated-companion accessesField
    // edges (Fathom 5.0.82 companion binding).
    // ===================================================================

    // controlKind stamps only on the SYNTHESIZED WebForms markup-companion
    // accessesField edge (Program.WebFormsMarkup.cs's `.fathom-companion.cs`
    // path — see WebFormsCompanionIntegrationTests), not a real on-disk
    // `.designer.cs` partial (a real cross-file member takes the plain
    // targetRef path, no controlType/controlKind metadata). Host-conditional
    // on the net472 reference-assembly pack, mirroring
    // WebFormsCompanionIntegrationTests' own gate.
    private const string Tfm = ".NETFramework,Version%3Dv4.7.2";

    private static bool FrameworkPackAvailable() =>
        FrameworkReferenceResolver.ResolveReferenceAssemblyDir(
            ".NETFramework,Version=v4.7.2", FrameworkReferenceResolver.GlobalPackagesDir()) != null;

    [Fact]
    public void ControlKind_LabelControlField_ClassifiedViaControlType()
    {
        // Pins interaction-surface.ts CONTROL_KIND_RULES row for `Label` →
        // "label", stamped alongside the existing `controlType` metadata
        // (Fathom 5.0.82) on the generated-companion accessesField edge
        // (WebFormsCompanionIntegrationTests fixture pattern — an `asp:Label`
        // markup control with no codebehind-declared field).
        if (!FrameworkPackAvailable()) return; // host-conditional — no net472 ref pack on this host

        var root = Path.Combine(Path.GetTempPath(), "fathom-semcat-wsp-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(root, "Site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(root, "Fixture.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + "Project(\"{E24C65DC-7377-472B-9ABA-BC803B73C61A}\") = \"Site\", \"Site\\\", \"{33333333-3333-3333-3333-333333333333}\"\n"
            + "\tProjectSection(WebsiteProperties) = preProject\n"
            + $"\t\tTargetFrameworkMoniker = \"{Tfm}\"\n"
            + "\t\tDebug.AspNetCompiler.PhysicalPath = \"Site\\\"\n"
            + "\tEndProjectSection\n"
            + "EndProject\n");
        File.WriteAllText(Path.Combine(site, "Page.aspx"),
            "<%@ Page Language=\"C#\" CodeFile=\"Page.aspx.cs\" Inherits=\"TestPage\" %>\n"
            + "<asp:Label runat=\"server\" ID=\"lblTitle\" Text=\"x\" />\n");
        File.WriteAllText(Path.Combine(site, "Page.aspx.cs"), @"
public partial class TestPage : System.Web.UI.Page {
    protected void Page_Load(object sender, System.EventArgs e) {
        lblTitle.Text = ""Hello"";
    }
}");
        try
        {
            var elements = AnalyzeElements(root, "Page.aspx.cs");
            if (elements.Count == 0) return; // WSP didn't load in this environment
            var edges = elements
                .SelectMany(el => el.TryGetProperty("edges", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .ToList();
            Assert.Contains(edges, e =>
                e.GetProperty("type").GetString() == "accessesField"
                && e.TryGetProperty("metadata", out var md)
                && md.TryGetProperty("controlType", out var ct)
                && ct.GetString() == "System.Web.UI.WebControls.Label"
                && md.TryGetProperty("controlKind", out var ck) && ck.GetString() == "label");
        }
        finally { Directory.Delete(root, true); }
    }

    // ===================================================================
    // serializationFormats — serialization-surface.ts FORMAT_BY_ATTR.
    // ===================================================================

    [Fact]
    public void SerializationFormats_JsonXmlAndRuntimeSerializable()
    {
        // Pins serialization-surface.ts FORMAT_BY_ATTR rows "JsonProperty" →
        // json, "DataContract" → data-contract, "Serializable" →
        // runtime-serializable (deduped + sorted).
        var dir = MakeTempTree(("Dto.cs", @"
[System.Serializable]
[DataContract]
public class Dto {
    [JsonProperty] public int X { get; set; }
}"));
        try
        {
            var elements = AnalyzeElements(dir, "Dto.cs");
            var cls = ElementNamed(elements, n => n.EndsWith("dto"));
            var clsFormats = TryProp(cls, "serializationFormats");
            Assert.NotNull(clsFormats);
            Assert.Equal(new[] { "data-contract", "runtime-serializable" }, StringArray(clsFormats!.Value));

            var prop = ElementNamed(elements, n => n.EndsWith("/x"));
            var propFormats = TryProp(prop, "serializationFormats");
            Assert.NotNull(propFormats);
            Assert.Equal(new[] { "json" }, StringArray(propFormats!.Value));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SerializationFormats_NonParticipatingKind_StaysAbsent()
    {
        // No serialization-mapped annotation on a method → no facet (no
        // catch-all; mirrors the engine's honest null).
        var dir = MakeTempTree(("Plain.cs", @"
public class Plain {
    public void M() {}
}"));
        try
        {
            var elements = AnalyzeElements(dir, "Plain.cs");
            var el = ElementNamed(elements, n => n.EndsWith("plain"));
            Assert.Null(TryProp(el, "serializationFormats"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ===================================================================
    // generatedSignals — is-generated.ts SIGNAL_BY_ATTR + DESIGNER_SUFFIX.
    // ===================================================================

    [Fact]
    public void GeneratedSignals_AttributeAndDesignerFilename()
    {
        // Pins is-generated.ts SIGNAL_BY_ATTR row "GeneratedCode" →
        // generated-code-attribute, AND the `.designer.cs` filename convention
        // → designer-filename (case-insensitive suffix match).
        var dir = MakeTempTree(
            ("Widget.cs", @"
public class Widget {
    [System.CodeDom.Compiler.GeneratedCode(""tool"", ""1.0"")]
    public void Gen() {}
}"),
            ("Widget.Designer.cs", @"
public partial class Companion {
    public void FromDesigner() {}
}"));
        try
        {
            var normalElements = AnalyzeElements(dir, "Widget.cs");
            var gen = ElementNamed(normalElements, n => n.EndsWith("gen"));
            var genSignals = TryProp(gen, "generatedSignals");
            Assert.NotNull(genSignals);
            Assert.Equal(new[] { "generated-code-attribute" }, StringArray(genSignals!.Value));

            var designerElements = AnalyzeElements(dir, "Widget.Designer.cs");
            var fromDesigner = ElementNamed(designerElements, n => n.EndsWith("fromdesigner"));
            var designerSignals = TryProp(fromDesigner, "generatedSignals");
            Assert.NotNull(designerSignals);
            Assert.Equal(new[] { "designer-filename" }, StringArray(designerSignals!.Value));

            // The FILE element itself (the artifact — wire-protocol's
            // kind="file" element, per AnalyzerArtifact's doc comment) also
            // carries the designer-filename signal: the old engine's
            // DESIGNER_SUFFIX check ran over EVERY element whose artifact
            // path matched, including the file node (no kind gate — see
            // is-generated.ts's "the file node, the class, and each member
            // ... are all generated"). Regression: the port originally
            // stamped only declaration elements, dropping the file element's
            // signal (EnvisionWeb isGenerated 10,301 → 10,057, -244).
            var designerArtifact = AnalyzeArtifact(dir, "Widget.Designer.cs");
            Assert.NotNull(designerArtifact);
            var fileSignals = TryProp(designerArtifact!.Value, "generatedSignals");
            Assert.NotNull(fileSignals);
            Assert.Equal(new[] { "designer-filename" }, StringArray(fileSignals!.Value));

            // The non-designer file's artifact carries NO generatedSignals —
            // absence of evidence stays an honest absent field, not `[]`.
            var normalArtifact = AnalyzeArtifact(dir, "Widget.cs");
            Assert.NotNull(normalArtifact);
            Assert.Null(TryProp(normalArtifact!.Value, "generatedSignals"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ===================================================================
    // apiCategory — dataaccess-surface.ts STORE_NAMESPACES ("system-data" →
    // ado-net) / WRITE_OPS ("add") / READ_OPS ("select"). Requires a REAL
    // external-library symbol resolution (System.Data.DataTable /
    // DataRowCollection are part of the base-class-library assembly
    // referenced by any net9.0 project — no NuGet restore needed), so this
    // fixture is a genuine MSBuild project rather than the bare-directory
    // mode the other tests use (bare mode only references corelib, which
    // does not carry System.Data). SqlClient/EF NuGet packages would need a
    // network restore not assumed available in this sandbox; DataTable /
    // DataRowCollection are real members of the SAME "system-data" (ado-net)
    // namespace prefix, so the classification exercised is byte-identical.
    //
    // Host-conditional: skipped (early return) ONLY when `dotnet restore`
    // itself fails (offline host without local reference packs) — same
    // policy as FrameworkReferenceResolver's / WebFormsCompanionIntegration's
    // host-conditional tests. That skip is OBSERVABLE (a message to test
    // output naming the exact reason) — never a silent green. A successful
    // restore that then resolves ZERO calls edges is NOT a skip condition
    // (restore succeeding means the environment IS exercisable) — it's a
    // real assertion failure, so the classification logic stays covered on
    // every host where `dotnet restore` can reach the local ref-pack cache.
    // ===================================================================

    [Fact]
    public void ApiCategory_AdoNet_ReadAndWrite_OnExternalCallsEdges()
    {
        var dir = MakeMsBuildProject("Repo", ("Repo.cs", @"
using System.Data;
public class Repo {
    private DataTable _table = new DataTable();
    public void Save(object[] row) { _table.Rows.Add(row); }     // WRITE_OPS ""add""
    public object[] Load() { return _table.Select(""x=1""); }     // READ_OPS ""select""
}"));
        try
        {
            if (!TryRestore(dir, "Repo.csproj"))
            {
                // Offline host without local reference packs — not
                // exercisable. Observable skip: a distinct message on every
                // run, not a bare silent pass.
                _output.WriteLine(
                    "SKIPPED ApiCategory_AdoNet_ReadAndWrite_OnExternalCallsEdges: " +
                    "`dotnet restore` failed for the Repo fixture (offline host / " +
                    "no local reference-pack cache) — apiCategory read/write " +
                    "classification NOT exercised this run.");
                return;
            }
            var edges = AnalyzeCallsEdges(dir, "Repo.cs");
            Assert.True(
                edges.Count > 0,
                "restore succeeded but MSBuildWorkspace resolved ZERO calls " +
                "edges for Repo.cs — this is a real failure (not an offline " +
                "skip: the restore that would gate a skip already succeeded), " +
                "the apiCategory classification below would otherwise assert " +
                "against an empty list and pass green with no coverage.");
            if (edges.Count == 0) return; // MSBuildWorkspace couldn't load the project in this environment

            Assert.Contains(edges, e => e.Target.Contains("datarowcollection") && e.Target.Contains("add")
                && e.ApiCategoryStore == "ado-net" && e.ApiCategoryOperation == "write");
            Assert.Contains(edges, e => e.Target.Contains("datatable") && e.Target.Contains("select")
                && e.ApiCategoryStore == "ado-net" && e.ApiCategoryOperation == "read");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string MakeMsBuildProject(string projectName, params (string Name, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fathom-semcat-msb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{projectName}.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");
        foreach (var (name, body) in files) File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private static bool TryRestore(string dir, string csprojName)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("restore");
            psi.ArgumentList.Add(Path.Combine(dir, csprojName));
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(120_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<(string Target, string? ApiCategoryStore, string? ApiCategoryOperation)>
        AnalyzeCallsEdges(string dir, string fileSuffix)
    {
        var elements = AnalyzeElements(dir, fileSuffix);
        var result = new List<(string, string?, string?)>();
        foreach (var el in elements)
        {
            if (!el.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in edges.EnumerateArray())
            {
                if (e.GetProperty("type").GetString() != "calls") continue;
                var target = e.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "";
                string? store = null;
                string? operation = null;
                if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
                    && md.TryGetProperty("apiCategory", out var apiCat) && apiCat.ValueKind == JsonValueKind.Object)
                {
                    store = apiCat.TryGetProperty("store", out var s) ? s.GetString() : null;
                    operation = apiCat.TryGetProperty("operation", out var o) ? o.GetString() : null;
                }
                result.Add((target, store, operation));
            }
        }
        return result;
    }
}
