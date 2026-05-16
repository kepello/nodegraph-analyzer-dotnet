/// <summary>
/// .NET / C# subprocess analyzer for @kepello/nodegraph-analysis.
///
/// Walks each .cs file with Roslyn and emits an `AnalyzerArtifact`
/// per the 0.4.0 wire contract. The analyzer is BDS-agnostic: it
/// extracts pure structural elements (classes, methods, properties,
/// etc.), their dependency edges, and size observations. Governance
/// vocabulary (`derives-from`, `state`, `audit`, frontmatter-style
/// identity) is consumer-side concern — a bds-v3 bridge reads
/// element.leadingComment for any tags it cares about.
///
/// Read-only. Audit-annotation write-back lives in the consumer.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var cliArgs = ParseArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());

if (string.IsNullOrEmpty(cliArgs.Path))
{
    Console.Error.WriteLine("Error: --path <repo-root> is required");
    Environment.Exit(1);
}

var loadedConfig = ReadAnalyzerConfigFromStdin();

if (cliArgs.Discover)
{
    foreach (var filePath in DiscoverFiles(cliArgs.Path, loadedConfig.Include, loadedConfig.Exclude))
    {
        Console.Out.WriteLine(filePath);
    }
    return;
}

RunOutput(cliArgs, loadedConfig);

static void RunOutput(AnalyzerArgs args, AnalyzerConfig config)
{
    Console.Error.WriteLine($".NET analyzer (output): scanning {args.Path}");

    var allFiles = DiscoverFiles(args.Path, config.Include, config.Exclude);
    var csFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
    var csprojFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
    var slnFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".sln", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.Error.WriteLine($".NET analyzer: found {csFiles.Count} .cs, {csprojFiles.Count} .csproj, {slnFiles.Count} .sln");

    var startTime = DateTime.UtcNow;
    var elementsEmitted = 0;

    // Per language-conformance row 4.1.2 Stage 3b (2026-05-16) — project-
    // level analysis pivot. Build ONE Compilation containing every .cs
    // file in the source dir so the SemanticModel resolves cross-file
    // references natively. Cross-file edges (calls, references, etc.)
    // emit `targetRef` with the resolved target's file natural key.
    //
    // Per operator preference (notes.md 2026-05-16 project-level pivot):
    // no per-file fallback. The Compilation is the analyzer's runtime.
    var fileContents = new Dictionary<string, string>();
    var syntaxTrees = new List<SyntaxTree>();
    foreach (var filePath in csFiles)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            fileContents[filePath] = content;
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(content, path: filePath));
        }
        catch (Exception ex)
        {
            Emit(new { type = "error", message = $"Failed to read source: {ex.Message}", filePath });
        }
    }
    var sharedCompilation = CSharpCompilation.Create(
        "FathomAnalysis",
        syntaxTrees: syntaxTrees,
        references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

    // Per-file iteration. Reuses the shared Compilation so SemanticModel
    // queries during edge emission have cross-file context. Parallel
    // ordering preserved; Console.WriteLine is atomic per call in .NET.
    Parallel.ForEach(syntaxTrees, tree =>
    {
        var filePath = tree.FilePath;
        try
        {
            if (!fileContents.TryGetValue(filePath, out var content)) return;
            var artifact = BuildArtifact(content, filePath, config.IncludeComments, sharedCompilation, tree);
            if (artifact == null) return;
            Emit(new { type = "artifact", artifact });
            Interlocked.Increment(ref elementsEmitted);
        }
        catch (Exception ex)
        {
            Emit(new { type = "error", message = $"Failed to process: {ex.Message}", filePath });
        }
    });

    // Structural emission for project + solution files. XML / text
    // parsing is cheap; sequential is fine and avoids any concern about
    // emitting cross-artifact edges in non-deterministic order.
    foreach (var filePath in csprojFiles)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var artifact = ProjectFileHelpers.BuildCsprojArtifact(content, filePath);
            if (artifact != null)
            {
                Emit(new { type = "artifact", artifact });
                Interlocked.Increment(ref elementsEmitted);
            }
        }
        catch (Exception ex)
        {
            Emit(new { type = "error", message = $"Failed to process .csproj: {ex.Message}", filePath });
        }
    }

    foreach (var filePath in slnFiles)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var artifact = ProjectFileHelpers.BuildSlnArtifact(content, filePath);
            if (artifact != null)
            {
                Emit(new { type = "artifact", artifact });
                Interlocked.Increment(ref elementsEmitted);
            }
        }
        catch (Exception ex)
        {
            Emit(new { type = "error", message = $"Failed to process .sln: {ex.Message}", filePath });
        }
    }

    var durationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
    Emit(new { type = "complete", elementsEmitted, durationMs });
}

