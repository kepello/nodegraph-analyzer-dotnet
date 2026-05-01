/// <bds id="code-dotnet-analyzer" />
/// <bds type="code" />
/// <bds component="orchestrator" />
/// <bds title=".NET BDS Analyzer" />
/// <bds status="draft" />
/// <bds traces-to="scenario-orchestrator" />

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <bds derives-from="design-orchestrator#net-analyzer" />
var cliArgs = ParseArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());

if (string.IsNullOrEmpty(cliArgs.Path))
{
    Console.Error.WriteLine("Error: --path <repo-root> is required");
    Environment.Exit(1);
}

if (cliArgs.Direction == "input")
{
    RunInput(cliArgs);
}
else
{
    RunOutput(cliArgs);
}

/// <bds derives-from="scenario-orchestrator#analyzer-contract/subprocess-analyzer-rules" />
static void RunOutput(AnalyzerArgs args)
{
    Console.Error.WriteLine($".NET analyzer (output): mode={args.Mode}, scanning {args.Path}");

    var csFiles = DiscoverCsFiles(args.Path, args.Include, args.Exclude);
    Console.Error.WriteLine($".NET analyzer: found {csFiles.Count} .cs files");

    var startTime = DateTime.UtcNow;
    var elementsEmitted = 0;

    // File-level parallelism: Roslyn syntax trees are immutable and
    // DecomposeWithRoslyn has no shared state, so each file can be
    // analyzed on its own thread. Console.WriteLine is atomic per call
    // in .NET, so Emit() from multiple threads produces interleaved
    // but individually-complete NDJSON lines — which is exactly what
    // the orchestrator's streaming parser already tolerates (order
    // doesn't matter; resolveStagedEdges runs post-hoc). Dominant
    // cost is Roslyn parsing + traversal per file, and .NET
    // cold-start dwarfs file-level work for small inputs, so the
    // parallelism pays off once the JIT is warm.
    Parallel.ForEach(csFiles, filePath =>
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var identity = ParseBdsIdentity(content);

            if (identity == null)
                return; // Not an artifact — silent

            if (!identity.ContainsKey("type"))
            {
                Emit(new { type = "error", message = "BDS comments present but missing 'type'", filePath });
                return;
            }

            var record = new Dictionary<string, object?>
            {
                ["id"] = identity.GetValueOrDefault("id", ""),
                ["type"] = identity["type"],
                ["component"] = identity.GetValueOrDefault("component", ""),
                ["title"] = identity.GetValueOrDefault("title", ""),
                ["status"] = identity.GetValueOrDefault("status", ""),
                ["filePath"] = filePath
            };

            if (identity.ContainsKey("traces-to"))
                record["tracesTo"] = identity["traces-to"];

            object[] elements;
            object[] artifactEdges;
            object[] problems;

            if (args.Mode == "identity")
            {
                elements = [];
                artifactEdges = [];
                problems = [];
            }
            else
            {
                // Structure / full mode: Roslyn AST decomposition.
                // Full mode adds deeper-traversal edges (instantiates,
                // overrides, delegates, genericConstraint, decorates,
                // partial) to the structure-mode baseline.
                var (elems, artEdges, probs) = DecomposeWithRoslyn(content, filePath, args.Mode);
                elements = elems;
                artifactEdges = artEdges;
                problems = probs;
            }

            if (artifactEdges.Length > 0)
            {
                Emit(new
                {
                    type = "element",
                    element = new { record, elements, artifactEdges, problems }
                });
            }
            else
            {
                Emit(new
                {
                    type = "element",
                    element = new { record, elements, problems }
                });
            }
            Interlocked.Increment(ref elementsEmitted);
        }
        catch (Exception ex)
        {
            Emit(new { type = "error", message = $"Failed to process: {ex.Message}", filePath });
        }
    });

    var durationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
    Emit(new { type = "complete", elementsEmitted, durationMs });
}

/// <bds derives-from="scenario-orchestrator#analyzer-contract/subprocess-analyzer-rules" />
static void RunInput(AnalyzerArgs args)
{
    Console.Error.WriteLine($".NET analyzer (input): scanning {args.Path}");

    var csFiles = DiscoverCsFiles(args.Path, args.Include, args.Exclude);

    // Build artifact ID → file path index by scanning identity comments
    var idToPath = new Dictionary<string, string>();
    foreach (var filePath in csFiles)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var identity = ParseBdsIdentity(content);
            if (identity != null && identity.TryGetValue("id", out var id) && !string.IsNullOrEmpty(id))
            {
                idToPath[id] = filePath;
            }
        }
        catch { /* skip */ }
    }

    Console.Error.WriteLine($".NET analyzer (input): indexed {idToPath.Count} artifacts");

    // Read NDJSON commands from stdin
    var startTime = DateTime.UtcNow;
    var updatesByFile = new Dictionary<string, List<UpdateRequest>>();

    string? line;
    while ((line = Console.In.ReadLine()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        JsonElement cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch
        {
            Emit(new { type = "error", artifactId = "", message = $"Invalid JSON: {line[..Math.Min(100, line.Length)]}" });
            continue;
        }

        var type = cmd.GetProperty("type").GetString();
        if (type == "complete") break;
        if (type != "update")
        {
            Emit(new { type = "error", artifactId = "", message = $"Unknown command type: {type}" });
            continue;
        }

        var artifactId = cmd.GetProperty("artifactId").GetString() ?? "";
        var elementName = cmd.TryGetProperty("elementName", out var elNameEl) && elNameEl.ValueKind == JsonValueKind.String
            ? elNameEl.GetString()
            : null;
        var fields = cmd.GetProperty("fields");

        if (!idToPath.TryGetValue(artifactId, out var filePath))
        {
            Emit(new { type = "error", artifactId, elementName, message = $"Unknown artifact ID: {artifactId}" });
            continue;
        }

        if (!updatesByFile.ContainsKey(filePath))
            updatesByFile[filePath] = new List<UpdateRequest>();
        updatesByFile[filePath].Add(new UpdateRequest { ArtifactId = artifactId, ElementName = elementName, Fields = fields });
    }

    // Apply updates per file
    var updatesApplied = 0;
    foreach (var kv in updatesByFile)
    {
        var success = ApplyUpdatesToFile(kv.Key, kv.Value);
        if (success.success)
        {
            foreach (var u in kv.Value)
            {
                Emit(new { type = "updated", artifactId = u.ArtifactId, elementName = u.ElementName });
                updatesApplied++;
            }
        }
        else
        {
            foreach (var u in kv.Value)
            {
                Emit(new { type = "error", artifactId = u.ArtifactId, elementName = u.ElementName, message = success.error });
            }
        }
    }

    var durationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
    Emit(new { type = "complete", updatesApplied, durationMs });
}

