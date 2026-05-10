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

var loadedConfig = LoadAnalyzerConfig(cliArgs.Path);

if (cliArgs.Discover)
{
    foreach (var filePath in DiscoverCsFiles(cliArgs.Path, loadedConfig.Include, loadedConfig.Exclude))
    {
        Console.Out.WriteLine(filePath);
    }
    return;
}

RunOutput(cliArgs, loadedConfig);

static void RunOutput(AnalyzerArgs args, AnalyzerConfig config)
{
    Console.Error.WriteLine($".NET analyzer (output): scanning {args.Path}");

    var csFiles = DiscoverCsFiles(args.Path, config.Include, config.Exclude);
    Console.Error.WriteLine($".NET analyzer: found {csFiles.Count} .cs files");

    var startTime = DateTime.UtcNow;
    var elementsEmitted = 0;

    // File-level parallelism: Roslyn syntax trees are immutable and the
    // emit pipeline has no shared state, so each file analyzes on its
    // own thread. Console.WriteLine is atomic per call in .NET, so
    // Emit() from multiple threads produces interleaved but
    // individually-complete NDJSON lines.
    Parallel.ForEach(csFiles, filePath =>
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var artifact = BuildArtifact(content, filePath, config.IncludeComments);
            if (artifact == null) return;
            Emit(new { type = "artifact", artifact });
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

// --- Artifact assembly ----------------------------------------------------

static object? BuildArtifact(string content, string filePath, bool includeComments)
{
    var (elements, artifactEdges, problems) = DecomposeWithRoslyn(content, filePath, includeComments);

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
    string content, string filePath, bool includeComments)
{
    var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
    var root = tree.GetRoot();

    // Minimal compilation for SemanticModel access (FQN + parameter
    // types). Single-file with core references is enough — unresolved
    // external types still produce usable display strings.
    var compilation = CSharpCompilation.Create("FathomAnalysis",
        syntaxTrees: [tree],
        references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    var semanticModel = compilation.GetSemanticModel(tree);
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
    var seenCanonical = new Dictionary<string, string>();
    var qualifiedNameCounts = new Dictionary<string, int>();

    foreach (var node in root.DescendantNodes())
    {
        var rawName = GetDeclarationName(node);
        if (rawName == null) continue;

        var elementKind = GetElementType(node);
        if (elementKind == null) continue;

        var qualifiedRaw = GetQualifiedRawName(node, rawName);
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
        var relationships = ExtractRelationships(node, allNames);

        // Type declarations emit explicit contains edges to their direct
        // members. Core treats these as authoritative for containment.
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
                var rels = relationships.ToList();
                foreach (var (edgeType, target) in IntraClassHelpers.ExtractEdges(node, memberIndex))
                {
                    var canonicalTarget = Canonicalize(target);
                    if (string.IsNullOrEmpty(canonicalTarget)) continue;
                    rels.Add(new { type = edgeType, targetName = canonicalTarget });
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

static object[] ExtractRelationships(SyntaxNode node, HashSet<string> allNames)
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

    if (node is TypeDeclarationSyntax typeDecl && typeDecl.BaseList != null)
    {
        var isClass = node is ClassDeclarationSyntax;
        var baseTypes = typeDecl.BaseList.Types.ToList();
        for (var i = 0; i < baseTypes.Count; i++)
        {
            var name = baseTypes[i].Type.ToString().Split('<')[0].Split('.').Last();
            if (!allNames.Contains(name)) continue;
            var isInterfaceShaped = name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);
            var subtype = (isClass && i == 0 && !isInterfaceShaped) ? "extends" : "implements";
            Add("inherits", subtype, name);
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

    {
        foreach (var creation in node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString().Split('<')[0].Split('.').Last();
            if (allNames.Contains(typeName) && typeName != GetDeclarationName(node))
                Add("calls", "instantiates", typeName);
        }
    }

    if (node is TypeDeclarationSyntax overrideContainer)
    {
        foreach (var member in overrideContainer.Members.OfType<MethodDeclarationSyntax>())
        {
            if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
            {
                Add("inherits", "overrides", member.Identifier.Text);
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
                    Add("uses", "genericConstraint", constraintName);
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
                if (allNames.Contains(attrName))
                    Add("uses", "decorates", attrName);
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

static AnalyzerConfig LoadAnalyzerConfig(string repoRoot)
{
    var configPath = Path.Combine(repoRoot, "nodegraph-analyzer-dotnet.config.json");
    if (!File.Exists(configPath)) return new AnalyzerConfig();

    string raw;
    try
    {
        raw = File.ReadAllText(configPath);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"Failed to read {configPath}: {ex.Message}");
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
        throw new InvalidOperationException($"Failed to parse {configPath}: {ex.Message}");
    }
}

static List<string> DiscoverCsFiles(string rootPath, List<string> include, List<string> exclude)
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