// --- Artifact assembly ----------------------------------------------------

static object? BuildArtifact(
    string content,
    string filePath,
    bool includeComments,
    CSharpCompilation sharedCompilation,
    SyntaxTree sharedTree)
{
    var (elements, artifactEdges, problems) = DecomposeWithRoslyn(
        content, filePath, includeComments, sharedCompilation, sharedTree);

    var artifact = new Dictionary<string, object?>
    {
        ["id"] = filePath,
        ["filePath"] = filePath,
        ["language"] = "csharp",
        ["contentHash"] = ComputeHash(content),
        ["elements"] = elements,
    };
    if (artifactEdges.Length > 0) artifact["edges"] = DedupeEdges(artifactEdges).ToArray();
    if (problems.Length > 0) artifact["problems"] = problems;

    if (includeComments)
    {
        var fileLeading = ExtractFileLeadingComment(content);
        if (fileLeading != null) artifact["leadingComment"] = fileLeading;
    }

    return artifact;
}

/// <summary>
/// File-level leading comment: every comment line at the top of the
/// file before the first non-comment, non-whitespace token. Includes
/// blank lines between comment blocks (preserved verbatim) so consumers
/// see the original layout.
/// </summary>
static string? ExtractFileLeadingComment(string content)
{
    var lines = content.Split('\n');
    var collected = new List<string>();
    var sawComment = false;

    foreach (var raw in lines)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("///") || trimmed.StartsWith("//") || trimmed.StartsWith("/*"))
        {
            collected.Add(raw);
            sawComment = true;
            continue;
        }
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (sawComment) collected.Add(raw);
            continue;
        }
        // First non-comment non-blank line — stop.
        break;
    }

    if (!sawComment) return null;
    // Trim trailing blank lines for consumer cleanliness; preserve
    // blanks between comment blocks.
    while (collected.Count > 0 && string.IsNullOrWhiteSpace(collected[^1]))
        collected.RemoveAt(collected.Count - 1);
    return string.Join("\n", collected);
}

// --- Roslyn Decomposition ---