// --- Roslyn Decomposition ---

/// <bds derives-from="scenario-orchestrator#net-analyzer/net-element-decomposition" />
static (object[] elements, object[] artifactEdges, object[] problems) DecomposeWithRoslyn(string content, string filePath, string mode)
{
    var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
    var root = tree.GetRoot();

    // WI-5: Create a minimal compilation so we can obtain a
    // SemanticModel. Required for FQN (ISymbol.ToDisplayString)
    // and parameter type resolution. Single-file compilation
    // with core references is sufficient — unresolved external
    // types still produce usable display strings.
    var compilation = CSharpCompilation.Create("BdsAnalysis",
        syntaxTrees: [tree],
        references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    var semanticModel = compilation.GetSemanticModel(tree);
    var elements = new List<object>();
    var problems = new List<object>();
    var allNames = new HashSet<string>();
    // artifact-level edges: file imports and artifact→top-level contains.
    // Core persists these without inferring containment from element names.
    var artifactEdges = new List<object>();

    // Collect imported names from using directives. allNames holds the raw
    // namespace (matched against AST text); targetName in the emitted edge
    // is canonicalized so graph matching compares on canonical identity.
    var compilationUnit = (CompilationUnitSyntax)root;
    foreach (var usingDirective in compilationUnit.Usings)
    {
        var nsName = usingDirective.Name?.ToString();
        if (nsName != null)
        {
            allNames.Add(nsName);
            var canonical = Canonicalize(nsName);
            if (!string.IsNullOrEmpty(canonical))
                artifactEdges.Add(new { type = "imports", subtype = "using", targetName = canonical });
        }
    }

    // First pass: collect all raw declaration names and their qualified
    // forms. Qualified form is `typename/membername` for members of a
    // containing type; top-level declarations use their bare name.
    // allNames is matched against raw AST text so it holds both forms —
    // qualified names for edge resolution, bare names for same-file lookups.
    foreach (var node in root.DescendantNodes())
    {
        var rawName = GetDeclarationName(node);
        if (rawName == null) continue;
        allNames.Add(rawName);
        var qualified = GetQualifiedRawName(node, rawName);
        if (qualified != rawName) allNames.Add(qualified);
    }

    // Second pass: extract elements. Element names are canonicalized path-
    // by-path so the `/` separator between class and member is preserved.
    // Overloaded methods (same qualified name, different parameter lists)
    // are disambiguated with $1, $2, ... suffix before canonicalization.
    var seenCanonical = new Dictionary<string, string>();  // canonical -> raw
    var elementQualifiedToCanonical = new Dictionary<string, string>();
    var qualifiedNameCounts = new Dictionary<string, int>();

    foreach (var node in root.DescendantNodes())
    {
        var rawName = GetDeclarationName(node);
        if (rawName == null) continue;

        var elementType = GetElementType(node);
        if (elementType == null) continue;

        var qualifiedRaw = GetQualifiedRawName(node, rawName);
        // Disambiguate overloads: first occurrence keeps bare name,
        // subsequent get $1, $2, ... appended.
        if (qualifiedNameCounts.TryGetValue(qualifiedRaw, out var count))
        {
            qualifiedNameCounts[qualifiedRaw] = count + 1;
            qualifiedRaw = $"{qualifiedRaw}${count}";
        }
        else
        {
            qualifiedNameCounts[qualifiedRaw] = 1;
        }
        var name = CanonicalizePath(qualifiedRaw);
        if (string.IsNullOrEmpty(name))
        {
            problems.Add(new { severity = "error", message = $"Declaration \"{qualifiedRaw}\" canonicalizes to empty string" });
            continue;
        }
        if (seenCanonical.TryGetValue(name, out var existingRaw))
        {
            problems.Add(new { severity = "error", message = $"Duplicate canonical element name \"{name}\" — declarations \"{existingRaw}\" and \"{qualifiedRaw}\" collide. Rename one." });
            continue;
        }
        seenCanonical[name] = qualifiedRaw;
        elementQualifiedToCanonical[qualifiedRaw] = name;

        var sourceText = node.ToFullString().Trim();
        // Hash excludes audit annotations so write-back doesn't invalidate verdicts
        var contentHash = ComputeHash(StripAuditAnnotations(sourceText));
        var derivesFrom = ExtractDerivesFrom(node);
        var (state, stateReason) = ExtractState(node);
        var audits = ExtractAudits(node);
        var relationships = ExtractRelationships(node, allNames, mode);

        // If this node is a type declaration, emit qualified contains
        // edges pointing at each of its direct member declarations.
        // The analyzer declares containment explicitly; core does not
        // derive it from canonical name structure.
        if (node is TypeDeclarationSyntax typeDecl)
        {
            var containsEdges = new List<object>(relationships);
            foreach (var member in typeDecl.Members)
            {
                var memberRawName = GetDeclarationName(member);
                if (memberRawName == null) continue;
                var memberQualified = $"{qualifiedRaw}/{memberRawName}";
                var memberCanonical = CanonicalizePath(memberQualified);
                if (!string.IsNullOrEmpty(memberCanonical))
                {
                    containsEdges.Add(new { type = "contains", targetName = memberCanonical });
                }
            }
            relationships = containsEdges.ToArray();
        }

        // Top-level declarations get an artifact→element contains edge.
        // Top-level here means the declaration is NOT a member of a
        // containing type — its parent is a CompilationUnit or a
        // namespace declaration.
        if (IsTopLevelDeclaration(node))
        {
            artifactEdges.Add(new { type = "contains", targetName = name });
        }

        if (state == "deferred" && stateReason == null)
        {
            problems.Add(new { severity = "warning", message = $"Element \"{name}\" is marked deferred but missing state-reason" });
        }

        // Physical-domain observations (per assertion-analysis#physical-observations).
        // Computed from the AST node's line span and body statements.
        // `observation` is omitted entirely if no domain has measurements.
        var physical = AnalysisHelpers.ExtractPhysicalObservations(node, tree, elementType);

        // Declarable taxonomy (task C3 of reference/node-normalization.md
        // adoption). Additive to elementType during migration; task D1
        // replaces elementType with role + elementKind.
        var declarable = AnalysisHelpers.MapToDeclarable(node);

        // signatureHash inputs (WI-6/7/8).
        var fqn = DeriveFullyQualifiedName(semanticModel, node);
        var parameterTypes = ExtractParameterTypes(semanticModel, node);

        var element = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["elementKind"] = "code",
            ["content"] = sourceText,
            ["contentHash"] = contentHash,
            ["edges"] = relationships
        };

        if (fqn != null)
            element["fullyQualifiedName"] = fqn;
        if (parameterTypes != null)
            element["parameterTypes"] = parameterTypes;
        if (derivesFrom != null)
            element["derivesFrom"] = derivesFrom;
        if (state != null)
            element["state"] = state;
        if (stateReason != null)
            element["stateReason"] = stateReason;
        if (audits.Length > 0)
            element["audits"] = audits;
        if (physical != null)
            element["observation"] = new { physical };
        if (declarable != null)
        {
            element["role"] = declarable.Value.role;
            element["flavors"] = declarable.Value.flavors;
            element["capabilities"] = declarable.Value.capabilities;
        }

        elements.Add(element);
    }

    return (elements.ToArray(), artifactEdges.ToArray(), problems.ToArray());
}

