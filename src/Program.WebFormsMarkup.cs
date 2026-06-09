/// <summary>
/// WebForms markup parsing + control-field companion synthesis (Fathom row
/// <c>dotnet-system-web-framework-ref-resolution</c> 5.0.87).
///
/// ASP.NET Web Site Projects have NO <c>.designer.cs</c> — control fields
/// (<c>lbl</c>, <c>cb</c>) are declared ONLY in the <c>.ascx/.aspx/.master</c>
/// markup; ASP.NET generates the partial-class fields at runtime. To the
/// analyzer the codebehind identifier is therefore undeclared and every
/// control binding (<c>lbl.Text = …</c>, <c>cb.Items.Add(…)</c>) drops.
///
/// This file does what the ASP.NET page compiler does, statically:
///   1. parse the markup's directives (<c>Control/Page/Master</c> →
///      <c>Inherits</c>+<c>CodeFile</c>; <c>Register</c> → tag-prefix
///      mappings) and its <c>runat="server"</c> controls;
///   2. map each control tag to a fully-qualified type (asp: → the
///      System.Web candidate namespaces; html tags → HtmlControls;
///      custom prefixes via Register / web.config &lt;pages&gt;&lt;controls&gt;;
///      <c>Src=</c> registers → the user control's own codebehind class);
///   3. synthesize ONE generated-companion partial per codebehind class
///      declaring <c>protected global::&lt;type&gt; &lt;id&gt;;</c> per control.
///
/// The companion is injected into the WSP Compilation ONLY — never the
/// per-file artifact map — so no phantom artifacts exist and edges to the
/// synthesized fields are tagged <c>resolutionProvenance: generated-companion</c>
/// (design: context-sufficiency-and-semantic-surfaces-2026-06-07 §5; H2).
///
/// Honest-residual policy (no-silent-degradation): a registered type whose
/// assembly can't be located still gets its field synthesized with the known
/// FQN (the field declares; member edges drop honestly — regression
/// WebFormsControlFieldTests Variant B) plus a loud problem; a control whose
/// prefix has NO registration (no type name derivable) is skipped, also with
/// a problem. Neither path reads as success.
/// </summary>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>One tag-prefix registration: either Namespace/Assembly (server
/// controls in a referenced assembly) or Src/TagName (a user control whose
/// type is the target markup's Inherits class).</summary>
record WebFormsRegister(string TagPrefix, string? Namespace, string? Assembly, string? Src, string? TagName);

/// <summary>One <c>runat="server"</c> control discovered in markup.
/// <c>TagPrefix</c> is null for html server controls (<c>&lt;div runat="server"&gt;</c>);
/// <c>TypeAttr</c> carries the html <c>type=</c> attribute (input mapping).</summary>
record WebFormsControl(string? TagPrefix, string TagName, string Id, string? TypeAttr);

/// <summary>The parse result for one markup file.</summary>
record WebFormsMarkupFile(
    string Path,
    string? Inherits,
    string? CodeFile,
    IReadOnlyList<WebFormsRegister> Registers,
    IReadOnlyList<WebFormsControl> Controls);

static class WebFormsMarkupParser
{
    // <%@ DirectiveName attr="v" ... %> — directives span lines in the real
    // corpus (EnvisionWeb Register directives wrap after Namespace=").
    static readonly Regex Directive = new(
        @"<%@\s*(?<name>\w+)(?<body>.*?)%>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex Attr = new(
        "(?<k>[A-Za-z_][\\w-]*)\\s*=\\s*\"(?<v>[^\"]*)\"",
        RegexOptions.Compiled);