static (object[] elements, object[] artifactEdges, object[] problems) DecomposeWithRoslyn(
    string content, string filePath, bool includeComments,
    CSharpCompilation sharedCompilation, SyntaxTree tree)
{
    var root = tree.GetRoot();

    // Single-file compilation removed; using shared multi-file Compilation
    // for cross-file symbol resolution. Per language-conformance Stage 3b
    // (2026-05-16 project-level pivot). SemanticModel.GetSymbolInfo() now
    // resolves cross-file references natively.
    var semanticModel = sharedCompilation.GetSemanticModel(tree);
    var elements = new List<object>();
    var problems = new List<object>();
    var allNames = new HashSet<string>();
    var artifactEdges = new List<object>();

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

    // First pass: collect declaration names for edge resolution.
    foreach (var node in root.DescendantNodes())
    {
        var rawName = GetDeclarationName(node);
        if (rawName == null) continue;
        allNames.Add(rawName);
        var qualified = GetQualifiedRawName(node, rawName);
        if (qualified != rawName) allNames.Add(qualified);
    }

    // Second pass: extract elements with overload disambiguation.
    // For callable kinds (methods / constructors / destructors / operators
    // / indexers / local functions) the raw name is suffixed with a
    // parameter-type signature `(Type1,Type2,...)` so overload identity
    // stays stable across sibling reorderings / additions / deletions.
    // The previous `${count}` visit-order suffix re-keyed surviving
    // overloads whenever a sibling was added or removed, causing the
    // substrate to tombstone + re-insert physically-unchanged elements
    // (Fathom work row analyzers-overload-natural-key-retrofit 2.2.23).
    // The collision-counter logic stays as a defensive fallback in case
    // two callables produce the same signature (unusual but possible
    // with generics / nested types).
    var seenCanonical = new Dictionary<string, string>();
    var qualifiedNameCounts = new Dictionary<string, int>();

    foreach (var node in root.DescendantNodes())
    {
        var rawName = GetDeclarationName(node);
        if (rawName == null) continue;

        var elementKind = GetElementType(node);
        if (elementKind == null) continue;

        var qualifiedRaw = GetQualifiedRawName(node, rawName) + GetParamSignature(node);
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

        // Parent name: the canonical path with the last segment stripped.
        // Top-level declarations have no parent — the artifact contains
        // them directly via artifactEdges.
        string? parentName = null;
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash > 0) parentName = name.Substring(0, lastSlash);

        var sourceText = node.ToFullString().Trim();
        var contentHash = ComputeHash(sourceText);
        var relationships = ExtractRelationships(node, allNames, semanticModel, filePath);

        // Type declarations emit explicit contains edges to their direct
        // members. Core treats these as authoritative for containment.
        // Members get the same parameter-signature suffix the standalone
        // pass appends, so the contains-edge target matches the canonical
        // name the member resolves to when iterated independently.
        if (node is TypeDeclarationSyntax typeDecl)
        {
            var containsEdges = new List<object>(relationships);
            foreach (var member in typeDecl.Members)
            {
                var memberRawName = GetDeclarationName(member);
                if (memberRawName == null) continue;
                var memberQualified = $"{qualifiedRaw}/{memberRawName}{GetParamSignature(member)}";
                var memberCanonical = CanonicalizePath(memberQualified);
                if (!string.IsNullOrEmpty(memberCanonical))
                {
                    containsEdges.Add(new { type = "contains", targetName = memberCanonical });
                }
            }
            relationships = containsEdges.ToArray();
        }

        if (IsTopLevelDeclaration(node))
        {
            artifactEdges.Add(new { type = "contains", targetName = name });
        }

        var observation = AnalysisHelpers.ExtractObservation(node, tree);
        var declarable = AnalysisHelpers.MapToDeclarable(node);
        var fqn = DeriveFullyQualifiedName(semanticModel, node);
        var parameterTypes = ExtractParameterTypes(semanticModel, node);

        // Per-method scalars (slice 3): the 10 raw inputs the engine
        // needs for cyclomatic / cognitive / Halstead / MI / complexity-
        // density derivations. Returns null for nodes without a body.
        var scalars = ScalarHelpers.Extract(node);

        // Intra-class edges (slice 3, LCOM4 input): accessesField and
        // callsMethod fire from a method body to a member of the same
        // type. Cross-type references continue to use the existing
        // `references` / `calls` types so the cohesion graph stays
        // disconnected from the coupling graph (architecture.md §5).
        if (ScalarHelpers.HasExtractableBody(node))
        {
            var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (containingType != null)
            {
                var memberIndex = IntraClassHelpers.BuildIndex(containingType);
                var className = containingType.Identifier.Text;
                var canonicalClass = Canonicalize(className);
                var rels = relationships.ToList();
                foreach (var (edgeType, subtype, target) in IntraClassHelpers.ExtractEdges(node, memberIndex))
                {
                    var canonicalTarget = Canonicalize(target);
                    if (string.IsNullOrEmpty(canonicalTarget)) continue;
                    // Per language-conformance B1: qualify intra-class
                    // targets with the class name (`class/member`) so the
                    // emitted targetName resolves to the actual element's
                    // natural key. Without this, `accessesField` edges
                    // stayed dangling. Mirrors the TS analyzer's
                    // intra-class edge composition (Fathom row 3.2.1).
                    var qualifiedTarget = string.IsNullOrEmpty(canonicalClass)
                        ? canonicalTarget
                        : $"{canonicalClass}/{canonicalTarget}";
                    if (subtype is null)
                    {
                        rels.Add(new { type = edgeType, targetName = qualifiedTarget });
                    }
                    else
                    {
                        rels.Add(new { type = edgeType, subtype, targetName = qualifiedTarget });
                    }
                }
                relationships = rels.ToArray();
            }
        }

        var lineSpan = node.GetLocation().GetLineSpan();
        var sourceLocation = new
        {
            startLine = lineSpan.StartLinePosition.Line + 1,
            endLine = lineSpan.EndLinePosition.Line + 1,
            startColumn = lineSpan.StartLinePosition.Character + 1,
            endColumn = lineSpan.EndLinePosition.Character + 1,
        };

        // Per-element analyzer-emitted metadata mirrors the TS analyzer's
        // shape: role + flavors + capabilities + signature info. Consumers
        // read these from `metadata`; the wire's top-level fields stay
        // analyzer-agnostic.
        var metadata = new Dictionary<string, object?>();
        if (declarable != null)
        {
            metadata["role"] = declarable.Value.role;
            metadata["flavors"] = declarable.Value.flavors;
            metadata["capabilities"] = declarable.Value.capabilities;
        }
        if (fqn != null) metadata["fullyQualifiedName"] = fqn;
        if (parameterTypes != null) metadata["parameterTypes"] = parameterTypes;

        var element = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["kind"] = elementKind,
            ["sourceLocation"] = sourceLocation,
            ["contentHash"] = contentHash,
            ["observation"] = observation,
            ["edges"] = DedupeEdges(relationships).ToArray(),
            ["metadata"] = metadata,
            // Language-conformance A2 — stable URI-safe natural key.
            ["naturalKey"] = MakeNaturalKey(filePath, name),
            // A6 — bare identifier (case preserved from source).
            ["bareName"] = BareNameFrom(qualifiedRaw),
            // A5 — language identifier; A7 — source location facet.
            ["language"] = "csharp",
            ["location"] = new
            {
                file = filePath,
                startLine = lineSpan.StartLinePosition.Line + 1,
                endLine = lineSpan.EndLinePosition.Line + 1,
            },
        };
        if (parentName != null) element["parentName"] = parentName;
        if (scalars != null)
        {
            element["scalars"] = new Dictionary<string, object?>
            {
                ["branchCount"] = scalars.BranchCount,
                ["sonarBranchCount"] = scalars.SonarBranchCount,
                ["sonarNestingDepthSum"] = scalars.SonarNestingDepthSum,
                ["maxNestingDepth"] = scalars.MaxNestingDepth,
                ["parameterCount"] = scalars.ParameterCount,
                ["returnStatementCount"] = scalars.ReturnStatementCount,
                ["halsteadOperatorCount"] = scalars.HalsteadOperatorCount,
                ["halsteadOperandCount"] = scalars.HalsteadOperandCount,
                ["halsteadUniqueOperators"] = scalars.HalsteadUniqueOperators,
                ["halsteadUniqueOperands"] = scalars.HalsteadUniqueOperands,
            };
        }

        if (includeComments)
        {
            element["content"] = sourceText;
            var leadingComment = ExtractElementLeadingComment(node);
            if (leadingComment != null) element["leadingComment"] = leadingComment;
        }

        elements.Add(element);
    }

    return (elements.ToArray(), artifactEdges.ToArray(), problems.ToArray());
}

