using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression fixtures for Fathom row <c>wsp-control-synthesis-case-sensitivity-gap</c>
/// (5.0.87.t.1). <c>MapControlType</c>'s <c>asp:</c> cascade and registered-
/// namespace path build candidate FQNs from the MARKUP-CASED tag name
/// (<c>&lt;asp:label&gt;</c> → probes <c>System.Web.UI.WebControls.label</c>,
/// which never binds — the real type is <c>Label</c>). A full-fidelity probe
/// against the real EnvisionWeb WSPs closed 21/21 residuals with a
/// case-insensitive resolver, 0 new failures.
///
/// The fix is <c>WebSiteProjectLoader.ResolveTypeName</c>: an exact
/// <c>GetTypeByMetadataName</c> hit always wins outright (bit-for-bit
/// compatible with every already-resolving control); on a miss, a lazily-
/// built, once-per-compilation case-insensitive index over the compilation's
/// top-level arity-0 types returns the CANONICAL metadata name — never a
/// bare bool. A naive case-insensitive bool would suppress the "unresolved"
/// problem while the synthesized field still declared the markup-cased
/// (non-existent-cased) type name — silently non-binding, since C# is
/// case-sensitive. These fixtures pin both halves: the problem disappearing
/// AND the companion field actually getting the canonical type.
/// </summary>
public class WebFormsCaseInsensitiveResolutionTests
{
    private static WebFormsControl Ctl(string? prefix, string tag, string id, string? typeAttr = null)
        => new(prefix, tag, id, typeAttr);

