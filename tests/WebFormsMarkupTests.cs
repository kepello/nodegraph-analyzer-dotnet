/// <summary>
/// Tests for WebForms markup parsing + control-field companion synthesis
/// (Fathom row <c>dotnet-system-web-framework-ref-resolution</c> 5.0.87 —
/// the WSP `.ascx` control-field synthesis). Web Site Projects have no
/// `.designer.cs`, so control fields exist ONLY in markup; the analyzer
/// parses the markup, maps tag prefixes to types, and synthesizes a
/// generated-companion partial injected into the WSP compilation.
///
/// Fixture shapes mirror the real EnvisionWeb corpus: multi-line
/// `&lt;%@ Register %&gt;` directives, `Src=` user-control registers,
/// `telerik:` prefix via web.config `&lt;pages&gt;&lt;controls&gt;`, html
/// server controls, and template-nested controls (which get NO field).
/// </summary>

namespace NodegraphAnalyzerDotnet.Tests;

public class WebFormsMarkupParserTests
{
    [Fact]
    public void Control_directive_yields_inherits_and_codefile()
    {
        var f = WebFormsMarkupParser.Parse(
            "<%@ Control Language=\"C#\" AutoEventWireup=\"true\"  CodeFile=\"ProductList.ascx.cs\" Inherits=\"Portal_Reports_ProductList\" %>",
            "/site/ProductList.ascx");
        Assert.Equal("Portal_Reports_ProductList", f.Inherits);
        Assert.Equal("ProductList.ascx.cs", f.CodeFile);
    }

    [Fact]
    public void Page_and_master_directives_also_yield_inherits()
    {
        var page = WebFormsMarkupParser.Parse(
            "<%@ Page Language=\"C#\" CodeFile=\"Default.aspx.cs\" Inherits=\"_Default\" MasterPageFile=\"~/site.master\" %>",
            "/site/Default.aspx");
        Assert.Equal("_Default", page.Inherits);
        var master = WebFormsMarkupParser.Parse(
            "<%@ Master Language=\"C#\" CodeFile=\"reports.master.cs\" Inherits=\"reports\" %>",
            "/site/reports.master");
        Assert.Equal("reports", master.Inherits);
    }

    [Fact]
    public void Register_namespace_assembly_parses_including_multiline()
    {
        // Real corpus shape: the directive spans two lines.
        var f = WebFormsMarkupParser.Parse(
            "<%@ Register Assembly=\"Telerik.Web.UI\" Namespace=\"Telerik.Web.UI.Editor\"\nTagPrefix=\"tools\" %>",
            "/site/x.ascx");
        var r = Assert.Single(f.Registers);
        Assert.Equal("tools", r.TagPrefix);
        Assert.Equal("Telerik.Web.UI.Editor", r.Namespace);
        Assert.Equal("Telerik.Web.UI", r.Assembly);
        Assert.Null(r.Src);
    }

    [Fact]
    public void Register_src_tagname_parses_with_lowercase_attributes()
    {
        // Real corpus shape: lowercase attribute names + multi-line.
        var f = WebFormsMarkupParser.Parse(
            "<%@ Register TagPrefix=\"rc\" TagName=\"ReportTitle\" \nSrc=\"~/Portal/Reports/ReportControls/ReportTitle.ascx\" %>\n"
            + "<%@ Register src=\"~/Portal/SortBy.ascx\" tagname=\"SortBy\" tagprefix=\"rc\" %>",
            "/site/x.ascx");
        Assert.Equal(2, f.Registers.Count);
        Assert.Equal("ReportTitle", f.Registers[0].TagName);
        Assert.Equal("~/Portal/Reports/ReportControls/ReportTitle.ascx", f.Registers[0].Src);
        Assert.Equal("SortBy", f.Registers[1].TagName);
        Assert.Equal("rc", f.Registers[1].TagPrefix);
    }

    [Fact]
    public void Reference_directive_is_ignored()
    {
        var f = WebFormsMarkupParser.Parse(
            "<%@ Reference  Control=\"../reports.master\" %>", "/site/x.ascx");
        Assert.Empty(f.Registers);
        Assert.Null(f.Inherits);
    }