/// <summary>
/// Concatenated leading-trivia text for a declaration node — every
/// comment trivia (line, block, or doc) immediately preceding the
/// node's source span, with blank lines and whitespace preserved
/// verbatim. Mirrors the TS analyzer's `--include-comments` semantics.
/// Returns null when no leading comment is present.
/// </summary>
static string? ExtractElementLeadingComment(SyntaxNode node)
{
    var leading = node.GetLeadingTrivia();
    if (leading.Count == 0) return null;

    // Find the first comment trivia; collect from there to the end so
    // the verbatim block (with intervening whitespace) survives.
    int firstCommentIdx = -1;
    for (int i = 0; i < leading.Count; i++)
    {
        if (IsCommentTrivia(leading[i])) { firstCommentIdx = i; break; }
    }
    if (firstCommentIdx == -1) return null;

    var sb = new StringBuilder();
    for (int i = firstCommentIdx; i < leading.Count; i++)
    {
        sb.Append(leading[i].ToFullString());
    }
    var text = sb.ToString().TrimEnd();
    return text.Length > 0 ? text : null;
}

static bool IsCommentTrivia(SyntaxTrivia trivia) =>
    trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
    || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
    || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
    || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

// --- Naming + canonicalization ----------------------------------------

static string CanonicalizePath(string raw)
{
    var parts = raw.Split('/')
        .Select(Canonicalize)
        .Where(s => !string.IsNullOrEmpty(s));
    return string.Join("/", parts);
}