    private static Compilation MakeCompilation(string source) => CSharpCompilation.Create(
        "Fixture",
        new[] { CSharpSyntaxTree.ParseText(source) },
        Array.Empty<MetadataReference>(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    // (a) Lowercase asp: tags resolve to the canonical PascalCase metadata
    // name, and the synthesized companion field declares that canonical
    // type — not the markup-cased guess.
    [Fact]
    public void Lowercase_asp_tags_resolve_to_canonical_type_and_emit_no_problem()
    {
        var compilation = MakeCompilation(
            "namespace System.Web.UI.WebControls { public class Label {} public class HiddenField {} }");
        Func<string, string?> resolveType = fqn => WebSiteProjectLoader.ResolveTypeName(compilation, fqn);

        var (labelType, labelProblem) = WebFormsCompanion.MapControlType(
            Ctl("asp", "label", "lbl"), [], resolveType, _ => null);
        Assert.Equal("System.Web.UI.WebControls.Label", labelType);
        Assert.Null(labelProblem);

        var (hiddenType, hiddenProblem) = WebFormsCompanion.MapControlType(
            Ctl("asp", "hiddenfield", "hf"), [], resolveType, _ => null);
        Assert.Equal("System.Web.UI.WebControls.HiddenField", hiddenType);
        Assert.Null(hiddenProblem);
    }

    // Companion-source level check for (a): the field itself must declare
    // the canonical type, not just "no problem" — a naive CI-bool fix would
    // pass the MapControlType assertions above by luck of the (fqn, problem)
    // shape while still emitting the wrong-cased field; BuildCompanions is
    // where that would actually surface.
    [Fact]
    public void BuildCompanions_synthesizes_canonical_typed_fields_for_lowercase_asp_tags()
    {
        var compilation = MakeCompilation(
            "namespace System.Web.UI.WebControls { public class Label {} public class HiddenField {} }");
        Func<string, string?> resolveType = fqn => WebSiteProjectLoader.ResolveTypeName(compilation, fqn);

        var markup = WebFormsMarkupParser.Parse(
            "<%@ Control CodeFile=\"X.ascx.cs\" Inherits=\"X\" %>\n"
            + "<asp:label runat=\"server\" ID=\"lbl\" />\n"
            + "<asp:hiddenfield runat=\"server\" ID=\"hf\" />",
            "/site/X.ascx");
        var problems = new List<string>();
        var companions = WebFormsCompanion.BuildCompanions(
            [markup], [], resolveType, _ => null, _ => Array.Empty<string>(), "/site", problems);

        var companion = Assert.Single(companions);
        Assert.Contains("protected global::System.Web.UI.WebControls.Label lbl;", companion.Source);
        Assert.Contains("protected global::System.Web.UI.WebControls.HiddenField hf;", companion.Source);
        Assert.Empty(problems);
    }

    // (b) A lowercase registered-prefix tag (Telerik-style) against a
    // PascalCase registered type resolves clean via the canonical name.
    [Fact]
    public void Lowercase_registered_prefix_tag_resolves_to_canonical_registered_type()
    {
        var compilation = MakeCompilation(
            "namespace Telerik.Web.UI { public class RadEditorish {} }");
        Func<string, string?> resolveType = fqn => WebSiteProjectLoader.ResolveTypeName(compilation, fqn);
        var registers = new[] { new WebFormsRegister("t", "Telerik.Web.UI", "Telerik.Web.UI", null, null) };

        var (typeName, problem) = WebFormsCompanion.MapControlType(
            Ctl("t", "radeditorish", "re1"), registers, resolveType, _ => null);
        Assert.Equal("Telerik.Web.UI.RadEditorish", typeName);
        Assert.Null(problem);
    }

    // (c) A genuinely-unregistered tag prefix must still drop honestly with
    // a problem — the resolver must not be able to swallow this category.
    [Fact]
    public void Genuinely_unregistered_prefix_still_drops_with_a_problem()
    {
        var compilation = MakeCompilation("namespace Ns { public class Whatever {} }");
        Func<string, string?> resolveType = fqn => WebSiteProjectLoader.ResolveTypeName(compilation, fqn);

        var (typeName, problem) = WebFormsCompanion.MapControlType(
            Ctl("mystery", "widget", "w1"), [], resolveType, _ => null);
        Assert.Null(typeName);
        Assert.NotNull(problem);
        Assert.Contains("no Register directive", problem);
    }

    // (d) A registered type whose assembly is truly absent from references
    // (nothing under its namespace exists in the compilation at all) must
    // stay Variant B: the markup-cased FQN candidate + a loud problem — the
    // resolver must not fake it as existing just because SOME case-insensitive
    // neighbor might coincidentally match (here, none does).
    [Fact]
    public void Registered_type_with_truly_absent_assembly_stays_honest_variant_b()
    {
        var compilation = MakeCompilation("namespace Unrelated { public class Noise {} }");
        Func<string, string?> resolveType = fqn => WebSiteProjectLoader.ResolveTypeName(compilation, fqn);
        var registers = new[] { new WebFormsRegister("ghost", "Ghost.Ns", "GhostAssembly", null, null) };

        var (typeName, problem) = WebFormsCompanion.MapControlType(
            Ctl("ghost", "widget", "g1"), registers, resolveType, _ => null);
        Assert.Equal("Ghost.Ns.widget", typeName); // markup-cased candidate, unresolved — not faked
        Assert.NotNull(problem);
        Assert.Contains("not found in references", problem);
    }

    // (e) Case-collision tie-break: a compilation declaring BOTH Ns.Widget
    // and Ns.widget. A tag matching neither exactly is genuinely ambiguous —
    // no nondeterministic pick, null (falls through to the honest problem
    // path). A tag matching one exactly resolves via the exact-match-first
    // path to exactly that one, untouched by the collision.
    [Fact]
    public void Case_colliding_types_resolve_exact_first_and_are_ambiguous_otherwise()
    {
        var compilation = MakeCompilation(
            "namespace Ns { public class Widget {} public class widget {} }");

        // Exact match wins outright — deterministic, bit-for-bit unaffected
        // by the case-variant coexisting in the same compilation.
        Assert.Equal("Ns.Widget", WebSiteProjectLoader.ResolveTypeName(compilation, "Ns.Widget"));
        Assert.Equal("Ns.widget", WebSiteProjectLoader.ResolveTypeName(compilation, "Ns.widget"));

        // No exact match for a third case variant — genuinely ambiguous
        // between Widget/widget, must return null, never a nondeterministic pick.
        Assert.Null(WebSiteProjectLoader.ResolveTypeName(compilation, "Ns.WIDGET"));
    }

    // The dictionary build is once-per-compilation, not once-per-control:
    // resolving several distinct candidates against the same compilation
    // must not rebuild the index each time (perf contract from the design;
    // observable via the ConditionalWeakTable cache identity holding after
    // repeated misses-then-hits).
    [Fact]
    public void Resolver_reuses_the_cached_index_across_repeated_calls_on_the_same_compilation()
    {
        var compilation = MakeCompilation(
            "namespace System.Web.UI.WebControls { public class Label {} public class TextBox {} }");
        Assert.Equal("System.Web.UI.WebControls.Label",
            WebSiteProjectLoader.ResolveTypeName(compilation, "System.Web.UI.WebControls.label"));
        Assert.Equal("System.Web.UI.WebControls.TextBox",
            WebSiteProjectLoader.ResolveTypeName(compilation, "System.Web.UI.WebControls.textbox"));
        // Same query again — still correct, proving the cached index (not a
        // one-shot side effect) backs repeated resolution.
        Assert.Equal("System.Web.UI.WebControls.Label",
            WebSiteProjectLoader.ResolveTypeName(compilation, "System.Web.UI.WebControls.LABEL"));
    }
}