    [Fact]
    public void Prefixed_runat_server_controls_with_id_are_discovered()
    {
        var f = WebFormsMarkupParser.Parse(
            "<asp:Label runat=\"server\" ID=\"lblTitle\" Text=\"x\"></asp:Label>\n"
            + "<rc:ReportTitle runat=\"server\" ID=\"ReportTitle\" />\n"
            + "<telerik:RadComboBox runat=\"Server\" id=\"cbInvDept\" Height=\"190px\">\n",
            "/site/x.ascx");
        Assert.Equal(3, f.Controls.Count);
        Assert.Equal(("asp", "Label", "lblTitle"), (f.Controls[0].TagPrefix, f.Controls[0].TagName, f.Controls[0].Id));
        Assert.Equal(("rc", "ReportTitle", "ReportTitle"), (f.Controls[1].TagPrefix, f.Controls[1].TagName, f.Controls[1].Id));
        // runat="Server" (capital S) still counts; lowercase `id` still counts.
        Assert.Equal(("telerik", "RadComboBox", "cbInvDept"), (f.Controls[2].TagPrefix, f.Controls[2].TagName, f.Controls[2].Id));
    }

    [Fact]
    public void Controls_without_runat_or_without_id_are_skipped()
    {
        var f = WebFormsMarkupParser.Parse(
            "<asp:Label ID=\"noRunat\" Text=\"x\" />\n"
            + "<asp:Literal runat=\"server\" Text=\"no id\" />\n"
            + "<div id=\"plainHtml\">x</div>",
            "/site/x.ascx");
        Assert.Empty(f.Controls);
    }

    [Fact]
    public void Html_server_controls_are_discovered_with_null_prefix_and_type_attr()
    {
        var f = WebFormsMarkupParser.Parse(
            "<img runat=\"server\" id=\"imgLogo\" src=\"x.png\" />\n"
            + "<input type=\"hidden\" runat=\"server\" id=\"hidToken\" />\n"
            + "<div runat=\"server\" id=\"divError\">x</div>",
            "/site/x.ascx");
        Assert.Equal(3, f.Controls.Count);
        Assert.Null(f.Controls[0].TagPrefix);
        Assert.Equal("img", f.Controls[0].TagName);
        Assert.Equal("hidden", f.Controls[1].TypeAttr);
        Assert.Equal("divError", f.Controls[2].Id);
    }