/// <summary>
/// Build a parameter-type suffix `(Type1,Type2,...)` for callable
/// declarations so overload identity stays stable across sibling
/// reorderings / additions / deletions. Non-callable declarations
/// (types, properties, fields, events) get the empty string.
///
/// Whitespace and slashes inside type names are replaced with stable
/// placeholders so the suffix survives `Canonicalize`'s segment
/// splitter. Generic argument punctuation (`<`, `>`, `,`) collapses to
/// dashes through `Canonicalize` itself, same as Swift's port. Match
/// surface mirrors Swift's `paramSignature` in nodegraph-analyzer-swift
/// (Fathom 2.2.21).
/// </summary>
static string GetParamSignature(SyntaxNode node)
{
    SeparatedSyntaxList<ParameterSyntax>? parameters = node switch
    {
        BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
        LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters,
        IndexerDeclarationSyntax idx => idx.ParameterList.Parameters,
        _ => null
    };
    if (parameters == null) return string.Empty;
    if (parameters.Value.Count == 0) return "()";
    var types = parameters.Value.Select(p =>
        (p.Type?.ToString() ?? "")
            .Replace("/", "-")
            .Replace(" ", ""));
    return "(" + string.Join(",", types) + ")";
}

static string GetQualifiedRawName(SyntaxNode node, string rawName)
{
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
    if (node is ParameterSyntax || node is TypeParameterSyntax)
    {
        var enclosingMember = FindEnclosingMember(node);
        if (enclosingMember != null)
        {
            var memberRaw = GetDeclarationName(enclosingMember);
            if (memberRaw != null)
            {
                var memberQualified = GetQualifiedRawName(enclosingMember, memberRaw);
                var prefix = node is TypeParameterSyntax ? "type-param-" : "";
                return $"{memberQualified}/{prefix}{rawName}";
            }
        }
    }
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
            || current is LocalFunctionStatementSyntax)
        {
            return current;
        }
        current = current.Parent;
    }
    return null;
}

static bool IsTopLevelDeclaration(SyntaxNode node)
{
    var parent = node.Parent;
    return parent is CompilationUnitSyntax
        || parent is NamespaceDeclarationSyntax
        || parent is FileScopedNamespaceDeclarationSyntax;
}

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

// --- signatureHash inputs --------------------------------------------

static string? DeriveFullyQualifiedName(SemanticModel model, SyntaxNode node)
{
    var symbol = model.GetDeclaredSymbol(node);
    if (symbol == null) return null;
    var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    if (fqn.StartsWith("global::"))
        fqn = fqn.Substring("global::".Length);
    return fqn;
}

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

// --- Edge extraction --------------------------------------------------

