using System.Diagnostics;
using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// End-to-end spawn tests for WSP control-field companion synthesis (Fathom
/// row <c>dotnet-system-web-framework-ref-resolution</c> 5.0.87): a Web Site
/// Project (declared in the .sln, no csproj, NO .designer.cs) whose markup
/// declares the control fields. One fixture covers every pattern category:
///
///   • asp: prefix (markup-only field)       → accessesField tagged
///     generated-companion + property-set into System.Web (external-library);
///   • Register Src= user control            → property-set resolves IN-SOURCE
///     (targetRef to the user control's codebehind — the dominant
///     EnvisionWeb SetLabels shape);
///   • Register Namespace/Assembly, missing  → field declares with the known
///     FQN, member edges drop honestly (Variant B) + loud problem;
///   • unregistered prefix                   → no field, loud problem;
///   • NOTHING may carry a targetRef into a synthetic companion path (the
///     5.0.77 dangling-edge failure mode this design must not reintroduce).
///
/// Host-conditional: skipped (early return) when the net472 reference-
/// assembly pack isn't restored — same policy as FrameworkReferenceResolver's
/// host-conditional tests.
/// </summary>
public class WebFormsCompanionIntegrationTests
{
    private const string Tfm = ".NETFramework,Version%3Dv4.7.2";

    private static bool FrameworkPackAvailable()
        => FrameworkReferenceResolver.ResolveReferenceAssemblyDir(
            ".NETFramework,Version=v4.7.2", FrameworkReferenceResolver.GlobalPackagesDir()) != null;

    private static string MakeWspFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "fathom-wsp-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(root, "Site");
        Directory.CreateDirectory(site);