/// <summary>
/// Canonicalize a raw name path segment by segment so the `/` separator
/// used for class/member containment is preserved. `MyClass/MyMethod`
/// becomes `myclass/mymethod`.
/// </summary>
static string CanonicalizePath(string raw)
{
    var parts = raw.Split('/')
        .Select(Canonicalize)
        .Where(s => !string.IsNullOrEmpty(s));
    return string.Join("/", parts);
}

/// <summary>
/// Walk up the syntax tree from a member declaration to find its
/// containing type and compose a qualified raw name `TypeName/MemberName`.
/// For top-level declarations (not nested in a type), returns the raw
/// name unchanged.
/// </summary>
static string GetQualifiedRawName(SyntaxNode node, string rawName)
{
    // Type members (method, property, constructor, destructor, field,
    // event, indexer, operator, enum member) get qualified with their
    // containing type.
    if (node is MethodDeclarationSyntax
        || node is PropertyDeclarationSyntax
        || node is ConstructorDeclarationSyntax
        || node is DestructorDeclarationSyntax
        || node is FieldDeclarationSyntax
        || node is EventDeclarationSyntax
        || node is EventFieldDeclarationSyntax
        || node is IndexerDeclarationSyntax
        || node is OperatorDeclarationSyntax
        || node is ConversionOperatorDeclarationSyntax
        || node is EnumMemberDeclarationSyntax
        || node is LocalFunctionStatementSyntax)
    {
        var parentType = node.Parent as BaseTypeDeclarationSyntax;
        if (parentType != null)
        {
            var parentRaw = parentType.Identifier.Text;
            var parentQualified = GetQualifiedRawName(parentType, parentRaw);
            return $"{parentQualified}/{rawName}";
        }
    }
    // Parameters and type parameters qualify under their enclosing
    // callable (method, constructor, local function, operator, indexer).
    if (node is ParameterSyntax || node is TypeParameterSyntax)
    {
        var enclosingMember = FindEnclosingMember(node);
        if (enclosingMember != null)
        {
            var memberRaw = GetDeclarationName(enclosingMember);
            if (memberRaw != null)
            {
                var memberQualified = GetQualifiedRawName(enclosingMember, memberRaw);
                // Disambiguate typeParameter from parameter with same name
                var prefix = node is TypeParameterSyntax ? "type-param-" : "";
                return $"{memberQualified}/{prefix}{rawName}";
            }
        }
    }
    // Accessors (get/set) qualify under their enclosing property/indexer.
    if (node is AccessorDeclarationSyntax)
    {
        var enclosingMember = node.Parent?.Parent;
        if (enclosingMember != null)
        {
            var memberRaw = GetDeclarationName(enclosingMember);
            if (memberRaw != null)
            {
                var memberQualified = GetQualifiedRawName(enclosingMember, memberRaw);
                return $"{memberQualified}/{rawName}";
            }
        }
    }
    // Attributes qualify under their enclosing declaration.
    if (node is AttributeSyntax)
    {
        var decl = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (decl != null)
        {
            var declRaw = GetDeclarationName(decl);
            if (declRaw != null)
            {
                var declQualified = GetQualifiedRawName(decl, declRaw);
                return $"{declQualified}/attr-{rawName}";
            }
        }
    }
    // Nested types: walk up to the nearest containing type.
    if (node is BaseTypeDeclarationSyntax && node.Parent is BaseTypeDeclarationSyntax enclosing)
    {
        var enclosingRaw = enclosing.Identifier.Text;
        var enclosingQualified = GetQualifiedRawName(enclosing, enclosingRaw);
        return $"{enclosingQualified}/{rawName}";
    }
    return rawName;
}

static SyntaxNode? FindEnclosingMember(SyntaxNode node)
{
    var current = node.Parent;
    while (current != null)
    {
        if (current is MethodDeclarationSyntax
            || current is ConstructorDeclarationSyntax
            || current is DestructorDeclarationSyntax
            || current is OperatorDeclarationSyntax
            || current is ConversionOperatorDeclarationSyntax
            || current is IndexerDeclarationSyntax
            || current is LocalFunctionStatementSyntax
            || current is BaseTypeDeclarationSyntax)
        {
            return current;
        }
        current = current.Parent;
    }
    return null;
}