static object[] ExtractRelationships(
    SyntaxNode node,
    HashSet<string> allNames,
    SemanticModel semanticModel,
    string currentFilePath)
{
    var relationships = new List<object>();
    var seen = new HashSet<string>();

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

    // Variant that emits targetRef when the target's file is resolvable
    // cross-file. Uses CanonicalizePath (slash-preserving) so qualified
    // names like `Runner/Run(ParsedArgs)` canonicalize to `runner/run-parsedargs`
    // before MakeNaturalKey substitutes `/` to `:`. Per B2.
    void AddWithTargetRef(string type, string subtype, string rawName, string? targetFile)
    {
        var canonical = CanonicalizePath(rawName);
        if (string.IsNullOrEmpty(canonical)) return;
        var subtypeKey = $"{subtype}:{canonical}";
        if (seen.Contains(subtypeKey)) return;
        seen.Add(subtypeKey);
        seen.Add($"{type}:{canonical}");
        if (targetFile != null)
        {
            var targetRef = MakeNaturalKey(targetFile, canonical);
            relationships.Add(new { type, subtype, targetName = canonical, targetRef });
        }
        else
        {
            relationships.Add(new { type, subtype, targetName = canonical });
        }
    }

    // Project-level resolver: try to resolve a syntax node to a cross-file
    // declaration's target file via the shared SemanticModel. Returns null
    // for same-file targets, external library symbols, or unresolvable
    // identifiers. Per language-conformance B2.
    string? ResolveTargetFile(SyntaxNode refNode)
    {
        try
        {
            var symbolInfo = semanticModel.GetSymbolInfo(refNode);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol == null) return null;
            // Follow aliases (e.g., `using Foo = SomeNs.SomeType`) to the
            // underlying declaration.
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            var declRefs = symbol.DeclaringSyntaxReferences;
            if (declRefs.Length == 0) return null;
            var declFilePath = declRefs[0].SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(declFilePath)) return null;
            if (declFilePath == currentFilePath) return null;
            return declFilePath;
        }
        catch
        {
            return null;
        }
    }

    // Project-level call resolver: resolves a call site to the target
    // method's parameter signature for A3 disambiguation AND its
    // containing-type qualification (so cross-file calls land on the
    // class-qualified natural key like `runner:run-parsedargs`).
    // Returns null for unresolvable calls (dynamic dispatch, external libs).
    (string FilePath, string QualifiedRawName)? ResolveCallTarget(SyntaxNode callee)
    {
        try
        {
            var symbolInfo = semanticModel.GetSymbolInfo(callee);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is not IMethodSymbol method) return null;
            var declRefs = method.DeclaringSyntaxReferences;
            if (declRefs.Length == 0) return null;
            var declFilePath = declRefs[0].SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(declFilePath)) return null;
            // Build the parameter signature in canonicalized form.
            var paramTypes = method.Parameters.Select(p =>
                p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            var sig = method.Parameters.Length == 0
                ? "()"
                : "(" + string.Join(",", paramTypes) + ")";
            // Qualify with the containing type so the target natural key
            // matches the declaration's class-qualified form (`runner/run-parsedargs`).
            var rawName = method.Name + sig;
            if (method.ContainingType != null && method.MethodKind == MethodKind.Ordinary)
            {
                rawName = $"{method.ContainingType.Name}/{rawName}";
            }
            return (declFilePath, rawName);
        }
        catch
        {
            return null;
        }
    }

    if (node is TypeDeclarationSyntax typeDecl && typeDecl.BaseList != null)
    {
        var isClass = node is ClassDeclarationSyntax;
        var baseTypes = typeDecl.BaseList.Types.ToList();
        for (var i = 0; i < baseTypes.Count; i++)
        {
            var name = baseTypes[i].Type.ToString().Split('<')[0].Split('.').Last();
            // Cross-file resolver: even when the local file doesn't list
            // the base type, the shared Compilation can resolve it.
            var targetFile = ResolveTargetFile(baseTypes[i].Type);
            if (!allNames.Contains(name) && targetFile == null) continue;
            var isInterfaceShaped = name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);
            var isExtends = isClass && i == 0 && !isInterfaceShaped;
            if (isExtends)
            {
                AddWithTargetRef("extends", "class", name, targetFile);
            }
            else
            {
                AddWithTargetRef("implements", "explicit", name, targetFile);
            }
        }
    }

    // Note: `contains` edges from a type to its members are emitted in
    // DecomposeWithRoslyn with qualified targetNames (`greeter/name`,
    // `greeter/greet`, ...). The legacy 0.3.x analyzer additionally
    // emitted bare-name contains here (e.g., targetName: "name") which
    // duplicated the qualified ones; that was a holdover from the
    // pre-qualified-naming days. Removed in 0.4.0.

    if (node is MethodDeclarationSyntax method)
    {
        // Per language-conformance B1: `references` with subtype
        // `return-type` / `parameter-type`. Under project-level
        // resolution (Stage 3b), cross-file targets emit targetRef.
        var returnType = method.ReturnType.ToString().Split('<')[0].Split('.').Last();
        var returnTargetFile = ResolveTargetFile(method.ReturnType);
        if ((allNames.Contains(returnType) || returnTargetFile != null) && returnType != GetDeclarationName(node))
            AddWithTargetRef("references", "return-type", returnType, returnTargetFile);

        foreach (var param in method.ParameterList.Parameters)
        {
            if (param.Type == null) continue;
            var paramType = param.Type.ToString().Split('<')[0].Split('.').Last();
            var paramTargetFile = ResolveTargetFile(param.Type);
            if (allNames.Contains(paramType) || paramTargetFile != null)
                AddWithTargetRef("references", "parameter-type", paramType, paramTargetFile);
        }
    }

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
            if (calledName == null || calledName == GetDeclarationName(node)) continue;

            // Project-level call resolution (Stage 3b): resolve the call's
            // method symbol to its declaration's file + class-qualified
            // param-disambiguated raw name. The natural key matches the
            // declaration's emission so cross-file calls resolve correctly (B2).
            var callTarget = ResolveCallTarget(invocation.Expression);
            if (callTarget != null)
            {
                AddWithTargetRef("calls", "direct", callTarget.Value.QualifiedRawName, callTarget.Value.FilePath);
            }
            else if (allNames.Contains(calledName))
            {
                // Same-file call without resolved signature: emit bare-name
                // form per the existing same-file convention.
                Add("calls", "direct", calledName);
            }
        }
    }

    {
        foreach (var creation in node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString().Split('<')[0].Split('.').Last();
            // B1 calls subtype core (Stage 3a): `constructor` for `new T(...)`.
            if (allNames.Contains(typeName) && typeName != GetDeclarationName(node))
                Add("calls", "constructor", typeName);
        }
    }

    if (node is TypeDeclarationSyntax overrideContainer)
    {
        foreach (var member in overrideContainer.Members.OfType<MethodDeclarationSyntax>())
        {
            if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
            {
                // Override semantics live on the method element's isOverride
                // facet per F2; no edge emitted. The legacy
                // `inherits[overrides]` edge is dropped per Stage 3a.
                _ = member;
            }
        }
    }

    {
        foreach (var assign in node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assign.IsKind(SyntaxKind.AddAssignmentExpression)) continue;
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
                    Add("references", "generic-constraint", constraintName);
            }
        }
    }

    {
        foreach (var attrList in node.DescendantNodes().OfType<AttributeListSyntax>())
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString().Split('<')[0].Split('.').Last();
                if (attrName.EndsWith("Attribute")) attrName = attrName[..^9];
                // Per language-conformance A8 (Stage 2 work):
                // attributes are surfaced via the element's `annotations`
                // facet, not via a `uses[decorates]` edge. No edge emitted
                // here; the full attribute-extraction pass is deferred to
                // Stage 2 implementation.
                _ = attrName;
            }
        }
    }

    if (node is TypeDeclarationSyntax partialCandidate)
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

