using System.Text.RegularExpressions;

/// <summary>
/// Analyzer-side port of the .NET framework vocabulary tables the L1 shared
/// engine (<c>nodegraph-analysis</c>) currently hard-codes across six
/// derivation modules (Fathom row boundary-drift-correction 3.4.1, chunk 3 —
/// the analyzer-side half; the engine deletes its own tables and consumes
/// these emitted facets in a coordinated later chunk).
///
/// ZERO-DIFF CONSTRAINT: every table and match rule below is ported
/// VERBATIM from the corresponding engine module — same sets, same string
/// casing/normalization, same precedence. Do NOT "improve" the matching here
/// (symbol-based matching is a filed residual, not this work). Source
/// modules (absolute paths under the sibling repo, read 2026-07-04):
///
///   - .../nodegraph-analysis/src/engine/derivations/stereotypes.ts
///     (BOUNDARY_BASE_TYPES / COLLECTION_BASE_TYPES / ROOT_ERROR_BASE_TYPES)
///   - .../nodegraph-analysis/src/engine/derivations/integration-surface.ts
///     (ENDPOINT_ATTRS / HOST_ATTRS / CONTRACT_ATTRS / HTTP_VERB_ATTRS)
///   - .../nodegraph-analysis/src/engine/derivations/dataaccess-surface.ts
///     (STORE_NAMESPACES / WRITE_OPS / READ_OPS)
///   - .../nodegraph-analysis/src/engine/derivations/interaction-surface.ts
///     (UI_BASES / LIFECYCLE_BY_NAME / CONTROL_KIND_RULES)
///   - .../nodegraph-analysis/src/engine/derivations/serialization-surface.ts
///     (FORMAT_BY_ATTR)
///   - .../nodegraph-analysis/src/engine/derivations/is-generated.ts
///     (SIGNAL_BY_ATTR / DESIGNER_SUFFIX)
/// </summary>
internal static class SemanticCatalog
{
    // ===================================================================
    // stereotypes.ts — BOUNDARY_BASE_TYPES / COLLECTION_BASE_TYPES /
    // ROOT_ERROR_BASE_TYPES (fully-qualified, lowercased membership sets).
    // ===================================================================

    private static readonly HashSet<string> BoundaryBaseTypes = new(StringComparer.Ordinal)
    {
        // WinForms (System.Windows.Forms.*; Component is System.ComponentModel)
        "system.windows.forms.form",
        "system.windows.forms.usercontrol",
        "system.windows.forms.control",
        "system.windows.forms.containercontrol",
        "system.windows.forms.scrollablecontrol",
        "system.componentmodel.component",
        // WebForms (System.Web.UI.*)
        "system.web.ui.page",
        "system.web.ui.masterpage",
        "system.web.ui.usercontrol",
        "system.web.ui.control",
        "system.web.ui.webcontrols.webcontrol",
        "system.web.ui.webcontrols.compositecontrol",
        // WPF (System.Windows.*)
        "system.windows.window",
        "system.windows.application",
        "system.windows.controls.contentcontrol",
        "system.windows.controls.usercontrol",
        "system.windows.controls.page",
        // Xamarin / MAUI / native mobile
        "xamarin.forms.contentpage",
        "microsoft.maui.controls.contentpage",
        "uikit.uiviewcontroller",
        "android.app.activity",
        "android.app.fragment",
        "androidx.fragment.app.fragment",
        // Reporting frameworks
        "telerik.reporting.report",
        // Service proxies
        "system.servicemodel.clientbase",
    };

    private static readonly HashSet<string> CollectionBaseTypes = new(StringComparer.Ordinal)
    {
        // System.Collections.Generic
        "system.collections.generic.list",
        "system.collections.generic.dictionary",
        "system.collections.generic.hashset",
        "system.collections.generic.sortedlist",
        "system.collections.generic.sorteddictionary",
        "system.collections.generic.sortedset",
        "system.collections.generic.queue",
        "system.collections.generic.stack",
        "system.collections.generic.linkedlist",
        // System.Collections.ObjectModel
        "system.collections.objectmodel.collection",
        "system.collections.objectmodel.observablecollection",
        "system.collections.objectmodel.readonlycollection",
        "system.collections.objectmodel.keyedcollection",
        // System.Collections (non-generic, legacy)
        "system.collections.arraylist",
        "system.collections.hashtable",
        "system.collections.sortedlist",
        "system.collections.collectionbase",
        "system.collections.dictionarybase",
    };