/// <summary>
/// True if the declaration is at the top level of the file — its parent
/// is a compilation unit or namespace, not another type declaration.
/// </summary>
static bool IsTopLevelDeclaration(SyntaxNode node)
{
    var parent = node.Parent;
    return parent is CompilationUnitSyntax
        || parent is NamespaceDeclarationSyntax
        || parent is FileScopedNamespaceDeclarationSyntax;
}

/// <bds audit="verifiability pass 4c4e814d66ba883194203a820f99ea269ef96ac5072c7add5d8a5e81613d6861 2026-04-09T00:57:42.227Z" />
static string? GetDeclarationName(SyntaxNode node) => node switch
{
    ClassDeclarationSyntax c => c.Identifier.Text,
    InterfaceDeclarationSyntax i => i.Identifier.Text,
    StructDeclarationSyntax s => s.Identifier.Text,
    EnumDeclarationSyntax e => e.Identifier.Text,
    MethodDeclarationSyntax m => m.Identifier.Text,
    PropertyDeclarationSyntax p => p.Identifier.Text,
    LocalFunctionStatementSyntax l => l.Identifier.Text,
    ConstructorDeclarationSyntax => "constructor",
    DestructorDeclarationSyntax => "destructor",
    EnumMemberDeclarationSyntax em => em.Identifier.Text,
    FieldDeclarationSyntax fd => fd.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
    EventFieldDeclarationSyntax efd => efd.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
    EventDeclarationSyntax ed => ed.Identifier.Text,
    IndexerDeclarationSyntax => "indexer",
    OperatorDeclarationSyntax od => $"operator-{od.OperatorToken.Text}",
    ConversionOperatorDeclarationSyntax cod => $"operator-{cod.Type.ToString().Replace(" ", "-")}",
    ParameterSyntax p => p.Identifier.Text,
    TypeParameterSyntax tp => tp.Identifier.Text,
    AccessorDeclarationSyntax ad => ad.Keyword.Text,
    AttributeSyntax a => a.Name.ToString(),
    _ => null
};

/// <bds audit="verifiability pass 9e8def13ea48e619f3f54b32f98b7baa68fc582ea6ce6b37d375dea52b0c81e7 2026-04-09T00:57:42.227Z" />
static string? GetElementType(SyntaxNode node) => node switch
{
    ClassDeclarationSyntax => "class",
    InterfaceDeclarationSyntax => "interface",
    StructDeclarationSyntax => "struct",
    EnumDeclarationSyntax => "enum",
    MethodDeclarationSyntax => "method",
    PropertyDeclarationSyntax => "property",
    LocalFunctionStatementSyntax => "method",
    ConstructorDeclarationSyntax => "constructor",
    DestructorDeclarationSyntax => "destructor",
    EnumMemberDeclarationSyntax => "enumMember",
    FieldDeclarationSyntax => "field",
    EventFieldDeclarationSyntax => "event",
    EventDeclarationSyntax => "event",
    IndexerDeclarationSyntax => "indexer",
    OperatorDeclarationSyntax => "operator",
    ConversionOperatorDeclarationSyntax => "operator",
    ParameterSyntax => "parameter",
    TypeParameterSyntax => "typeParameter",
    AccessorDeclarationSyntax => "accessor",
    AttributeSyntax => "annotation",
    _ => null
};

// --- signatureHash support (reference/signature-hash-close.md WI-6/7) ---

/// <summary>
/// Derive a fully-qualified name from the Roslyn SemanticModel.
/// Uses FullyQualifiedFormat with "global::" prefix stripped.
/// Returns null when no symbol can be resolved (rare — e.g.,
/// malformed syntax or error-recovery nodes).
/// </summary>
static string? DeriveFullyQualifiedName(SemanticModel model, SyntaxNode node)
{
    var symbol = model.GetDeclaredSymbol(node);
    if (symbol == null) return null;
    var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    if (fqn.StartsWith("global::"))
        fqn = fqn.Substring("global::".Length);
    return fqn;
}

/// <summary>
/// Extract a normalized parameter type list for callable declarations.
/// Per §3.2 of signature-hash-close.md: "{modifier} {type}" where
/// modifier ∈ {"", "ref", "out", "in", "params"}, type is fully-
/// qualified with nullable annotations stripped.
/// Returns null for non-callable nodes (types, properties, fields).
/// </summary>
static string[]? ExtractParameterTypes(SemanticModel model, SyntaxNode node)
{
    var symbol = model.GetDeclaredSymbol(node);
    ImmutableArray<IParameterSymbol>? parameters = symbol switch
    {
        IMethodSymbol m => m.Parameters,
        IPropertySymbol { IsIndexer: true } p => p.Parameters,
        _ => null
    };
    if (parameters == null) return null;

    var result = new string[parameters.Value.Length];
    for (int i = 0; i < parameters.Value.Length; i++)
    {
        var p = parameters.Value[i];
        var typeText = p.Type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithMiscellaneousOptions(
                    SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                    & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier))
            .Replace("global::", "");

        var modifier = p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => ""
        };
        if (p.IsParams) modifier = "params ";

        result[i] = $"{modifier}{typeText}".Trim();
    }
    return result;
}

// Declarable Taxonomy Mapping + physical-metrics extraction moved to
// Program.Analysis.cs (component="analysis") per the analyzer plugin's
// two-sided concern: (a) fulfilling the orchestrator subprocess
// contract in this file, (b) producing design-analysis observation
// fields in AnalysisHelpers.

// --- Derives-From Extraction ---

/// <bds derives-from="scenario-orchestrator#element-inventory/derives-from-extraction" />
static string? ExtractDerivesFrom(SyntaxNode node)
{
    var trivia = node.GetLeadingTrivia();
    var pattern = new Regex(@"<(?:bds|fathom)\s+derives-from=""([^""]+)""\s*/>");

    foreach (var t in trivia)
    {
        if (t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            t.IsKind(SyntaxKind.SingleLineCommentTrivia))
        {
            var match = pattern.Match(t.ToFullString());
            if (match.Success) return match.Groups[1].Value;
        }
    }

    return null;
}

// --- State Extraction ---