    // Any element tag (prefixed or plain html). `<%` never matches ([A-Za-z]
    // required), so directives/code blocks are naturally excluded.
    static readonly Regex Tag = new(
        @"<(?<close>/?)(?<name>[A-Za-z][\w]*(?::[A-Za-z][\w]*)?)(?<attrs>[^>]*?)(?<self>/?)>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Server-side comments hide markup from the ASP.NET parser (html comments
    // do NOT — controls inside <!-- --> are still created at runtime, so we
    // deliberately keep scanning inside those).
    static readonly Regex ServerComment = new(
        @"<%--.*?--%>", RegexOptions.Compiled | RegexOptions.Singleline);

    public static WebFormsMarkupFile Parse(string content, string path)
    {
        content = ServerComment.Replace(content, " ");

        string? inherits = null, codeFile = null;
        var registers = new List<WebFormsRegister>();

        foreach (Match d in Directive.Matches(content))
        {
            var attrs = ParseAttrs(d.Groups["body"].Value);
            switch (d.Groups["name"].Value.ToLowerInvariant())
            {
                case "control":
                case "page":
                case "master":
                    inherits ??= Get(attrs, "Inherits");
                    codeFile ??= Get(attrs, "CodeFile") ?? Get(attrs, "CodeBehind");
                    break;
                case "register":
                    var prefix = Get(attrs, "TagPrefix");
                    if (prefix != null)
                        registers.Add(new WebFormsRegister(
                            prefix,
                            Get(attrs, "Namespace"),
                            Get(attrs, "Assembly"),
                            Get(attrs, "Src"),
                            Get(attrs, "TagName")));
                    break;
                // Reference / Import / OutputCache / … carry no field facts.
            }
        }

        var controls = new List<WebFormsControl>();
        // Controls nested inside a template (`<ItemTemplate>` and friends) get
        // NO page-level field — ASP.NET scopes them to a naming container.
        // Track only *Template open/close tags; tolerant of the corpus's
        // non-well-formed html everywhere else.
        var templateDepth = 0;
        foreach (Match t in Tag.Matches(content))
        {
            var name = t.Groups["name"].Value;
            var localName = name.Contains(':') ? name.Split(':')[1] : name;
            var isTemplate = localName.EndsWith("Template", StringComparison.OrdinalIgnoreCase);
            if (t.Groups["close"].Value == "/")
            {
                if (isTemplate && templateDepth > 0) templateDepth--;
                continue;
            }
            if (isTemplate && t.Groups["self"].Value != "/")
            {
                templateDepth++;
                continue;
            }
            if (templateDepth > 0) continue;

            var attrs = ParseAttrs(t.Groups["attrs"].Value);
            var runat = Get(attrs, "runat");
            if (runat == null || !runat.Equals("server", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Get(attrs, "id");
            if (string.IsNullOrEmpty(id)) continue;

            string? tagPrefix = null;
            var tagName = name;
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                tagPrefix = name.Substring(0, colon);
                tagName = name.Substring(colon + 1);
            }
            controls.Add(new WebFormsControl(tagPrefix, tagName, id, Get(attrs, "type")));
        }

        return new WebFormsMarkupFile(path, inherits, codeFile, registers, controls);
    }

    /// <summary>
    /// Parse web.config's site-wide prefix registrations
    /// (<c>&lt;pages&gt;&lt;controls&gt;&lt;add tagPrefix=… namespace=… assembly=…/&gt;</c>).
    /// On EnvisionWeb this — not a per-file Register — is what declares
    /// <c>telerik:</c> → Telerik.Web.UI.
    /// </summary>
    public static IReadOnlyList<WebFormsRegister> ParsePagesControls(string webConfigContent)
    {
        var result = new List<WebFormsRegister>();
        var pages = Regex.Match(webConfigContent, @"<pages\b.*?</pages>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!pages.Success) return result;
        foreach (Match add in Regex.Matches(pages.Value, @"<add\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attrs = ParseAttrs(add.Value);
            var prefix = Get(attrs, "tagPrefix");
            if (prefix == null) continue;
            result.Add(new WebFormsRegister(
                prefix,
                Get(attrs, "namespace"),
                Get(attrs, "assembly"),
                Get(attrs, "src"),
                Get(attrs, "tagName")));
        }
        return result;
    }

    static Dictionary<string, string> ParseAttrs(string body)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Attr.Matches(body))
            attrs.TryAdd(a.Groups["k"].Value, a.Groups["v"].Value);
        return attrs;
    }

    static string? Get(Dictionary<string, string> attrs, string key)
        => attrs.TryGetValue(key, out var v) ? v : null;
}

static class WebFormsCompanion
{
    /// <summary>Synthetic companion file-path suffix. Companion trees exist in
    /// the Compilation only (never ingested as artifacts); this suffix is the
    /// stateless predicate the edge emitters use to recognize them.</summary>
    public const string PathSuffix = ".fathom-companion.cs";

    public static bool IsCompanionPath(string path)
        => path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// First declaration reference NOT in a synthetic companion tree, or null
    /// when only companion declarations exist. The companion partial appears
    /// in a type's <c>DeclaringSyntaxReferences</c> alongside the real
    /// codebehind; a resolver that blindly takes <c>[0]</c> could emit a
    /// targetRef to the companion's natural key — a non-ingested file — i.e.
    /// a dangling edge (the exact 5.0.77 failure mode, reborn).
    /// </summary>
    public static Microsoft.CodeAnalysis.SyntaxReference? FirstNonCompanionDeclRef(
        System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.SyntaxReference> declRefs)
    {
        foreach (var r in declRefs)
            if (!IsCompanionPath(r.SyntaxTree.FilePath)) return r;
        return null;
    }

    // The `asp:` prefix legitimately spans these namespaces (ScriptManager /
    // UpdatePanel live in System.Web.UI, not WebControls) — probe in order.
    static readonly string[] AspCandidateNamespaces =
    [
        "System.Web.UI.WebControls",
        "System.Web.UI",
        "System.Web.UI.WebControls.WebParts",
    ];

    // Html server controls (`<div runat="server" id=…>`) map per the ASP.NET
    // page compiler's table; anything unlisted is HtmlGenericControl.
    static readonly Dictionary<string, string> HtmlTagTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["form"] = "HtmlForm",
        ["select"] = "HtmlSelect",
        ["textarea"] = "HtmlTextArea",
        ["img"] = "HtmlImage",
        ["a"] = "HtmlAnchor",
        ["button"] = "HtmlButton",
        ["table"] = "HtmlTable",
        ["head"] = "HtmlHead",
    };

    static readonly Dictionary<string, string> HtmlInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = "HtmlInputText",
        ["password"] = "HtmlInputPassword",
        ["checkbox"] = "HtmlInputCheckBox",
        ["radio"] = "HtmlInputRadioButton",
        ["hidden"] = "HtmlInputHidden",
        ["file"] = "HtmlInputFile",
        ["image"] = "HtmlInputImage",
        ["button"] = "HtmlInputButton",
        ["submit"] = "HtmlInputSubmit",
        ["reset"] = "HtmlInputReset",
    };