    [Fact]
    public void Template_nested_controls_get_no_field()
    {
        // ASP.NET generates no page-level field for controls inside a
        // template (they live in naming containers) — synthesis must skip
        // them or it invents members that don't exist at runtime.
        var f = WebFormsMarkupParser.Parse(
            "<asp:Repeater runat=\"server\" ID=\"rpt\">\n"
            + "  <ItemTemplate>\n"
            + "    <asp:Label runat=\"server\" ID=\"lblInner\" />\n"
            + "  </ItemTemplate>\n"
            + "</asp:Repeater>\n"
            + "<asp:Label runat=\"server\" ID=\"lblOuter\" />",
            "/site/x.ascx");
        Assert.Equal(new[] { "rpt", "lblOuter" }, f.Controls.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Web_config_pages_controls_registrations_parse()
    {
        var registers = WebFormsMarkupParser.ParsePagesControls(
            "<configuration><system.web>\n"
            + "<pages controlRenderingCompatibilityVersion=\"3.5\">\n"
            + "  <controls>\n"
            + "    <add tagPrefix=\"telerik\" namespace=\"Telerik.Web.UI\" assembly=\"Telerik.Web.UI\" />\n"
            + "  </controls>\n"
            + "</pages></system.web></configuration>");
        var r = Assert.Single(registers);
        Assert.Equal(("telerik", "Telerik.Web.UI", "Telerik.Web.UI"), (r.TagPrefix, r.Namespace, r.Assembly));
    }
}

public class WebFormsCompanionTests
{
    private static WebFormsControl Ctl(string? prefix, string tag, string id, string? typeAttr = null)
        => new(prefix, tag, id, typeAttr);

    [Fact]
    public void Asp_prefix_probes_webcontrols_then_system_web_ui()
    {
        var exists = new HashSet<string> { "System.Web.UI.WebControls.Label", "System.Web.UI.ScriptManager" };
        Func<string, string?> resolveType = fqn => exists.Contains(fqn) ? fqn : null;
        var (label, p1) = WebFormsCompanion.MapControlType(
            Ctl("asp", "Label", "lbl"), [], resolveType, _ => null);
        Assert.Equal("System.Web.UI.WebControls.Label", label);
        Assert.Null(p1);
        var (sm, p2) = WebFormsCompanion.MapControlType(
            Ctl("asp", "ScriptManager", "sm1"), [], resolveType, _ => null);
        Assert.Equal("System.Web.UI.ScriptManager", sm);
        Assert.Null(p2);
    }

    [Fact]
    public void Registered_namespace_prefix_maps_even_when_type_unresolved()
    {
        // Assembly registered but not locatable: synthesize the field anyway
        // with the known FQN (the field declares; member edges drop honestly —
        // regression Variant B) and report a problem. NOT a silent fallback.
        var registers = new[] { new WebFormsRegister("telerik", "Telerik.Web.UI", "Telerik.Web.UI", null, null) };
        var (resolved, pOk) = WebFormsCompanion.MapControlType(
            Ctl("telerik", "RadComboBox", "cb"), registers,
            fqn => fqn == "Telerik.Web.UI.RadComboBox" ? fqn : null, _ => null);
        Assert.Equal("Telerik.Web.UI.RadComboBox", resolved);
        Assert.Null(pOk);

        var (unresolved, pMissing) = WebFormsCompanion.MapControlType(
            Ctl("telerik", "RadComboBox", "cb"), registers, _ => null, _ => null);
        Assert.Equal("Telerik.Web.UI.RadComboBox", unresolved);
        Assert.NotNull(pMissing);
    }

    [Fact]
    public void Unregistered_prefix_yields_no_type_and_a_problem()
    {
        var (t, problem) = WebFormsCompanion.MapControlType(
            Ctl("mystery", "Widget", "w1"), [], _ => null, _ => null);
        Assert.Null(t);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Src_register_maps_to_the_user_controls_inherits_class()
    {
        var registers = new[] { new WebFormsRegister("rc", null, null, "~/Controls/ReportTitle.ascx", "ReportTitle") };
        var (t, p) = WebFormsCompanion.MapControlType(
            Ctl("rc", "ReportTitle", "ReportTitle"), registers, _ => null,
            src => src == "~/Controls/ReportTitle.ascx" ? "Portal_Reports_ReportControls_ReportTile" : null);
        Assert.Equal("Portal_Reports_ReportControls_ReportTile", t);
        Assert.Null(p);
    }

    [Fact]
    public void Src_register_with_missing_target_yields_no_type_and_a_problem()
    {
        var registers = new[] { new WebFormsRegister("rc", null, null, "~/Gone.ascx", "Gone") };
        var (t, p) = WebFormsCompanion.MapControlType(
            Ctl("rc", "Gone", "gone1"), registers, _ => null, _ => null);
        Assert.Null(t);
        Assert.NotNull(p);
    }

    [Fact]
    public void Html_controls_map_to_htmlcontrols_types()
    {
        Func<string, string?> resolveType = fqn => fqn.StartsWith("System.Web.UI.HtmlControls.") ? fqn : null;
        Assert.Equal("System.Web.UI.HtmlControls.HtmlGenericControl",
            WebFormsCompanion.MapControlType(Ctl(null, "div", "divError"), [], resolveType, _ => null).TypeName);
        Assert.Equal("System.Web.UI.HtmlControls.HtmlInputCheckBox",
            WebFormsCompanion.MapControlType(Ctl(null, "input", "chk1", "checkbox"), [], resolveType, _ => null).TypeName);
        Assert.Equal("System.Web.UI.HtmlControls.HtmlInputText",
            WebFormsCompanion.MapControlType(Ctl(null, "input", "txt1"), [], resolveType, _ => null).TypeName);
        Assert.Equal("System.Web.UI.HtmlControls.HtmlSelect",
            WebFormsCompanion.MapControlType(Ctl(null, "select", "sel1"), [], resolveType, _ => null).TypeName);
        Assert.Equal("System.Web.UI.HtmlControls.HtmlImage",
            WebFormsCompanion.MapControlType(Ctl(null, "img", "img1"), [], resolveType, _ => null).TypeName);
        Assert.Equal("System.Web.UI.HtmlControls.HtmlAnchor",
            WebFormsCompanion.MapControlType(Ctl(null, "a", "lnk1"), [], resolveType, _ => null).TypeName);
        Assert.Equal("System.Web.UI.HtmlControls.HtmlForm",
            WebFormsCompanion.MapControlType(Ctl(null, "form", "form1"), [], resolveType, _ => null).TypeName);
    }

    [Fact]
    public void Companion_source_declares_protected_global_fields_in_partial()
    {
        var src = WebFormsCompanion.GenerateCompanionSource("Portal_Reports_ProductList",
            [("ReportTitle", "Portal_Reports_ReportControls_ReportTile"),
             ("lblTitle", "System.Web.UI.WebControls.Label")]);
        Assert.Contains("<auto-generated>", src);
        Assert.Contains("partial class Portal_Reports_ProductList", src);
        Assert.Contains("protected global::Portal_Reports_ReportControls_ReportTile ReportTitle;", src);
        Assert.Contains("protected global::System.Web.UI.WebControls.Label lblTitle;", src);
        Assert.DoesNotContain("namespace", src);
    }

    [Fact]
    public void Dotted_inherits_wraps_the_partial_in_its_namespace()
    {
        var src = WebFormsCompanion.GenerateCompanionSource("My.Site.Pages.Default",
            [("lbl", "System.Web.UI.WebControls.Label")]);
        Assert.Contains("namespace My.Site.Pages", src);
        Assert.Contains("partial class Default", src);
    }

    [Fact]
    public void BuildCompanions_merges_markup_files_of_the_same_class_and_dedupes_ids()
    {
        var a = WebFormsMarkupParser.Parse(
            "<%@ Control CodeFile=\"X.ascx.cs\" Inherits=\"X\" %>\n<asp:Label runat=\"server\" ID=\"lbl\" />",
            "/site/A.ascx");
        var b = WebFormsMarkupParser.Parse(
            "<%@ Control CodeFile=\"X.ascx.cs\" Inherits=\"X\" %>\n<asp:Label runat=\"server\" ID=\"lbl\" />\n"
            + "<asp:Button runat=\"server\" ID=\"btn\" />",
            "/site/B.ascx");
        var problems = new List<string>();
        var companions = WebFormsCompanion.BuildCompanions(
            [a, b], [], fqn => fqn.StartsWith("System.Web.UI.WebControls.") ? fqn : null, _ => null,
            _ => Array.Empty<string>(), "/site", problems);
        var companion = Assert.Single(companions);
        Assert.EndsWith(WebFormsCompanion.PathSuffix, companion.Path);
        // One field per id across both markup files — no CS0102 duplicates.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(companion.Source, @"\blbl;"));
        Assert.Contains("btn;", companion.Source);
    }

    [Fact]
    public void BuildCompanions_skips_ids_already_declared_in_codebehind()
    {
        var a = WebFormsMarkupParser.Parse(
            "<%@ Control CodeFile=\"X.ascx.cs\" Inherits=\"X\" %>\n"
            + "<asp:Label runat=\"server\" ID=\"lblDeclared\" />\n<asp:Label runat=\"server\" ID=\"lblNew\" />",
            "/site/A.ascx");
        var companions = WebFormsCompanion.BuildCompanions(
            [a], [], fqn => fqn, _ => null,
            cls => cls == "X" ? new[] { "lblDeclared" } : Array.Empty<string>(),
            "/site", new List<string>());
        var companion = Assert.Single(companions);
        Assert.DoesNotContain("lblDeclared", companion.Source);
        Assert.Contains("lblNew", companion.Source);
    }

    [Fact]
    public void BuildCompanions_resolves_src_relative_and_tilde_paths()
    {
        var seen = new List<string>();
        var a = WebFormsMarkupParser.Parse(
            "<%@ Control CodeFile=\"X.ascx.cs\" Inherits=\"X\" %>\n"
            + "<%@ Register TagPrefix=\"rc\" TagName=\"T1\" Src=\"~/Controls/T1.ascx\" %>\n"
            + "<%@ Register TagPrefix=\"rc\" TagName=\"T2\" Src=\"../Shared/T2.ascx\" %>\n"
            + "<rc:T1 runat=\"server\" ID=\"t1\" />\n<rc:T2 runat=\"server\" ID=\"t2\" />",
            Path.Combine("/site", "Sub", "A.ascx"));
        WebFormsCompanion.BuildCompanions(
            [a], [], _ => null,
            p => { seen.Add(p); return "SomeClass"; },
            _ => Array.Empty<string>(), "/site", new List<string>());
        Assert.Equal(Path.GetFullPath(Path.Combine("/site", "Controls", "T1.ascx")), seen[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine("/site", "Shared", "T2.ascx")), seen[1]);
    }

    [Fact]
    public void Markup_without_codefile_or_without_controls_yields_no_companion()
    {
        var noCodeFile = WebFormsMarkupParser.Parse(
            "<%@ Control Inherits=\"X\" %>\n<asp:Label runat=\"server\" ID=\"lbl\" />", "/site/A.ascx");
        var noControls = WebFormsMarkupParser.Parse(
            "<%@ Control CodeFile=\"Y.ascx.cs\" Inherits=\"Y\" %>\n<div>static</div>", "/site/B.ascx");
        var companions = WebFormsCompanion.BuildCompanions(
            [noCodeFile, noControls], [], fqn => fqn, _ => null,
            _ => Array.Empty<string>(), "/site", new List<string>());
        Assert.Empty(companions);
    }

    [Fact]
    public void Companion_path_predicate_matches_only_the_suffix_convention()
    {
        Assert.True(WebFormsCompanion.IsCompanionPath("/site/A.ascx" + WebFormsCompanion.PathSuffix));
        Assert.False(WebFormsCompanion.IsCompanionPath("/site/A.ascx.cs"));
        Assert.False(WebFormsCompanion.IsCompanionPath("/site/A.ascx"));
    }
}