/// <bds derives-from="scenario-orchestrator#element-inventory/element-properties" />
static (string? state, string? stateReason) ExtractState(SyntaxNode node)
{
    var trivia = node.GetLeadingTrivia();
    var statePattern = new Regex(@"<(?:bds|fathom)\s+state=""(active|deferred)""\s*/>");
    var reasonPattern = new Regex(@"<(?:bds|fathom)\s+state-reason=""([^""]+)""\s*/>");

    string? state = null;
    string? stateReason = null;

    foreach (var t in trivia)
    {
        if (!t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
            !t.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;

        var text = t.ToFullString();
        var sMatch = statePattern.Match(text);
        if (sMatch.Success) state = sMatch.Groups[1].Value;
        var rMatch = reasonPattern.Match(text);
        if (rMatch.Success) stateReason = rMatch.Groups[1].Value;
    }

    return (state, stateReason);
}

// --- Audit Annotation Extraction ---

/// <bds derives-from="scenario-orchestrator#element-inventory/element-edges" />
static object[] ExtractAudits(SyntaxNode node)
{
    var trivia = node.GetLeadingTrivia();
    // New graph-store format: first field is `{direction}:{anchor-node-id}`.
    // Legacy format: first field is just the direction. Parsers accept
    // both during the transition; writers emit the new form only.
    var pattern = new Regex(@"<(?:bds|fathom)\s+audit=""(derivability|verifiability|completeness)(?::(\S+))?\s+(\S+)\s+(\S+)\s+(\S+)""\s*/>");
    var audits = new List<object>();

    foreach (var t in trivia)
    {
        if (!t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
            !t.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;

        var text = t.ToFullString();
        var matches = pattern.Matches(text);
        foreach (Match m in matches)
        {
            var direction = m.Groups[1].Value;
            var anchorNodeId = m.Groups[2].Success ? m.Groups[2].Value : null;
            var verdict = m.Groups[3].Value;
            var hash = m.Groups[4].Value;
            var timestamp = m.Groups[5].Value;
            if (verdict == "pass" || verdict == "fail" || verdict == "insufficient-context")
            {
                var judgmentId = anchorNodeId != null ? $"{direction}:{anchorNodeId}" : null;
                audits.Add(new { judgmentId, direction, verdict, hash, timestamp });
            }
        }
    }

    return audits.ToArray();
}

/// <bds derives-from="scenario-orchestrator#content-hashing/hash-computation" />
static string StripAuditAnnotations(string sourceText)
{
    // Remove any /// <bds audit="..." /> or /// <fathom audit="..." /> lines before hashing
    var pattern = new Regex(@"^\s*///\s*<(?:bds|fathom)\s+audit=""[^""]+""\s*/>\s*\r?\n?", RegexOptions.Multiline);
    return pattern.Replace(sourceText, "");
}

// --- Relationship Extraction ---

/// <bds derives-from="scenario-orchestrator#net-analyzer/net-element-edges" />
/// <bds audit="verifiability pass 8349a6199e7135585762d1f200fd92ecdf5abe7e381b25d7e33c5094810accf3 2026-04-09T00:57:42.227Z" />
static object[] ExtractRelationships(SyntaxNode node, HashSet<string> allNames, string mode)
{
    // Edge targetName is emitted in canonical form so downstream matching
    // (both intra-file and cross-file through the graph builder) compares on
    // the BDS canonical identity that element.name uses. The allNames lookup
    // continues to use raw source text — it's matching the AST, not BDS IDs.
    var relationships = new List<object>();
    var seen = new HashSet<string>();
    var isFullMode = mode == "full";

    // Add an edge with a subtype. The dedup key is `{subtype-or-type}:{name}`
    // plus a registration of the higher-order `{type}:{name}` key so the
    // references catchall at the end skips anything already captured by a
    // specific subtype.
    void Add(string type, string subtype, string rawName)
    {
        var canonical = Canonicalize(rawName);
        if (string.IsNullOrEmpty(canonical)) return;
        var subtypeKey = $"{subtype}:{canonical}";
        if (seen.Contains(subtypeKey)) return;
        seen.Add(subtypeKey);
        seen.Add($"{type}:{canonical}");
        relationships.Add(new { type, subtype, targetName = canonical });
    }

    // Inherits: base types. Classes get `extends` for the first base (if it
    // is a class) and `implements` for the rest; interfaces and structs treat
    // every base as `implements`. Without a semantic model we use the C#
    // convention: for classes the first entry in BaseList is the base class
    // when present, otherwise all are interfaces. Interfaces and structs
    // cannot have a base class in C#, so every base is an interface.
    if (node is TypeDeclarationSyntax typeDecl && typeDecl.BaseList != null)
    {
        var isClass = node is ClassDeclarationSyntax;
        var baseTypes = typeDecl.BaseList.Types.ToList();
        for (var i = 0; i < baseTypes.Count; i++)
        {
            var name = baseTypes[i].Type.ToString().Split('<')[0].Split('.').Last();
            if (!allNames.Contains(name)) continue;
            // Heuristic: first base on a class is treated as extends unless
            // it visually looks like an interface (leading 'I' + uppercase).
            // This matches C# convention; a semantic model would be more
            // reliable but isn't available without full compilation.
            var isInterfaceShaped = name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);
            var subtype = (isClass && i == 0 && !isInterfaceShaped) ? "extends" : "implements";
            Add("inherits", subtype, name);
        }
    }

    // Contains: members of a type. No subtype (uniform across languages).
    if (node is TypeDeclarationSyntax container)
    {
        foreach (var member in container.Members)
        {
            var memberName = GetDeclarationName(member);
            if (memberName != null)
            {
                var canonical = Canonicalize(memberName);
                if (string.IsNullOrEmpty(canonical)) continue;
                var key = $"contains:{canonical}";
                if (seen.Contains(key)) continue;
                seen.Add(key);
                relationships.Add(new { type = "contains", targetName = canonical });
            }
        }
    }

    // Uses: type references in parameters, return types, properties.
    if (node is MethodDeclarationSyntax method)
    {
        var returnType = method.ReturnType.ToString().Split('<')[0].Split('.').Last();
        if (allNames.Contains(returnType) && returnType != GetDeclarationName(node))
            Add("uses", "typeRef", returnType);

        foreach (var param in method.ParameterList.Parameters)
        {
            var paramType = param.Type?.ToString().Split('<')[0].Split('.').Last();
            if (paramType != null && allNames.Contains(paramType))
                Add("uses", "typeRef", paramType);
        }
    }

    // Calls: method invocations within methods.
    if (node is MethodDeclarationSyntax || node is LocalFunctionStatementSyntax)
    {
        foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? calledName = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null
            };
            if (calledName != null && allNames.Contains(calledName) && calledName != GetDeclarationName(node))
                Add("calls", "invokes", calledName);
        }
    }

    // Full-mode: instantiates — `new X()` via ObjectCreationExpressionSyntax.
    if (isFullMode)
    {
        foreach (var creation in node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString().Split('<')[0].Split('.').Last();
            if (allNames.Contains(typeName) && typeName != GetDeclarationName(node))
                Add("calls", "instantiates", typeName);
        }
    }

    // Full-mode: overrides — methods with the `override` modifier. Emitted
    // on the containing type element; target is the method name.
    if (isFullMode && node is TypeDeclarationSyntax overrideContainer)
    {
        foreach (var member in overrideContainer.Members.OfType<MethodDeclarationSyntax>())
        {
            if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
            {
                Add("inherits", "overrides", member.Identifier.Text);
            }
        }
    }

    // Full-mode: delegates — event subscriptions `target += handler`.
    // C#-specific; models the invocation-via-delegate relationship.
    if (isFullMode)
    {
        foreach (var assign in node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assign.IsKind(SyntaxKind.AddAssignmentExpression)) continue;
            // The right-hand side is the handler being subscribed.
            var handlerName = assign.Right switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null
            };
            if (handlerName != null && allNames.Contains(handlerName))
                Add("calls", "delegates", handlerName);
        }
    }

    // Full-mode: genericConstraint — `where T : Foo` clauses on a type or
    // method declaration. Each constraint type becomes a uses edge.
    if (isFullMode)
    {
        IEnumerable<TypeParameterConstraintClauseSyntax> constraintClauses = node switch
        {
            TypeDeclarationSyntax td => td.ConstraintClauses,
            MethodDeclarationSyntax md => md.ConstraintClauses,
            _ => Array.Empty<TypeParameterConstraintClauseSyntax>()
        };
        foreach (var clause in constraintClauses)
        {
            foreach (var constraint in clause.Constraints.OfType<TypeConstraintSyntax>())
            {
                var constraintName = constraint.Type.ToString().Split('<')[0].Split('.').Last();
                if (allNames.Contains(constraintName) && constraintName != GetDeclarationName(node))
                    Add("uses", "genericConstraint", constraintName);
            }
        }
    }

    // Full-mode: decorates — `[Attribute]` applications on classes, methods,
    // properties, parameters. Target is the attribute name (sans suffix).
    if (isFullMode)
    {
        foreach (var attrList in node.DescendantNodes().OfType<AttributeListSyntax>())
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString().Split('<')[0].Split('.').Last();
                // C# allows `[Foo]` to reference `FooAttribute`; strip the suffix
                // for canonical matching against element names.
                if (attrName.EndsWith("Attribute")) attrName = attrName[..^9];
                if (allNames.Contains(attrName))
                    Add("uses", "decorates", attrName);
            }
        }
    }

    // Full-mode: partial — partial class split across files. Emits a `partial`
    // edge pointing at the type's own canonical name as a shared-identity
    // marker. Cross-file resolution unifies these downstream.
    if (isFullMode && node is TypeDeclarationSyntax partialCandidate)
    {
        if (partialCandidate.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            var canonical = Canonicalize(partialCandidate.Identifier.Text);
            if (!string.IsNullOrEmpty(canonical))
            {
                var key = $"partial:{canonical}";
                if (!seen.Contains(key))
                {
                    seen.Add(key);
                    relationships.Add(new { type = "partial", targetName = canonical });
                }
            }
        }
    }

    // References: identifiers not already captured by other relationship types.
    foreach (var id in node.DescendantNodes().OfType<IdentifierNameSyntax>())
    {
        var idName = id.Identifier.Text;
        if (allNames.Contains(idName) && idName != GetDeclarationName(node))
        {
            var canonical = Canonicalize(idName);
            if (string.IsNullOrEmpty(canonical)) continue;
            if (seen.Contains($"inherits:{canonical}") || seen.Contains($"contains:{canonical}")
                || seen.Contains($"uses:{canonical}") || seen.Contains($"calls:{canonical}")
                || seen.Contains($"references:{canonical}")) continue;
            seen.Add($"references:{canonical}");
            relationships.Add(new { type = "references", subtype = "identifier", targetName = canonical });
        }
    }

    return relationships.ToArray();
}