    /// <summary>
    /// Map a discovered control to a fully-qualified type name.
    /// Returns (TypeName, Problem):
    ///   (fqn, null)     — resolved against the compilation's references;
    ///   (fqn, problem)  — type NAME known from the registration but absent
    ///                     from references → synthesize anyway (field declares,
    ///                     member edges drop honestly — Variant B) + loud problem;
    ///   (null, problem) — no type name derivable (unregistered prefix /
    ///                     missing Src target) → skip the field + loud problem.
    /// <paramref name="srcInheritsLookup"/> receives the Register's Src value
    /// verbatim; BuildCompanions wraps it with path resolution.
    /// </summary>
    public static (string? TypeName, string? Problem) MapControlType(
        WebFormsControl control,
        IReadOnlyList<WebFormsRegister> registers,
        Func<string, bool> typeExists,
        Func<string, string?> srcInheritsLookup)
    {
        // Html server control → HtmlControls table.
        if (control.TagPrefix is null)
        {
            string typeName;
            if (control.TagName.Equals("input", StringComparison.OrdinalIgnoreCase))
                typeName = control.TypeAttr != null && HtmlInputTypes.TryGetValue(control.TypeAttr, out var it)
                    ? it : "HtmlInputText";
            else
                typeName = HtmlTagTypes.TryGetValue(control.TagName, out var ht)
                    ? ht : "HtmlGenericControl";
            var fqn = "System.Web.UI.HtmlControls." + typeName;
            return typeExists(fqn)
                ? (fqn, null)
                : (fqn, $"html server control type \"{fqn}\" not found in references");
        }

        // asp: → probe the System.Web candidate namespaces in order.
        if (control.TagPrefix.Equals("asp", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ns in AspCandidateNamespaces)
            {
                var candidate = ns + "." + control.TagName;
                if (typeExists(candidate)) return (candidate, null);
            }
            var fallback = AspCandidateNamespaces[0] + "." + control.TagName;
            return (fallback, $"asp:{control.TagName} resolved in none of the System.Web namespaces");
        }

        // Custom prefix → Register directives (file-level + web.config global).
        // Src+TagName registrations take precedence (the tag names a specific
        // user control); Namespace registrations cover the whole prefix.
        WebFormsRegister? nsRegister = null;
        foreach (var r in registers)
        {
            if (!r.TagPrefix.Equals(control.TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Src != null && r.TagName != null
                && r.TagName.Equals(control.TagName, StringComparison.OrdinalIgnoreCase))
            {
                var inheritsClass = srcInheritsLookup(r.Src);
                return inheritsClass != null
                    ? (inheritsClass, null)
                    : (null, $"user control \"{r.Src}\" (tag {control.TagPrefix}:{control.TagName}) "
                        + "has no resolvable Inherits class");
            }
            if (r.Namespace != null) nsRegister ??= r;
        }
        if (nsRegister != null)
        {
            var fqn = nsRegister.Namespace + "." + control.TagName;
            return typeExists(fqn)
                ? (fqn, null)
                : (fqn, $"registered type \"{fqn}\" (assembly \"{nsRegister.Assembly ?? "(none)"}\") "
                    + "not found in references");
        }
        return (null, $"tag prefix \"{control.TagPrefix}\" has no Register directive or "
            + "web.config <pages><controls> registration");
    }

    /// <summary>
    /// Build the generated-companion sources for a WSP: ONE merged partial per
    /// codebehind class (a class fed by several markup files must not get
    /// duplicate fields — CS0102 would poison resolution), skipping ids the
    /// codebehind already declares (ASP.NET's own rule). Unresolvable controls
    /// append a human-readable message to <paramref name="problems"/> — the
    /// caller wraps them as loud analyzer problems.
    /// </summary>
    public static List<(string Path, string Source)> BuildCompanions(
        IReadOnlyList<WebFormsMarkupFile> markupFiles,
        IReadOnlyList<WebFormsRegister> globalRegisters,
        Func<string, bool> typeExists,
        Func<string, string?> srcInheritsLookup,
        Func<string, IReadOnlyCollection<string>> declaredMembersOfClass,
        string wspRoot,
        List<string> problems)
    {
        var result = new List<(string, string)>();
        var byClass = markupFiles
            .Where(f => f.Inherits != null && f.CodeFile != null && f.Controls.Count > 0)
            .GroupBy(f => f.Inherits!, StringComparer.Ordinal);

        foreach (var group in byClass)
        {
            var fields = new List<(string Id, string TypeName)>();
            var seenIds = new HashSet<string>(declaredMembersOfClass(group.Key), StringComparer.Ordinal);
            string? companionBasePath = null;

            foreach (var markup in group)
            {
                companionBasePath ??= markup.Path;
                var registers = markup.Registers.Concat(globalRegisters).ToList();
                // Resolve Register Src paths relative to THIS markup file.
                string? LookupSrc(string src) => srcInheritsLookup(ResolveSrcPath(src, markup.Path, wspRoot));

                foreach (var control in markup.Controls)
                {
                    if (!seenIds.Add(control.Id)) continue; // declared in codebehind or an earlier markup file
                    var (typeName, problem) = MapControlType(control, registers, typeExists, LookupSrc);
                    if (problem != null)
                        problems.Add($"{markup.Path}: control \"{control.Id}\" — {problem}");
                    if (typeName == null) continue; // honest drop, problem above
                    fields.Add((control.Id, typeName));
                }
            }

            if (fields.Count == 0 || companionBasePath == null) continue;
            result.Add((companionBasePath + PathSuffix, GenerateCompanionSource(group.Key, fields)));
        }

        return result;
    }

    /// <summary>Resolve a Register <c>Src</c> value: <c>~/</c> is WSP-rooted;
    /// anything else is relative to the registering markup file.</summary>
    public static string ResolveSrcPath(string src, string markupPath, string wspRoot)
    {
        var normalized = src.Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.StartsWith("~/", StringComparison.Ordinal) || normalized.StartsWith("~\\", StringComparison.Ordinal))
            return Path.GetFullPath(Path.Combine(wspRoot, normalized.Substring(2)));
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(markupPath) ?? wspRoot, normalized));
    }

    /// <summary>Emit the companion partial. Fields are <c>protected</c> (what
    /// the ASP.NET page compiler generates) and <c>global::</c>-qualified so
    /// codebehind <c>using</c>s can't bend them.</summary>
    public static string GenerateCompanionSource(
        string className, IReadOnlyList<(string Id, string TypeName)> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//   Fathom generated-companion: WebForms control fields synthesized from");
        sb.AppendLine("//   markup (Web Site Projects have no .designer.cs). Compilation-only —");
        sb.AppendLine("//   never ingested as an artifact. Row 5.0.87.");
        sb.AppendLine("// </auto-generated>");

        var lastDot = className.LastIndexOf('.');
        var ns = lastDot > 0 ? className.Substring(0, lastDot) : null;
        var simpleName = lastDot > 0 ? className.Substring(lastDot + 1) : className;
        var indent = "";
        if (ns != null)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            indent = "    ";
        }
        sb.AppendLine($"{indent}public partial class {simpleName}");
        sb.AppendLine($"{indent}{{");
        foreach (var (id, typeName) in fields)
            sb.AppendLine($"{indent}    protected global::{typeName} {id};");
        sb.AppendLine($"{indent}}}");
        if (ns != null) sb.AppendLine("}");
        return sb.ToString();
    }
}