    private static readonly HashSet<string> RootErrorBaseTypes = new(StringComparer.Ordinal)
    {
        "error",
        "exception",
        "system.exception",
    };

    /// <summary>
    /// <c>baseTypeRoles</c> — classifies the SAME flattened <c>baseTypes</c>
    /// facet an element already emits against the three stereotypes.ts base-
    /// type sets (lowercased membership, exactly as the engine's
    /// <c>classStereotype</c> rules 0.7/0.75/0.77 match). Unlike
    /// <c>classStereotype</c> (which returns the FIRST matching category and
    /// stops — a single-value class-level decision), this facet is a plain
    /// fact enumeration: every base type that matches every category gets its
    /// own <c>{role, source}</c> entry, in boundary → collection → error
    /// order. CONTRACT: called for every element that carries <c>baseTypes</c>
    /// — always returns a list (possibly empty), never null.
    /// </summary>
    internal static List<Dictionary<string, object?>> ClassifyBaseTypeRoles(IReadOnlyList<string> baseTypes)
    {
        var roles = new List<Dictionary<string, object?>>();
        foreach (var b in baseTypes)
        {
            if (BoundaryBaseTypes.Contains(b.ToLowerInvariant()))
                roles.Add(new Dictionary<string, object?> { ["role"] = "boundary", ["source"] = b });
        }
        foreach (var b in baseTypes)
        {
            if (CollectionBaseTypes.Contains(b.ToLowerInvariant()))
                roles.Add(new Dictionary<string, object?> { ["role"] = "collection", ["source"] = b });
        }
        foreach (var b in baseTypes)
        {
            if (RootErrorBaseTypes.Contains(b.ToLowerInvariant()))
                roles.Add(new Dictionary<string, object?> { ["role"] = "error", ["source"] = b });
        }
        return roles;
    }

    // ===================================================================
    // integration-surface.ts — ENDPOINT_ATTRS / HTTP_VERB_ATTRS /
    // CONTRACT_ATTRS / HOST_ATTRS. Method-level endpoint rules checked
    // before class-level host/contract; the REST verb-attribute rule
    // is checked between the two attribute-map endpoint families,
    // matching the engine's rule order exactly.
    // ===================================================================

    private static readonly (string Attr, string Protocol, string Framework)[] EndpointAttrs =
    {
        ("WebMethod", "soap", "asmx"),
        ("ScriptMethod", "ajax", "aspnet-ajax"),
        ("OperationContract", "soap", "wcf"),
    };

    private static readonly (string Attr, string Protocol, string Framework)[] HostAttrs =
    {
        ("WebService", "soap", "asmx"),
        ("ScriptService", "ajax", "aspnet-ajax"),
        ("ApiController", "rest", "aspnet-webapi"),
    };

    private static readonly (string Attr, string Protocol, string Framework)[] ContractAttrs =
    {
        ("ServiceContract", "soap", "wcf"),
    };

    private static readonly (string Attr, string Verb)[] HttpVerbAttrs =
    {
        ("HttpGet", "GET"),
        ("HttpPost", "POST"),
        ("HttpPut", "PUT"),
        ("HttpDelete", "DELETE"),
        ("HttpPatch", "PATCH"),
        ("HttpHead", "HEAD"),
        ("HttpOptions", "OPTIONS"),
    };