// --- Utility ---

/// <bds audit="verifiability pass 4cdcd1efef1c37100cb7784d5513e9c1031674096a3b0c67391d2e7b5fc4f28c 2026-04-09T00:57:42.228Z" />
static string ComputeHash(string content)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content.Trim()));
    return Convert.ToHexStringLower(bytes);
}

/// <bds derives-from="scenario-orchestrator#element-inventory/element-properties" />
/// <summary>
/// Apply the BDS canonicalization rule to a raw source identifier or heading
/// text, producing a path component suitable for use in a canonical element ID.
/// Rule: lowercase, replace non-alphanumeric runs with single "-", trim.
/// BDS IDs are owned by BDS — source identifiers remain unchanged in
/// element.content; canonicalization affects element.name only.
/// </summary>
static string Canonicalize(string raw)
{
    if (string.IsNullOrEmpty(raw)) return string.Empty;
    var lower = raw.ToLowerInvariant();
    var sb = new StringBuilder();
    var lastDash = true; // suppress leading dash
    foreach (var c in lower)
    {
        if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
        {
            sb.Append(c);
            lastDash = false;
        }
        else if (!lastDash)
        {
            sb.Append('-');
            lastDash = true;
        }
    }
    var result = sb.ToString();
    if (result.EndsWith('-')) result = result.Substring(0, result.Length - 1);
    return result;
}