// --- Utility ----------------------------------------------------------

static string ComputeHash(string content)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content.Trim()));
    return Convert.ToHexStringLower(bytes);
}

/// <summary>
/// Synthesize a URI-safe natural key per the language-conformance A2/A4
/// convention. `/` in either component substitutes to `:` so the result
/// is safe for the substrate's cross-graph URI scheme.
/// </summary>
static string MakeNaturalKey(string artifactId, string name)
{
    var safeArtifact = artifactId.Replace('/', ':');
    var safeName = name.Replace('/', ':');
    return string.IsNullOrEmpty(safeName)
        ? safeArtifact
        : $"{safeArtifact}#{safeName}";
}

/// <summary>
/// Recover the bare identifier from a raw declaration name. Drops the
/// qualifying parent path (`User/Rename(string)` → `Rename(string)`)
/// and any param-signature suffix (`Rename(string)` → `Rename`).
/// Preserves source case for A6.
/// </summary>
static string BareNameFrom(string rawName)
{
    if (string.IsNullOrEmpty(rawName)) return rawName;
    var lastSlash = rawName.LastIndexOf('/');
    var tail = lastSlash < 0 ? rawName : rawName.Substring(lastSlash + 1);
    var parenIdx = tail.IndexOf('(');
    return parenIdx < 0 ? tail : tail.Substring(0, parenIdx);
}