        File.WriteAllText(Path.Combine(root, "Fixture.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + "Project(\"{E24C65DC-7377-472B-9ABA-BC803B73C61A}\") = \"Site\", \"Site\\\", \"{11111111-1111-1111-1111-111111111111}\"\n"
            + "\tProjectSection(WebsiteProperties) = preProject\n"
            + $"\t\tTargetFrameworkMoniker = \"{Tfm}\"\n"
            + "\t\tDebug.AspNetCompiler.PhysicalPath = \"Site\\\"\n"
            + "\tEndProjectSection\n"
            + "EndProject\n");

        // The page under test: every pattern category in one markup file.
        File.WriteAllText(Path.Combine(site, "Page.aspx"),
            "<%@ Page Language=\"C#\" CodeFile=\"Page.aspx.cs\" Inherits=\"TestPage\" %>\n"
            + "<%@ Register TagPrefix=\"rc\" TagName=\"Sub\" Src=\"~/Sub.ascx\" %>\n"
            + "<%@ Register TagPrefix=\"ghost\" Namespace=\"Ghost.Ns\" Assembly=\"GhostAssembly\" %>\n"
            + "<asp:Label runat=\"server\" ID=\"lblTitle\" Text=\"x\" />\n"
            + "<rc:Sub runat=\"server\" ID=\"SubCtl\" />\n"
            + "<ghost:Widget runat=\"server\" ID=\"ghost1\" />\n"
            + "<unreg:Thing runat=\"server\" ID=\"unreg1\" />\n");
        File.WriteAllText(Path.Combine(site, "Page.aspx.cs"), @"
public partial class TestPage : System.Web.UI.Page {
    protected void Page_Load(object sender, System.EventArgs e) {
        lblTitle.Text = ""Hello"";   // asp: control — generated-companion field, external-library member
        SubCtl.Title = ""T"";        // Src= user control — member resolves IN-SOURCE
        ghost1.Spook = 1;            // registered-but-missing assembly — honest member drop (Variant B)
        unreg1.Text = ""x"";         // unregistered prefix — no field at all
    }
}");

        // The user control the page composes (the SetLabels shape).
        File.WriteAllText(Path.Combine(site, "Sub.ascx"),
            "<%@ Control Language=\"C#\" CodeFile=\"Sub.ascx.cs\" Inherits=\"SubControl\" %>\n"
            + "<asp:Label runat=\"server\" ID=\"lblInner\" />\n");
        File.WriteAllText(Path.Combine(site, "Sub.ascx.cs"), @"
public partial class SubControl : System.Web.UI.UserControl {
    public string Title { get { return lblInner.Text; } set { lblInner.Text = value; } }
}");

        return root;
    }

    private sealed record Edge(string Type, string? Subtype, string Target, string? TargetRef, bool External, string? Provenance);

    [Fact]
    public void Wsp_markup_fields_synthesize_and_bindings_emit_per_pattern_category()
    {
        if (!FrameworkPackAvailable()) return; // host-conditional, like FrameworkReferenceResolverTests
        var root = MakeWspFixture();
        try
        {
            var (edgesByFile, problems) = Analyze(root);
            var pageEdges = EdgesFor(edgesByFile, "Page.aspx.cs");

            // asp: control — the synthesized field resolves: accessesField is
            // emitted in the external generated-companion shape (no targetRef).
            var lblAccess = pageEdges.Single(e => e.Type == "accessesField" && e.Target.Contains("lbltitle"));
            Assert.True(lblAccess.External);
            Assert.Equal("generated-companion", lblAccess.Provenance);
            Assert.Null(lblAccess.TargetRef);
            // …and the member write lands in System.Web as external-library.
            Assert.Contains(pageEdges, e => e.Type == "calls" && e.Subtype == "property-set"
                && e.Target.Contains("text") && e.External && e.Provenance == "external-library");

            // Src= user control — the property write resolves IN-SOURCE with a
            // targetRef into the user control's codebehind artifact. (First,
            // not Single: the class element aggregates its members' edges.)
            var subWrite = pageEdges.First(e => e.Type == "calls" && e.Subtype == "property-set"
                && e.Target.Contains("subcontrol/title"));
            Assert.NotNull(subWrite.TargetRef);
            Assert.Contains("sub.ascx.cs", subWrite.TargetRef!.ToLowerInvariant());

            // Registered-but-missing assembly — the field still declares (its
            // access emits generated-companion)…
            Assert.Contains(pageEdges, e => e.Type == "accessesField"
                && e.Target.Contains("ghost1") && e.Provenance == "generated-companion");
            // …but the member write drops honestly (type is an error symbol).
            Assert.DoesNotContain(pageEdges, e => e.Target.Contains("spook"));

            // Unregistered prefix — no field synthesized at all: no
            // accessesField, and the undeclared identifier's member write drops.
            Assert.DoesNotContain(pageEdges, e => e.Type == "accessesField" && e.Target.Contains("unreg1"));

            // Loud, not silent: one WSP problem names both unresolved controls.
            var problem = problems.Single(p => p.Contains("generated-companion synthesis"));
            Assert.Contains("ghost1", problem);
            Assert.Contains("unreg1", problem);

            // The umbrella invariant: NOTHING anywhere may targetRef into a
            // synthetic companion (the 5.0.77 dangling-edge mode, reborn).
            Assert.DoesNotContain(edgesByFile.SelectMany(kv => kv.Value),
                e => e.TargetRef != null && e.TargetRef.Contains("fathom-companion"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Codebehind_declared_field_is_not_duplicated_and_still_resolves()
    {
        if (!FrameworkPackAvailable()) return;
        var root = Path.Combine(Path.GetTempPath(), "fathom-wsp-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(root, "Site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(root, "Fixture.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + "Project(\"{E24C65DC-7377-472B-9ABA-BC803B73C61A}\") = \"Site\", \"Site\\\", \"{22222222-2222-2222-2222-222222222222}\"\n"
            + "\tProjectSection(WebsiteProperties) = preProject\n"
            + $"\t\tTargetFrameworkMoniker = \"{Tfm}\"\n"
            + "\t\tDebug.AspNetCompiler.PhysicalPath = \"Site\\\"\n"
            + "\tEndProjectSection\n"
            + "EndProject\n");
        // Markup declares lblBoth; codebehind ALSO declares it (legal in real
        // ASP.NET — the page compiler skips the generated field). Synthesis
        // must skip it too or the duplicate poisons the class with CS0102
        // error symbols and currently-working resolution regresses.
        File.WriteAllText(Path.Combine(site, "P.aspx"),
            "<%@ Page Language=\"C#\" CodeFile=\"P.aspx.cs\" Inherits=\"P\" %>\n"
            + "<asp:Label runat=\"server\" ID=\"lblBoth\" />\n");
        File.WriteAllText(Path.Combine(site, "P.aspx.cs"), @"
public partial class P : System.Web.UI.Page {
    protected System.Web.UI.WebControls.Label lblBoth;
    protected void Page_Load(object sender, System.EventArgs e) {
        lblBoth.Text = ""x"";
    }
}");
        try
        {
            var (edgesByFile, _) = Analyze(root);
            var edges = EdgesFor(edgesByFile, "P.aspx.cs");
            // The field is the codebehind's own (same-file): a plain
            // intra-class accessesField, NOT generated-companion.
            var access = edges.Single(e => e.Type == "accessesField" && e.Target.Contains("lblboth"));
            Assert.False(access.External);
            // And the Label.Text write still resolves through the framework pack.
            Assert.Contains(edges, e => e.Type == "calls" && e.Subtype == "property-set"
                && e.Target.Contains("text") && e.Provenance == "external-library");
        }
        finally { Directory.Delete(root, true); }
    }

    private static List<Edge> EdgesFor(
        Dictionary<string, List<Edge>> edgesByFile, string fileSuffix)
        => edgesByFile.Where(kv => kv.Key.Replace('\\', '/').EndsWith(fileSuffix))
            .SelectMany(kv => kv.Value).ToList();

    private static string? AnalyzerDll()
    {
        var dir = Path.GetDirectoryName(typeof(WebFormsCompanionIntegrationTests).Assembly.Location)!;
        for (var probe = new DirectoryInfo(dir); probe != null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "src", "bin", "Debug", "net9.0", "NodegraphAnalyzerDotnet.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static (Dictionary<string, List<Edge>> EdgesByFile, List<string> Problems) Analyze(string dir)
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
        proc.WaitForExit(120_000);

        var edgesByFile = new Dictionary<string, List<Edge>>();
        var problems = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;
                if (t.GetString() == "problem")
                {
                    problems.Add(root.GetProperty("problem").GetRawText());
                    continue;
                }
                if (t.GetString() != "artifact") continue;
                var artifact = root.GetProperty("artifact");
                var id = artifact.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (!artifact.TryGetProperty("elements", out var elements)) continue;
                var list = edgesByFile.TryGetValue(id, out var existing)
                    ? existing : edgesByFile[id] = new List<Edge>();
                foreach (var el in elements.EnumerateArray())
                {
                    if (!el.TryGetProperty("edges", out var elEdges) || elEdges.ValueKind != JsonValueKind.Array) continue;
                    foreach (var e in elEdges.EnumerateArray())
                    {
                        var ty = e.TryGetProperty("type", out var tyl) ? tyl.GetString() ?? "" : "";
                        var st = e.TryGetProperty("subtype", out var stl) ? stl.GetString() : null;
                        var tn = e.TryGetProperty("targetName", out var tnl) ? tnl.GetString() ?? "" : "";
                        var tr = e.TryGetProperty("targetRef", out var trl) ? trl.GetString() : null;
                        var ext = false;
                        string? prov = null;
                        if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                        {
                            ext = md.TryGetProperty("external", out var ex) && ex.ValueKind == JsonValueKind.True;
                            prov = md.TryGetProperty("resolutionProvenance", out var pv) ? pv.GetString() : null;
                        }
                        list.Add(new Edge(ty, st, tn, tr, ext, prov));
                    }
                }
            }
        }
        return (edgesByFile, problems);
    }
}