    /// <summary>
    /// <c>integrationRole</c> — single-object classification over the
    /// element's own annotation names (Attribute-suffix-normalized: a name is
    /// matched with OR without the trailing "Attribute", exactly like
    /// integration-surface.ts's <c>names</c> Set), applying the engine's
    /// current precedence: endpoint attrs, then HTTP-verb attrs (REST), then
    /// contract attrs, then host attrs — first match wins. Returns null when
    /// no annotation matches (no catch-all, mirrors the engine).
    /// <paramref name="elementName"/> should be the BARE, already-lowercased,
    /// signature-free identifier (this analyzer's A6 <c>bareName</c> facet,
    /// lowercased) — the shape <c>operationName</c>'s split-on-`(` assumes.
    /// </summary>
    internal static Dictionary<string, object?>? ClassifyIntegrationRole(
        IReadOnlyList<string> rawAnnotationNames, string? elementName)
    {
        var names = NormalizeAttributeNames(rawAnnotationNames);
        if (names.Count == 0) return null;

        foreach (var (attr, protocol, framework) in EndpointAttrs)
        {
            if (!names.Contains(attr)) continue;
            var role = new Dictionary<string, object?> { ["kind"] = "endpoint", ["protocol"] = protocol, ["framework"] = framework };
            var operation = OperationName(elementName);
            if (operation != null) role["operation"] = operation;
            return role;
        }
        foreach (var (attr, verb) in HttpVerbAttrs)
        {
            if (!names.Contains(attr)) continue;
            var role = new Dictionary<string, object?> { ["kind"] = "endpoint", ["protocol"] = "rest", ["framework"] = "aspnet-webapi" };
            var operation = OperationName(elementName);
            if (operation != null) role["operation"] = operation;
            role["verb"] = verb;
            return role;
        }
        foreach (var (attr, protocol, framework) in ContractAttrs)
        {
            if (names.Contains(attr))
                return new Dictionary<string, object?> { ["kind"] = "contract", ["protocol"] = protocol, ["framework"] = framework };
        }
        foreach (var (attr, protocol, framework) in HostAttrs)
        {
            if (names.Contains(attr))
                return new Dictionary<string, object?> { ["kind"] = "host", ["protocol"] = protocol, ["framework"] = framework };
        }
        return null;
    }