static string Canonicalize(string raw)
{
    if (string.IsNullOrEmpty(raw)) return string.Empty;
    var lower = raw.ToLowerInvariant();
    var sb = new StringBuilder();
    var lastDash = true;
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

static void Emit(object obj)
{
    Console.WriteLine(JsonSerializer.Serialize(obj));
}

/// <summary>
/// Dedupe a per-source edge list to one edge per (type, targetName).
/// First wins. Mirrors `dedupeEdges` in `@kepello/nodegraph-analysis/protocol`
/// — the substrate's `edges_live_unique_*` indexes exclude `subtype` from
/// the key, so two edges differing only in subtype collide at ingest.
/// Edges are anonymous-typed objects; reflection reads `type` + `targetName`.
/// </summary>
static List<object> DedupeEdges(IEnumerable<object>? edges)
{
    if (edges == null) return new List<object>();
    var seen = new HashSet<string>();
    var output = new List<object>();
    foreach (var edge in edges)
    {
        var t = edge.GetType();
        var typeProp = t.GetProperty("type")?.GetValue(edge) as string ?? "";
        var targetName = t.GetProperty("targetName")?.GetValue(edge) as string ?? "";
        var key = $"{typeProp}|{targetName}";
        if (!seen.Add(key)) continue;
        output.Add(edge);
    }
    return output;
}

static AnalyzerArgs ParseArgs(string[] argv)
{
    var result = new AnalyzerArgs();

    for (int i = 0; i < argv.Length; i++)
    {
        if (argv[i] == "--path" && i + 1 < argv.Length)
        {
            result.Path = argv[++i];
        }
        else if (argv[i] == "--discover")
        {
            result.Discover = true;
        }
    }

    return result;
}

// Read the per-analyzer config slice from stdin (UTF-8 JSON,
// terminated by EOF). The orchestrator writes one JSON object per
// analyzer before closing stdin. Empty stdin → fatal error
// (orchestrator invocations always send a payload; absence means
// standalone invocation without piped config).
static AnalyzerConfig ReadAnalyzerConfigFromStdin()
{
    string raw;
    using (var reader = new StreamReader(Console.OpenStandardInput()))
    {
        raw = reader.ReadToEnd().Trim();
    }
    if (string.IsNullOrEmpty(raw))
    {
        throw new InvalidOperationException(
            "analyzer config not provided on stdin. Orchestrator invocations always pipe a JSON object; standalone use must do the same."
        );
    }
    try
    {
        var doc = JsonDocument.Parse(raw);
        var cfg = new AnalyzerConfig();
        if (doc.RootElement.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Array)
            foreach (var v in inc.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String) cfg.Include.Add(v.GetString()!);
        if (doc.RootElement.TryGetProperty("exclude", out var exc) && exc.ValueKind == JsonValueKind.Array)
            foreach (var v in exc.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String) cfg.Exclude.Add(v.GetString()!);
        if (doc.RootElement.TryGetProperty("includeComments", out var ic) && ic.ValueKind == JsonValueKind.True)
            cfg.IncludeComments = true;
        return cfg;
    }
    catch (JsonException ex)
    {
        throw new InvalidOperationException($"Failed to parse analyzer config from stdin: {ex.Message}");
    }
}

static List<string> DiscoverFiles(string rootPath, List<string> include, List<string> exclude)
{
    var results = new List<string>();
    WalkDirectory(rootPath, results, rootPath, include, exclude);
    return results;
}

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
                if (SkipDirsHolder.Set.Contains(name)) continue;
                WalkDirectory(entry, results, rootPath, include, exclude);
            }
            else
            {
                var ext = Path.GetExtension(entry);
                if (FileExtensionsHolder.UniversalSkip.Contains(ext)) continue;
                if (!FileExtensionsHolder.Analyzed.Contains(ext)) continue;
                var relPath = Path.GetRelativePath(rootPath, entry);
                if (MatchesFilter(relPath, include, exclude))
                    results.Add(entry);
            }
        }
    }
    catch { /* skip unreadable dirs */ }
}

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

class AnalyzerArgs
{
    public string Path { get; set; } = "";
    public bool Discover { get; set; }
}

class AnalyzerConfig
{
    public List<string> Include { get; } = new();
    public List<string> Exclude { get; } = new();
    public bool IncludeComments { get; set; }
}

// Universal-skip directory names. Mirrors `UNIVERSAL_SKIP_DIRS` in
// `@kepello/nodegraph-analysis/protocol`. Plus `bin` / `.vs` which the
// .NET tooling produces as build outputs alongside `obj`.
static class SkipDirsHolder
{
    public static readonly HashSet<string> Set = new(StringComparer.Ordinal)
    {
        ".git", "node_modules",
        "dist", ".next", ".cache", ".turbo", ".parcel-cache", ".vite", ".output",
        ".build", ".swiftpm",
        "obj", "bin", ".vs",
        "__pycache__", ".venv",
        ".fathom",
    };
}

static class FileExtensionsHolder
{
    public static readonly HashSet<string> Analyzed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".sln",
    };

    // Universally-skipped file extensions (build artifacts that leak
    // into walkable directories). Mirrors `UNIVERSAL_SKIP_EXTENSIONS`
    // in `@kepello/nodegraph-analysis@0.18.2+` so the .NET analyzer's
    // own walk drops them at the source.
    public static readonly HashSet<string> UniversalSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".pdb",
    };
}