/// <bds derives-from="scenario-orchestrator#net-analyzer/net-artifact-identity-extraction" />
static Dictionary<string, string>? ParseBdsIdentity(string content)
{
    var fields = new Dictionary<string, string>();
    var pattern = new Regex(@"///\s*<(?:bds|fathom)\s+([a-z-]+)=""([^""]*)""\s*/>");

    foreach (var line in content.Split('\n'))
    {
        var match = pattern.Match(line.TrimStart());
        if (match.Success)
        {
            fields[match.Groups[1].Value] = match.Groups[2].Value;
        }
    }

    return fields.Count > 0 ? fields : null;
}

/// <bds audit="verifiability pass 6b31b5a8be7c4cbba6858afbec70e3aa29d5d67c22d295da069d99d3055f6691 2026-04-09T00:57:42.228Z" />
static void Emit(object obj)
{
    Console.WriteLine(JsonSerializer.Serialize(obj));
}

/// <bds derives-from="scenario-orchestrator#analyzer-contract/subprocess-analyzer-rules" />
static AnalyzerArgs ParseArgs(string[] argv)
{
    var result = new AnalyzerArgs();

    for (int i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--path" when i + 1 < argv.Length:
                result.Path = argv[++i];
                break;
            case "--mode" when i + 1 < argv.Length:
                var m = argv[++i];
                if (m is "identity" or "structure" or "full")
                    result.Mode = m;
                // Unrecognized: fall back to structure
                break;
            case "--output":
                result.Direction = "output";
                break;
            case "--input":
                result.Direction = "input";
                break;
            case "--include" when i + 1 < argv.Length:
                result.Include.Add(argv[++i]);
                break;
            case "--exclude" when i + 1 < argv.Length:
                result.Exclude.Add(argv[++i]);
                break;
        }
    }

    return result;
}

/// <bds audit="verifiability pass 8a46fc69259b2fdd0710d9870292495653932d605440afbb060ddf4280196c73 2026-04-09T00:57:42.228Z" />
static List<string> DiscoverCsFiles(string rootPath, List<string> include, List<string> exclude)
{
    var results = new List<string>();
    WalkDirectory(rootPath, results, rootPath, include, exclude);
    return results;
}

/// <bds audit="verifiability pass 650f1e3d1904df986679789d83fd37f726fe690a31998b9ca0c72d8f43cde7e5 2026-04-09T00:57:42.228Z" />
static void WalkDirectory(string dir, List<string> results, string rootPath,
    List<string> include, List<string> exclude)
{
    try
    {
        foreach (var entry in Directory.GetFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);

            if (Directory.Exists(entry))
            {
                if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs")
                    continue;
                WalkDirectory(entry, results, rootPath, include, exclude);
            }
            else if (Path.GetExtension(entry) == ".cs")
            {
                var relPath = Path.GetRelativePath(rootPath, entry);
                if (MatchesFilter(relPath, include, exclude))
                    results.Add(entry);
            }
        }
    }
    catch { /* skip unreadable dirs */ }
}

/// <bds audit="verifiability pass c7e83e384589a90efe9721729bdea47c73266ebcb66fcdbb63f1fc305d37587d 2026-04-09T00:57:42.228Z" />
static bool MatchesFilter(string path, List<string> include, List<string> exclude)
{
    foreach (var pattern in exclude)
    {
        if (path.Contains(pattern.Replace("**", "").Replace("*", "")))
            return false;
    }

    if (include.Count > 0)
    {
        return include.Any(pattern =>
            path.Contains(pattern.Replace("**", "").Replace("*", "")));
    }

    return true;
}

/// <bds audit="verifiability pass 94f1cf2c92a6a4833cb7bbc1097429662af0809967cd6ea07a36e7c64459c053 2026-04-09T00:57:42.229Z" />
class AnalyzerArgs
{
    /// <bds audit="verifiability pass 28e8c008877a2dca2260721b92c255df8bb213eb3abec52d17c57737496fdb79 2026-04-09T00:57:42.229Z" />
    public string Path { get; set; } = "";
    /// <bds audit="verifiability pass 341d9d88585fbc052e6d91b9539840b3778116d2878fdcb804b6c2b59ed77564 2026-04-09T00:57:42.229Z" />
    public string Mode { get; set; } = "structure";
    /// <bds audit="verifiability pass 5d4ec38764d5bacaf91ddec2b1d69df2879a84849a7277da19e2b0a6602fa1df 2026-04-09T00:57:42.229Z" />
    public string Direction { get; set; } = "output";
    /// <bds audit="verifiability pass b11e3e89495520d2f90f8a11e4f4791a851139ddfe7ead707df300e02e15ce41 2026-04-09T00:57:42.229Z" />
    public List<string> Include { get; } = new();
    /// <bds audit="verifiability pass 93f24bd478ca5e24cf7cfe696ff0da82dc95814cd5f6b09e26921c1ed6d2ad62 2026-04-09T00:57:42.230Z" />
    public List<string> Exclude { get; } = new();
}

/// <bds audit="verifiability pass 2abd784eca2676beeefe947ae617f6ac0f739b85a816ad689e683a4846193e93 2026-04-09T00:57:42.230Z" />
class UpdateRequest
{
    /// <bds audit="verifiability pass 721e9deb51746f37bebffb86bccef93647fe52f65efd9e9418e9e0e748c58fdc 2026-04-09T00:57:42.230Z" />
    public string ArtifactId { get; set; } = "";
    /// <bds audit="verifiability pass 76e925ea7c29397f99f74d0821a37a84efd8e5bd123bdddf71411ab1e0f42836 2026-04-09T00:57:42.230Z" />
    public string? ElementName { get; set; }
    /// <bds audit="verifiability pass 76fbe3d0e3958b6b2f331555a3b59a2fccea1349b737f777b86e5fd2d471f534 2026-04-09T00:57:42.230Z" />
    public JsonElement Fields { get; set; }
}