    /// <summary>Attribute names with the "Attribute" suffix ALSO added
    /// (not stripped-replaced) — mirrors integration-surface.ts's <c>names</c>
    /// Set build exactly, so a source-written `[WebMethodAttribute]` matches
    /// the suffix-free table key `WebMethod` too.</summary>
    private static HashSet<string> NormalizeAttributeNames(IReadOnlyList<string> rawNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in rawNames)
        {
            if (string.IsNullOrEmpty(n)) continue;
            names.Add(n);
            if (n.EndsWith("Attribute", StringComparison.Ordinal))
                names.Add(n[..^"Attribute".Length]);
        }
        return names;
    }

    /// <summary>Bare operation name from a (possibly qualified, possibly
    /// signatured) element name: <c>Svc/Ping(int)</c> → <c>Ping</c>. Mirrors
    /// integration-surface.ts's <c>operationName</c> exactly.</summary>
    private static string? OperationName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var lastSlash = name.LastIndexOf('/');
        var last = lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
        var parenIdx = last.IndexOf('(');
        var bare = (parenIdx >= 0 ? last[..parenIdx] : last).Trim();
        return bare.Length > 0 ? bare : null;
    }

    // ===================================================================
    // dataaccess-surface.ts — STORE_NAMESPACES (most-specific-first
    // prefix match) / WRITE_OPS / READ_OPS (substring match).
    // ===================================================================

    private static readonly (string Namespace, string Store)[] StoreNamespaces =
    {
        ("system-data-entity", "ef6"),
        ("system-data-linq", "linq-to-sql"),
        ("microsoft-entityframeworkcore", "ef-core"),
        ("dapper", "dapper"),
        ("system-data", "ado-net"),
    };

    private static readonly string[] WriteOps =
    {
        "add", "insert", "update", "delete", "remove", "save", "merge",
        "writexml", "executenonquery", "acceptchanges",
    };

    private static readonly string[] ReadOps =
    {
        "select", "find", "fill", "read", "load", "query",
        "executereader", "executescalar", "getdata", "get",
    };

    /// <summary>
    /// <c>apiCategory</c> for a <c>calls</c> edge whose canonical target
    /// (the SAME kebab-canonical string the analyzer emits as the external
    /// edge's <c>targetName</c>) matches a persistence namespace prefix.
    /// Mirrors dataaccess-surface.ts's <c>storeFor</c> + WRITE_OPS/READ_OPS
    /// substring match; per-edge (not per-element aggregate), so the
    /// engine's read+write→"mixed" case has no single-edge analogue — write
    /// takes precedence over read when a single target string happens to
    /// contain both substrings, matching the engine's own
    /// <c>write ? "write" : read ? "read" : "unknown"</c> ternary precedence
    /// (the "unknown" case is the honest absence of the optional field).
    /// Returns null when the target matches no persistence namespace.
    /// </summary>
    internal static Dictionary<string, object?>? ClassifyApiCategory(string canonicalTarget)
    {
        string? store = null;
        foreach (var (ns, st) in StoreNamespaces)
        {
            if (canonicalTarget.StartsWith(ns, StringComparison.Ordinal)) { store = st; break; }
        }
        if (store == null) return null;

        var write = Array.Exists(WriteOps, w => canonicalTarget.Contains(w, StringComparison.Ordinal));
        var read = Array.Exists(ReadOps, r => canonicalTarget.Contains(r, StringComparison.Ordinal));

        var category = new Dictionary<string, object?> { ["domain"] = "persistence", ["store"] = store };
        if (write) category["operation"] = "write";
        else if (read) category["operation"] = "read";
        return category;
    }

    // ===================================================================
    // interaction-surface.ts — UI_BASES (class-level entryKind/framework
    // from baseTypes) / LIFECYCLE_BY_NAME (method-name lifecycle) /
    // CONTROL_KIND_RULES (control-type FQN → domain taxonomy).
    // ===================================================================

    private static readonly (string Needle, string EntryKind, string Framework)[] UiBases =
    {
        ("System.Web.UI.MasterPage", "page", "webforms"),
        ("System.Web.UI.Page", "page", "webforms"),
        ("System.Web.UI.UserControl", "control", "webforms"),
        ("System.Web.UI.WebControls", "control", "webforms"),
        ("System.Web.UI.HtmlControls", "control", "webforms"),
        ("System.Windows.Forms.Form", "window", "winforms"),
        ("System.Windows.Forms.UserControl", "control", "winforms"),
        ("System.Windows.Forms.Control", "control", "winforms"),
    };

    /// <summary>
    /// <c>interactionRole</c> class-level skeleton — the FIRST <c>UI_BASES</c>
    /// needle whose FQN substring appears in ANY of the element's baseTypes
    /// (case-sensitive <c>Contains</c>, exactly interaction-surface.ts's
    /// <c>bases.some(b =&gt; b.includes(needle))</c> — never the lowercased
    /// stereotypes.ts convention). Method-level `handler` surface (binds /
    /// invokes / lifecycle-gated-by-parent-class) stays engine-side; the
    /// analyzer emits the ungated <c>uiLifecycle</c>/<c>uiTriggers</c>
    /// name-match facets instead (see <see cref="ClassifyUiLifecycle"/>).
    /// </summary>
    internal static Dictionary<string, object?>? ClassifyInteractionRole(IReadOnlyList<string> baseTypes)
    {
        foreach (var (needle, entryKind, framework) in UiBases)
        {
            var matched = false;
            foreach (var b in baseTypes)
            {
                if (b.Contains(needle, StringComparison.Ordinal)) { matched = true; break; }
            }
            if (matched)
                return new Dictionary<string, object?> { ["entryKind"] = entryKind, ["framework"] = framework };
        }
        return null;
    }

    private static readonly Dictionary<string, string> LifecycleByName = new(StringComparer.Ordinal)
    {
        ["page_init"] = "init",
        ["page_load"] = "load",
        ["page_prerender"] = "prerender",
        ["page_unload"] = "unload",
        ["oninit"] = "init",
        ["onload"] = "load",
        ["onprerender"] = "prerender",
        ["onunload"] = "unload",
    };

    /// <summary>
    /// <c>uiLifecycle</c> / <c>uiTriggers</c> — pure name-match (the WebForms
    /// auto-wired lifecycle names, then the <c>btnSave_Click</c>-shaped
    /// auto-wired event-handler convention), exactly interaction-surface.ts's
    /// method-level rules MINUS the "containing class is a UI surface"
    /// structural gate — the engine keeps that gate on its own read of this
    /// facet, so an analyzer-side ungated emission is a safe (honest) superset,
    /// never a silent narrowing. <paramref name="canonicalName"/> should be
    /// the BARE, already-lowercased, signature-free identifier (this
    /// analyzer's A6 <c>bareName</c> facet, lowercased) — the shape the
    /// engine's own <c>bareName()</c> split-on-`/`-then-`(` assumes (this
    /// analyzer's dash-joined canonical <c>name</c> facet carries no literal
    /// parens to split on, so the SAME algorithm needs the pre-stripped bare
    /// identifier as input to reach the same result). Returns (null, null) on
    /// no match.
    /// </summary>
    internal static (string? Lifecycle, string[]? Triggers) ClassifyUiLifecycle(string? canonicalName)
    {
        var bare = BareLastSegment(canonicalName);
        if (bare == null) return (null, null);
        if (LifecycleByName.TryGetValue(bare, out var lifecycle)) return (lifecycle, null);

        var underscore = bare.LastIndexOf('_');
        if (underscore > 0 && underscore < bare.Length - 1)
        {
            // `btnsave_click` — an auto-wired event handler.
            return ("event-handler", new[] { bare[(underscore + 1)..] });
        }
        return (null, null);
    }

    /// <summary>Last name segment, bare of signature: mirrors interaction-
    /// surface.ts's <c>bareName</c> exactly (split on `/`, then `(`).</summary>
    private static string? BareLastSegment(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var lastSlash = name.LastIndexOf('/');
        var last = lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
        var parenIdx = last.IndexOf('(');
        var bare = (parenIdx >= 0 ? last[..parenIdx] : last).Trim();
        return bare.Length > 0 ? bare : null;
    }

    private static readonly (Regex Pattern, string ControlKind)[] ControlKindRules =
    {
        (new Regex(@"(?:^|\.)\w*Button$", RegexOptions.IgnoreCase), "button"),
        (new Regex(@"(?:^|\.)(?:Label|Literal|HtmlGenericControl)$", RegexOptions.IgnoreCase), "label"),
        (new Regex(@"(?:^|\.)(?:\w*Grid(?:View)?|Repeater|DataList|ListView)$", RegexOptions.IgnoreCase), "grid"),
        (new Regex(@"(?:^|\.)(?:HyperLink|HtmlAnchor)$", RegexOptions.IgnoreCase), "link"),
        (new Regex(@"(?:^|\.)(?:Image|HtmlImage)$", RegexOptions.IgnoreCase), "image"),
        (new Regex(@"(?:^|\.)(?:\w*TextBox|\w*ComboBox|DropDownList|ListBox|CheckBox\w*|RadioButton\w*|\w*DatePicker|\w*Input\w*|HtmlSelect|FileUpload|Calendar)$", RegexOptions.IgnoreCase), "form-field"),
        (new Regex(@"(?:^|\.)(?:Panel|PlaceHolder|UpdatePanel|MultiView|View|Content|HtmlForm)$", RegexOptions.IgnoreCase), "container"),
        (new Regex(@"Validator$", RegexOptions.IgnoreCase), "validation"),
    };

    /// <summary>
    /// Map a control-type FQN to the domain <c>ControlKind</c> taxonomy —
    /// mirrors interaction-surface.ts's <c>mapControlKind</c> exactly. Null
    /// input (no known control type) → null (no fact to record); a KNOWN but
    /// unlisted type → <c>"other"</c> (never a guess).
    /// </summary>
    internal static string? MapControlKind(string? controlTypeFqn)
    {
        if (string.IsNullOrEmpty(controlTypeFqn)) return null;
        foreach (var (pattern, kind) in ControlKindRules)
        {
            if (pattern.IsMatch(controlTypeFqn)) return kind;
        }
        return "other";
    }

    // ===================================================================
    // serialization-surface.ts — FORMAT_BY_ATTR (Attribute-suffix
    // normalized), gated to CONTRACT_KINDS / MEMBER_KINDS element kinds.
    // ===================================================================

    private static readonly Dictionary<string, string> FormatByAttr = new(StringComparer.Ordinal)
    {
        // Newtonsoft.Json
        ["JsonProperty"] = "json",
        ["JsonIgnore"] = "json",
        ["JsonObject"] = "json",
        ["JsonArray"] = "json",
        ["JsonConverter"] = "json",
        ["JsonRequired"] = "json",
        ["JsonExtensionData"] = "json",
        // System.Text.Json
        ["JsonPropertyName"] = "json",
        ["JsonInclude"] = "json",
        ["JsonSerializable"] = "json",
        // System.Xml.Serialization
        ["XmlRoot"] = "xml",
        ["XmlType"] = "xml",
        ["XmlElement"] = "xml",
        ["XmlAttribute"] = "xml",
        ["XmlText"] = "xml",
        ["XmlArray"] = "xml",
        ["XmlArrayItem"] = "xml",
        ["XmlIgnore"] = "xml",
        ["XmlEnum"] = "xml",
        // System.Runtime.Serialization (WCF data contracts)
        ["DataContract"] = "data-contract",
        ["CollectionDataContract"] = "data-contract",
        ["DataMember"] = "data-contract",
        ["EnumMember"] = "data-contract",
        // Runtime / binary serialization markers
        ["Serializable"] = "runtime-serializable",
        ["NonSerialized"] = "runtime-serializable",
    };

    private static readonly HashSet<string> SerializationContractKinds = new(StringComparer.Ordinal)
    {
        "class", "struct", "interface", "enum", "record",
    };

    private static readonly HashSet<string> SerializationMemberKinds = new(StringComparer.Ordinal)
    {
        "property", "field", "enumMember",
    };

    /// <summary>
    /// <c>serializationFormats</c> — deduped + sorted formats implied by the
    /// element's own annotations, gated to type-like / member-like element
    /// kinds (exactly serialization-surface.ts's CONTRACT_KINDS/MEMBER_KINDS
    /// gate). Returns null when the kind doesn't participate OR no annotation
    /// maps to a format (no catch-all).
    /// </summary>
    internal static string[]? ClassifySerializationFormats(string? elementKind, IReadOnlyList<string> rawAnnotationNames)
    {
        if (elementKind == null) return null;
        if (!SerializationContractKinds.Contains(elementKind) && !SerializationMemberKinds.Contains(elementKind))
            return null;

        var formats = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawAnnotationNames)
        {
            var n = StripAttributeSuffix(raw);
            if (FormatByAttr.TryGetValue(n, out var format)) formats.Add(format);
        }
        return formats.Count > 0 ? formats.ToArray() : null;
    }

    // ===================================================================
    // is-generated.ts — SIGNAL_BY_ATTR (Attribute-suffix normalized) +
    // DESIGNER_SUFFIX filename convention.
    // ===================================================================

    private static readonly Dictionary<string, string> GeneratedSignalByAttr = new(StringComparer.Ordinal)
    {
        ["GeneratedCode"] = "generated-code-attribute",
        ["CompilerGenerated"] = "compiler-generated-attribute",
        ["DebuggerNonUserCode"] = "debugger-non-user-code-attribute",
    };

    private const string DesignerSuffix = ".designer.cs";

    /// <summary>
    /// <c>generatedSignals</c> — today's four attribute-derived strings (per
    /// SIGNAL_BY_ATTR) plus the <c>.designer.cs</c> filename signal, exactly
    /// as is-generated.ts computes them; deduped + sorted. No kind gate
    /// (generated-ness is a fact about ANY element). Returns null when no
    /// evidence is present (mirrors the engine's honest-null / no-catch-all
    /// discipline).
    /// </summary>
    internal static string[]? ClassifyGeneratedSignals(IReadOnlyList<string> rawAnnotationNames, string? filePath)
    {
        var signals = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawAnnotationNames)
        {
            var n = StripAttributeSuffix(raw);
            if (GeneratedSignalByAttr.TryGetValue(n, out var signal)) signals.Add(signal);
        }
        if (!string.IsNullOrEmpty(filePath) && filePath.ToLowerInvariant().EndsWith(DesignerSuffix, StringComparison.Ordinal))
        {
            signals.Add("designer-filename");
        }
        return signals.Count > 0 ? signals.ToArray() : null;
    }

    /// <summary>C# attributes may be written with or without the "Attribute"
    /// suffix; strip it so table lookups match the base name — mirrors both
    /// serialization-surface.ts and is-generated.ts's per-annotation
    /// normalization (strip-only, not add-both; equivalent for lookup since
    /// no table key itself ends in "Attribute").</summary>
    private static string StripAttributeSuffix(string raw) =>
        raw.EndsWith("Attribute", StringComparison.Ordinal) ? raw[..^"Attribute".Length] : raw;
}