/// <bds audit="verifiability pass 9a3c819937c1559ee9ede46849b1cf07d5001e014d0b944a920518681d7755aa 2026-04-09T00:57:42.231Z" />
partial class Program
{
    /// <bds derives-from="scenario-orchestrator#analyzer-contract/subprocess-analyzer-rules" />
    public static (bool success, string? error) ApplyUpdatesToFile(string filePath, List<UpdateRequest> updates)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch (Exception ex) { return (false, $"Failed to read: {ex.Message}"); }

        foreach (var update in updates)
        {
            content = update.ElementName != null
                ? UpdateElement(content, update.ElementName, update.Fields)
                : UpdateArtifact(content, update.Fields);
        }

        try { File.WriteAllText(filePath, content); }
        catch (Exception ex) { return (false, $"Failed to write: {ex.Message}"); }
        return (true, null);
    }

    /// <bds derives-from="scenario-orchestrator#analyzer-contract/subprocess-analyzer-rules" />
    static string UpdateArtifact(string content, JsonElement fields)
    {
        if (fields.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            content = UpdateBdsField(content, "status", status.GetString()!);
        if (fields.TryGetProperty("supersededBy", out var sb) && sb.ValueKind == JsonValueKind.String)
            content = UpdateBdsField(content, "superseded-by", sb.GetString()!);
        return content;
    }

    /// <bds audit="verifiability pass 8f2279fd836b1435bf96d021c17199b5805d678a4265f1b0f715dc8cbaa79493 2026-04-09T00:57:42.231Z" />
    static string UpdateBdsField(string content, string field, string value)
    {
        // Try to update an existing /// <bds ... /> or /// <fathom ... /> at file level
        var pattern = new Regex($@"(///\s*<(?:bds|fathom)\s+{field}=)""[^""]*""(\s*/>)");
        if (pattern.IsMatch(content))
        {
            return pattern.Replace(content, $"$1\"{value}\"$2");
        }
        // Insert before the first non-comment line if no existing field
        return content;
    }

    /// <bds derives-from="scenario-orchestrator#analyzer-contract/subprocess-analyzer-rules" />
    static string UpdateElement(string content, string elementName, JsonElement fields)
    {
        // Find the declaration. C# matches:
        //   class Name | interface Name | struct Name | enum Name
        //   ReturnType Name(  | int Name {  for methods/properties
        var lines = content.Split('\n');
        var escName = Regex.Escape(elementName);
        var declPattern = new Regex(
            $@"^([ \t]*)(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|virtual|override|async|\s)*\s*(?:class|interface|struct|enum|record)\s+{escName}\b" +
            $@"|^([ \t]*)(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|virtual|override|async|\s)*\s*\w[\w<>,\s\?\[\]]*\s+{escName}\s*[\(\{{]"
        );

        int declIdx = -1;
        string indent = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var match = declPattern.Match(lines[i]);
            if (match.Success)
            {
                declIdx = i;
                indent = (match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : match.Groups[2].Value);
                break;
            }
        }
        if (declIdx == -1) return content;

        // Walk backward to find leading /// <bds ... /> or /// <fathom ... /> annotation block
        int blockStart = declIdx;
        for (int i = declIdx - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("///")) { blockStart = i; continue; }
            if (string.IsNullOrWhiteSpace(trimmed)) { continue; }
            break;
        }

        // Categorize existing annotation lines
        var preserveDerivesFrom = new List<string>();
        string? preserveState = null;
        string? preserveStateReason = null;
        var preserveAudits = new List<string>();
        var preserveOther = new List<string>();

        var derivesPattern = new Regex(@"^\s*///\s*<(?:bds|fathom)\s+derives-from=");
        var statePattern = new Regex(@"^\s*///\s*<(?:bds|fathom)\s+state=""(active|deferred)""");
        var reasonPattern = new Regex(@"^\s*///\s*<(?:bds|fathom)\s+state-reason=");
        var auditPattern = new Regex(@"^\s*///\s*<(?:bds|fathom)\s+audit=");

        for (int i = blockStart; i < declIdx; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (derivesPattern.IsMatch(line)) preserveDerivesFrom.Add(line);
            else if (statePattern.IsMatch(line)) preserveState = line;
            else if (reasonPattern.IsMatch(line)) preserveStateReason = line;
            else if (auditPattern.IsMatch(line)) preserveAudits.Add(line);
            else if (trimmed.StartsWith("///")) preserveOther.Add(line);
        }

        // Apply updates
        if (fields.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String)
        {
            preserveState = $"{indent}/// <fathom state=\"{stateEl.GetString()}\" />";
        }
        if (fields.TryGetProperty("stateReason", out var srEl) && srEl.ValueKind == JsonValueKind.String)
        {
            preserveStateReason = $"{indent}/// <fathom state-reason=\"{srEl.GetString()}\" />";
        }
        if (fields.TryGetProperty("audits", out var auditsEl) && auditsEl.ValueKind == JsonValueKind.Array)
        {
            preserveAudits.Clear();
            foreach (var a in auditsEl.EnumerateArray())
            {
                var dir = a.GetProperty("direction").GetString();
                var verdict = a.GetProperty("verdict").GetString();
                var hash = a.GetProperty("hash").GetString();
                var ts = a.GetProperty("timestamp").GetString();
                // New graph-store format: first field is `{direction}:{anchor-node-id}`.
                // Legacy annotations without a judgment ID fall back to the four-field form.
                string head = dir!;
                if (a.TryGetProperty("judgmentId", out var jidEl) && jidEl.ValueKind == JsonValueKind.String)
                {
                    head = jidEl.GetString()!;
                }
                preserveAudits.Add($"{indent}/// <fathom audit=\"{head} {verdict} {hash} {ts}\" />");
            }
        }

        // Reassemble
        var newAnnotations = new List<string>();
        newAnnotations.AddRange(preserveDerivesFrom);
        if (preserveState != null) newAnnotations.Add(preserveState);
        if (preserveStateReason != null) newAnnotations.Add(preserveStateReason);
        newAnnotations.AddRange(preserveAudits);
        newAnnotations.AddRange(preserveOther);

        var result = new List<string>();
        result.AddRange(lines.Take(blockStart));
        result.AddRange(newAnnotations);
        result.AddRange(lines.Skip(declIdx));
        return string.Join("\n", result);
    }
}
