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

// MSBuildLocator.RegisterDefaults MUST fire before any MSBuild type is
// JIT-touched. Failures (no .NET SDK on host) degrade gracefully:
// per-csproj project loading is skipped, and the analyzer falls back to
// the System-runtime-only sharedCompilation path that pre-Phase-2 code
// shipped. Per Fathom row dotnet-csproj-sln-handling (1.11.15) Phase 2.
var msbuildAvailable = TryRegisterMsbuild();

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

RunOutput(cliArgs, loadedConfig, msbuildAvailable);

static bool TryRegisterMsbuild()
{
    try
    {
        Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($".NET analyzer: MSBuildLocator.RegisterDefaults failed ({ex.GetType().Name}: {ex.Message}); falling back to sharedCompilation path.");
        return false;
    }
}

static void RunOutput(AnalyzerArgs args, AnalyzerConfig config, bool msbuildAvailable)
{
    Console.Error.WriteLine($".NET analyzer (output): scanning {args.Path}");

    var allFiles = DiscoverFiles(args.Path, config.Include, config.Exclude);
    var csFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
    var csprojFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
    var slnFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".sln", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.Error.WriteLine($".NET analyzer: found {csFiles.Count} .cs, {csprojFiles.Count} .csproj, {slnFiles.Count} .sln");

    // Element natural keys are keyed by the directory-walked (on-disk-case)
    // file path; but Roslyn's `SyntaxTree.FilePath` for a project document is
    // the case AS WRITTEN in the .csproj `<Compile Include>`, which can differ
    // (classic WinForms `Foo.designer.cs` in the csproj vs `Foo.Designer.cs`
    // on disk — case-insensitive FS hides it). A cross-file targetRef built
    // from the Roslyn path then can't string-match the element key → dangles
    // (Fathom row dotnet-l0-partial-class-dispose-binding 5.0.68.1.1). Map any
    // resolved declaration path back to the discovered on-disk case so both
    // sides of the edge agree (rationale + logic in NamingHelpers).
    var canonByLower = NamingHelpers.BuildCanonicalPathMap(csFiles);
    string CanonicalizeFilePath(string p) => NamingHelpers.CanonicalizeFilePathCase(p, canonByLower);

    var startTime = DateTime.UtcNow;
    var elementsEmitted = 0;

    // Phase 2 (row 1.11.15): per-project MSBuild compilation map.
    // For each .cs that maps to a loaded project, the analyzer uses the
    // project's Compilation — which has NuGet PackageReferences and
    // cross-project ProjectReferences resolved by the MSBuildWorkspace.
    // The sharedCompilation below remains the fallback for orphan files
    // (.cs outside any csproj directory, hosts without .NET SDK, or
    // .csproj that failed to load).
    Dictionary<string, (Compilation Compilation, SyntaxTree SyntaxTree)> projectMap =
        new(StringComparer.Ordinal);
    var projectProblems = new List<object>();
    if (msbuildAvailable && csprojFiles.Count > 0)
    {
        projectMap = MSBuildIntegration.LoadProjects(csprojFiles, projectProblems);
        Console.Error.WriteLine($".NET analyzer: MSBuildWorkspace loaded {projectMap.Count} document(s) from {csprojFiles.Count} project(s); {projectProblems.Count} problem(s).");
    }
    foreach (var p in projectProblems)
    {
        Emit(new { type = "problem", problem = p });
    }

    // Per language-conformance row 4.1.2 Stage 3b (2026-05-16) — project-
    // level analysis pivot. Build ONE Compilation containing every .cs
    // file in the source dir so the SemanticModel resolves cross-file
    // references natively. Cross-file edges (calls, references, etc.)
    // emit `targetRef` with the resolved target's file natural key.
    //
    // This sharedCompilation is the FALLBACK for files not under any
    // loaded csproj — orphan .cs in the source tree, hosts without
    // MSBuild, or projects that failed to open. Cross-package /
    // cross-project resolution there only fires via the project map.
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

    // Per-file iteration. Project-map hit uses the MSBuildWorkspace
    // compilation; orphan files use the sharedCompilation fallback.
    // Parallel ordering preserved; Console.WriteLine is atomic per call in .NET.
    Parallel.ForEach(syntaxTrees, tree =>
    {
        var filePath = tree.FilePath;
        try
        {
            if (!fileContents.TryGetValue(filePath, out var content)) return;
            Compilation compilationForFile;
            SyntaxTree treeForFile;
            if (projectMap.TryGetValue(filePath, out var projectEntry))
            {
                compilationForFile = projectEntry.Compilation;
                treeForFile = projectEntry.SyntaxTree;
            }
            else
            {
                compilationForFile = sharedCompilation;
                treeForFile = tree;
            }
            var artifact = BuildArtifact(content, filePath, config.IncludeComments, compilationForFile, treeForFile, CanonicalizeFilePath);
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
    Compilation sharedCompilation,
    SyntaxTree sharedTree,
    Func<string, string> canonicalizeFilePath)
{
    var (elements, artifactEdges, problems, limitations) = DecomposeWithRoslyn(
        content, filePath, includeComments, sharedCompilation, sharedTree, canonicalizeFilePath);

    var artifact = new Dictionary<string, object?>
    {
        ["id"] = filePath,
        ["filePath"] = filePath,
        ["language"] = "csharp",
        ["sourceHash"] = ComputeHash(content),
        ["elements"] = elements,
    };
    if (artifactEdges.Length > 0) artifact["edges"] = DedupeEdges(artifactEdges).ToArray();
    if (problems.Length > 0) artifact["problems"] = problems;
    if (limitations.Length > 0) artifact["limitations"] = limitations;

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

static (object[] elements, object[] artifactEdges, object[] problems, object[] limitations) DecomposeWithRoslyn(
    string content, string filePath, bool includeComments,
    Compilation sharedCompilation, SyntaxTree tree,
    Func<string, string> canonicalizeFilePath)
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
    // Group J — limitations accumulator (filled by E3 heuristics + future
    // fallback-emission sites per P2 fallback-emits-limitation rule).
    var limitationsAccumulator = new List<object>();

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
    // Pass 1 — assign every declarable node its FINAL canonical natural key
    // (collision-resolved). Both the element emission AND the contains-edge pass
    // below read this one authoritative map instead of recomputing the canonical
    // from the raw member name. The recompute (pre-5.0.68.3) missed the
    // disambiguation suffixes — the `$count` overload-signature counter AND the
    // case-collision `-casedup` suffix — so contains edges mis-targeted the bare
    // name (wrong sibling on case collisions; the first overload on sig
    // collisions), which then mis-feeds L3 cluster membership. `DescendantNodes()`
    // is pre-order (a type precedes its members), so the contains pass can't name
    // a member during the type's own emission without this precomputed map.
    var canonicalByNode = new Dictionary<SyntaxNode, (string Name, string QualifiedRaw)>();
    {
        var seenCanonical = new Dictionary<string, string>();
        var qualifiedNameCounts = new Dictionary<string, int>();
        foreach (var node in root.DescendantNodes())
        {
            var rawName = GetDeclarationName(node);
            if (rawName == null) continue;
            if (GetElementType(node) == null) continue;

            var qualifiedRaw = GetQualifiedRawName(node, rawName) + NamingHelpers.GetParamSignature(node);
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
                // The raw-name collision counter above is case-SENSITIVE, so a
                // true signature duplicate never reaches here. A collision on the
                // CANONICAL name therefore means two declarations differ only by
                // case (or punctuation) — e.g. C#'s case-sensitive siblings
                // `isAuto` vs `IsAuto` both lowercasing to `isauto`. Pre-fix this
                // emitted a hard error and DROPPED the second declaration (lossy,
                // Fathom 5.0.68.3). Instead: disambiguate the element key so BOTH
                // elements are emitted, and record a structured limitation. Edge
                // resolution is case-insensitive by design (canonical keys are
                // lowercased), so edges to the bare name resolve to the first
                // declaration — a documented residual, not a dropped element.
                var n = 1;
                string disambiguated;
                do { disambiguated = $"{name}-casedup{n}"; n++; } while (seenCanonical.ContainsKey(disambiguated));
                var collisionSpan = node.GetLocation().GetLineSpan();
                limitationsAccumulator.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "csharp-canonical-name-collision",
                    ["severity"] = "minor",
                    ["location"] = new Dictionary<string, object?>
                    {
                        ["file"] = filePath,
                        ["startLine"] = collisionSpan.StartLinePosition.Line + 1,
                        ["endLine"] = collisionSpan.EndLinePosition.Line + 1,
                    },
                    ["description"] = $"Declaration \"{qualifiedRaw}\" canonicalizes to \"{name}\", colliding with "
                        + $"\"{existingRaw}\" (case-only-distinct C# siblings). Emitted as \"{disambiguated}\" so the "
                        + "element is not dropped; edges to the case-insensitive canonical name resolve to the first declaration.",
                    ["metadata"] = new Dictionary<string, object?>
                    {
                        ["canonicalName"] = name,
                        ["existingRaw"] = existingRaw,
                        ["collidingRaw"] = qualifiedRaw,
                    },
                });
                name = disambiguated;
            }
            seenCanonical[name] = qualifiedRaw;
            canonicalByNode[node] = (name, qualifiedRaw);
        }
    }

    // Pass 2 — emit elements, reading the canonical map (one source of truth).
    foreach (var node in root.DescendantNodes())
    {
        if (!canonicalByNode.TryGetValue(node, out var canon)) continue;
        var name = canon.Name;
        var qualifiedRaw = canon.QualifiedRaw;
        var rawName = GetDeclarationName(node)!;
        var elementKind = GetElementType(node)!;

        // Parent name: the canonical path with the last segment stripped.
        // Top-level declarations have no parent — the artifact contains
        // them directly via artifactEdges.
        string? parentName = null;
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash > 0) parentName = name.Substring(0, lastSlash);

        var sourceText = node.ToFullString().Trim();
        var contentHash = ComputeHash(sourceText);
        var relationships = ExtractRelationships(node, allNames, semanticModel, filePath, limitationsAccumulator, canonicalizeFilePath);

        // Type declarations emit explicit contains edges to their direct
        // members. Core treats these as authoritative for containment. The
        // target is the member's FINAL canonical from pass 1's map — so the
        // contains edge matches exactly the key the member element is emitted
        // under, including any `$count` / `-casedup` disambiguation (Fathom
        // 5.0.68.3). Members with no canonical (no element emitted) get no edge.
        if (node is TypeDeclarationSyntax typeDecl)
        {
            var containsEdges = new List<object>(relationships);
            foreach (var member in typeDecl.Members)
            {
                if (canonicalByNode.TryGetValue(member, out var memberCanon))
                {
                    containsEdges.Add(new { type = "contains", targetName = memberCanon.Name });
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

        // F6 return-shape facets (Fathom 3.1.1.1 Stage 2). Populated in the
        // body-bearing block below (reuses the intra-class member-field
        // index for returnsField); null for non-body-bearing declarations.
        string? returnKindFacet = null;
        bool? returnsFieldFacet = null;

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
                // F6 return-shape (reuses the member-field index for the
                // returnsField `return _x;` / `return this.X;` detection).
                (returnKindFacet, returnsFieldFacet) =
                    ReturnShapeHelpers.Extract(node, semanticModel, memberIndex.Fields);
                // Qualify with the FULL nested-type path (`MyDataSet/MyDataTable`),
                // not just the immediate class name, so intra-class targets bind
                // to the nested member's element key (`mydataset:mydatatable:...`).
                // Generated typed-DataSet code (nested DataTable classes) is the
                // common case (Fathom row dotnet-l0-property-accessor-edges /
                // nested-class intra-class qualification). Per-segment Canonicalize
                // joined by `/` (the resolver treats `/` as the path separator).
                var classQualifiedRaw = GetQualifiedRawName(containingType, containingType.Identifier.Text);
                var canonicalClass = string.Join("/", classQualifiedRaw.Split('/').Select(Canonicalize));
                var rels = relationships.ToList();
                var intraResult = IntraClassHelpers.ExtractEdges(node, memberIndex);
                foreach (var (edgeType, subtype, target) in intraResult.Edges)
                {
                    var canonicalTarget = Canonicalize(target);
                    if (string.IsNullOrEmpty(canonicalTarget)) continue;
                    // Per language-conformance B1: qualify intra-class
                    // targets with the class name (`class/member`) so the
                    // emitted targetName resolves to the actual element's
                    // natural key. `callsMethod` targets arrive
                    // signature-suffixed (`method(int)`) so they match the
                    // method element's natural key (Fathom row 5.0.68.1);
                    // `accessesField` targets are bare field names. Without
                    // this, intra-class edges stayed dangling. Mirrors the TS
                    // analyzer's intra-class edge composition (Fathom 3.2.1).
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

                // Same-arity overload ambiguity → structured limitation, not a
                // guessed edge (Trade-off dotnet-callsmethod-overload-ambiguity
                // 2.2.17). Syntactic resolution can't pick among overloads that
                // share the called arity without type inference.
                foreach (var amb in intraResult.AmbiguousCalls)
                {
                    var ambSpan = node.GetLocation().GetLineSpan();
                    limitationsAccumulator.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "csharp-ambiguous-overload",
                        ["severity"] = "minor",
                        ["location"] = new Dictionary<string, object?>
                        {
                            ["file"] = filePath,
                            ["startLine"] = ambSpan.StartLinePosition.Line + 1,
                            ["endLine"] = ambSpan.EndLinePosition.Line + 1,
                        },
                        ["description"] = $"Same-class call to overloaded method '{amb.MethodName}' "
                            + $"could not be resolved to a specific overload by argument count "
                            + $"({amb.OverloadCount} overloads share the called arity); callsMethod edge omitted.",
                        ["metadata"] = new Dictionary<string, object?>
                        {
                            ["methodName"] = amb.MethodName,
                            ["overloadCount"] = amb.OverloadCount,
                            ["arities"] = amb.Arities,
                        },
                    });
                }
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
            ["sourceHash"] = contentHash,
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

        // Group C body-shape facets — hasBody, isAbstract, isInterface.
        // hasBody: callable kinds report whether they have a method body.
        // isAbstract: classes / methods carrying the `abstract` modifier.
        // isInterface: true for interface declarations; false for other
        // type kinds (class / struct / enum / record).
        if (node is MethodDeclarationSyntax m)
        {
            element["hasBody"] = m.Body != null || m.ExpressionBody != null;
            if (m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AbstractKeyword)))
            {
                element["isAbstract"] = true;
            }
            else if (m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AbstractKeyword) || mod.IsKind(SyntaxKind.ExternKeyword)))
            {
                element["isAbstract"] = true;
            }
            else
            {
                element["isAbstract"] = false;
            }
        }
        else if (node is ConstructorDeclarationSyntax ctor)
        {
            element["hasBody"] = ctor.Body != null || ctor.ExpressionBody != null;
        }
        else if (node is LocalFunctionStatementSyntax lf)
        {
            element["hasBody"] = lf.Body != null || lf.ExpressionBody != null;
        }
        else if (node is AccessorDeclarationSyntax acc)
        {
            element["hasBody"] = acc.Body != null || acc.ExpressionBody != null;
        }
        else if (node is OperatorDeclarationSyntax op)
        {
            element["hasBody"] = op.Body != null || op.ExpressionBody != null;
        }
        else if (node is ConversionOperatorDeclarationSyntax cop)
        {
            element["hasBody"] = cop.Body != null || cop.ExpressionBody != null;
        }
        else if (node is PropertyDeclarationSyntax || node is FieldDeclarationSyntax || node is EventDeclarationSyntax || node is ParameterSyntax)
        {
            element["hasBody"] = false;
        }

        if (node is InterfaceDeclarationSyntax)
        {
            element["isInterface"] = true;
        }
        else if (node is ClassDeclarationSyntax cls)
        {
            element["isInterface"] = false;
            if (cls.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AbstractKeyword)))
            {
                element["isAbstract"] = true;
            }
        }
        else if (node is StructDeclarationSyntax || node is RecordDeclarationSyntax || node is EnumDeclarationSyntax)
        {
            element["isInterface"] = false;
        }

        // Language-conformance Group D — D1 accessibility, D3 folder, D4 qualifiedName.
        // D2 module facet deferred (namespace+assembly composition; no
        // immediate consumer surfaced the need).
        var flavorsForAccessibility = declarable?.flavors as IDictionary<string, object?>
            ?? new Dictionary<string, object?>();
        var accessibility = DeriveAccessibility(node, new Dictionary<string, object?>(flavorsForAccessibility));
        if (accessibility != null)
        {
            element["accessibility"] = accessibility;
        }
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder))
        {
            element["folder"] = folder;
        }
        if (fqn != null)
        {
            element["qualifiedName"] = fqn;
        }

        // Language-conformance Group F — type-system facets.
        // F2 dispatchKind on dispatchable callables; F3 callableRole on
        // every callable; F5 isAsync on methods carrying the async
        // modifier. F1 type-position references already emitted as
        // `references` edges (wired in 4.1.2).
        if (node is MethodDeclarationSyntax methodForF)
        {
            // F2 dispatchKind for methods.
            string dispatch;
            if (methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AbstractKeyword)))
                dispatch = "abstract";
            else if (methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)))
                dispatch = "static";
            else if (methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword))
                && methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.SealedKeyword)))
                dispatch = "final";
            else if (methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword)))
                dispatch = "override";
            else if (methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.VirtualKeyword)))
                dispatch = "virtual";
            else if (methodForF.Parent is InterfaceDeclarationSyntax)
                dispatch = "virtual";
            else
                // C# instance methods without virtual/override are
                // statically dispatched (no override possible without
                // virtual / abstract base).
                dispatch = "static";
            element["dispatchKind"] = dispatch;

            // F3 callableRole — none for plain methods. (Constructor
            // role lives on ConstructorDeclarationSyntax branch below.)
            element["callableRole"] = "none";

            // F5 isAsync — `async` modifier.
            if (methodForF.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)))
            {
                element["isAsync"] = true;
            }
            else
            {
                element["isAsync"] = false;
            }
        }
        else if (node is ConstructorDeclarationSyntax)
        {
            element["dispatchKind"] = "static";
            element["callableRole"] = "constructor";
        }

        // F6 — return-shape facets (returnKind via SemanticModel + syntactic
        // fallback; returnsField via own-field return detection). Body-bearing
        // callables only; the L1 methodStereotype rules consume these at S4.
        if (returnKindFacet != null) element["returnKind"] = returnKindFacet;
        if (returnsFieldFacet != null) element["returnsField"] = returnsFieldFacet;

        // Language-conformance Group G — canonical documentation.
        // G1 `documentation` carries parsed XML-doc tag content; G2
        // `documentationCoverage` is true iff G1 was emitted.
        var canonicalDoc = ExtractCanonicalDocumentation(node);
        if (canonicalDoc != null)
        {
            element["documentation"] = canonicalDoc;
            element["documentationCoverage"] = true;
        }
        else
        {
            element["documentationCoverage"] = false;
        }

        // Language-conformance Group E — entry-point detection.
        // Subset of E1 enum at v1 per operator decision: main / http-handler /
        // library-export / other / none. Aggressive E3 heuristic seed.
        var entryPointResult = DetectEntryPoint(node, accessibility, filePath);
        element["entryPoint"] = entryPointResult.Kind;
        if (entryPointResult.Trigger != null)
        {
            element["entryPointTrigger"] = entryPointResult.Trigger;
        }
        if (entryPointResult.HeuristicNote != null)
        {
            element["entryPointHeuristicNote"] = entryPointResult.HeuristicNote;
        }
        if (entryPointResult.Limitation != null)
        {
            limitationsAccumulator.Add(entryPointResult.Limitation);
        }

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

    return (elements.ToArray(), artifactEdges.ToArray(), problems.ToArray(), limitationsAccumulator.ToArray());
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

/// <summary>
/// G1 canonical documentation extraction. Returns the parsed
/// triple-slash XML-doc content in the unified shape, or null when
/// the element has no doc comment.
///
/// Source convention: C# `///` lines forming a contiguous block
/// immediately before the declaration. Each line is parsed as a
/// fragment of an XML document. We collect the canonical tags:
///   <summary>        → summary (required when present)
///   <param name="X"> → params[X]
///   <returns>        → returns
///   <exception cref="X"> → throws[]
///   <example>        → examples[]
/// Unknown tags are dropped (consumers care about the canonical shape).
/// </summary>
static Dictionary<string, object?>? ExtractCanonicalDocumentation(SyntaxNode node)
{
    var leading = node.GetLeadingTrivia();
    if (leading.Count == 0) return null;

    // Concatenate all `///` line content into one XML fragment string,
    // stripping the leading slash-slash-slash + one optional space per
    // line. Multi-line doc trivia get the same treatment via their
    // text shape.
    var sb = new StringBuilder();
    bool any = false;
    foreach (var trivia in leading)
    {
        if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
        {
            foreach (var line in trivia.ToFullString().Split('\n'))
            {
                // Strip leading whitespace + `///` (or `/**`/`*/` for
                // multi-line) + one optional space.
                var stripped = System.Text.RegularExpressions.Regex.Replace(
                    line, @"^\s*(///|/\*\*?|\*+/|\*)\s?", "");
                sb.Append(stripped);
                sb.Append('\n');
                any = true;
            }
        }
    }
    if (!any) return null;

    var xml = sb.ToString().Trim();
    if (xml.Length == 0) return null;

    return ParseXmlDocFragment(xml);
}

/// <summary>
/// Parse a stripped XML-doc fragment (the concatenated `///` content
/// with line-prefix removed) into the G1 canonical shape. Exported
/// for direct testability without trivia-extraction.
/// </summary>
static Dictionary<string, object?>? ParseXmlDocFragment(string xml)
{
    // Wrap in a root element so XDocument can parse fragmented siblings.
    string wrapped = "<__root__>" + xml + "</__root__>";
    System.Xml.Linq.XDocument doc;
    try
    {
        doc = System.Xml.Linq.XDocument.Parse(wrapped, System.Xml.Linq.LoadOptions.PreserveWhitespace);
    }
    catch
    {
        // Malformed XML — fall back to summary-only with the raw text.
        return new Dictionary<string, object?> { ["summary"] = xml.Trim() };
    }
    var root = doc.Root!;

    string? summary = null;
    var summaryEl = root.Element("summary");
    if (summaryEl != null) summary = NormalizeXmlInnerText(summaryEl.Value);

    var paramsDict = new Dictionary<string, object?>();
    foreach (var p in root.Elements("param"))
    {
        var name = (string?)p.Attribute("name");
        if (!string.IsNullOrEmpty(name))
        {
            paramsDict[name!] = NormalizeXmlInnerText(p.Value);
        }
    }

    string? returns = null;
    var returnsEl = root.Element("returns");
    if (returnsEl != null) returns = NormalizeXmlInnerText(returnsEl.Value);

    var throwsList = new List<object?>();
    foreach (var ex in root.Elements("exception"))
    {
        var cref = (string?)ex.Attribute("cref") ?? "";
        // `cref="T:Namespace.Type"` → bare-name "Type"
        var typeBare = cref;
        var colonIdx = typeBare.IndexOf(':');
        if (colonIdx >= 0) typeBare = typeBare.Substring(colonIdx + 1);
        var dotIdx = typeBare.LastIndexOf('.');
        if (dotIdx >= 0) typeBare = typeBare.Substring(dotIdx + 1);
        throwsList.Add(new Dictionary<string, object?>
        {
            ["type"] = typeBare,
            ["description"] = NormalizeXmlInnerText(ex.Value),
        });
    }

    var examplesList = new List<object?>();
    foreach (var ex in root.Elements("example"))
    {
        var t = NormalizeXmlInnerText(ex.Value);
        if (t.Length > 0) examplesList.Add(t);
    }

    if (summary == null && paramsDict.Count == 0 && returns == null
        && throwsList.Count == 0 && examplesList.Count == 0)
    {
        return null;
    }

    var result = new Dictionary<string, object?>();
    result["summary"] = summary ?? "";
    if (paramsDict.Count > 0) result["params"] = paramsDict;
    if (returns != null) result["returns"] = returns;
    if (throwsList.Count > 0) result["throws"] = throwsList;
    if (examplesList.Count > 0) result["examples"] = examplesList;
    return result;
}

static string NormalizeXmlInnerText(string raw)
{
    // Collapse runs of whitespace + trim. XML-doc tags often span
    // multiple lines and consumers want a flat readable description.
    var collapsed = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ");
    return collapsed.Trim();
}

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
// GetParamSignature moved to NamingHelpers (Program.Naming.cs) so the
// intra-class callsMethod resolver builds byte-identical signatures
// (Fathom 5.0.68.1). Call sites use NamingHelpers.GetParamSignature.

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
    string currentFilePath,
    List<object> limitations,
    Func<string, string> canonicalizeFilePath)
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
            var declFilePath = canonicalizeFilePath(declRefs[0].SyntaxTree.FilePath);
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
            var declFilePath = canonicalizeFilePath(declRefs[0].SyntaxTree.FilePath);
            if (string.IsNullOrEmpty(declFilePath)) return null;
            // Build the qualified raw name from the resolved declaration's
            // SYNTAX using the exact functions that build the element natural
            // key (Program.cs line ~341: `GetQualifiedRawName + GetParamSignature`).
            // Deriving from syntax — not from the semantic symbol's
            // `ContainingType.Name` + `ToDisplayString` signature — guarantees
            // the emitted targetRef is byte-identical to the callee's element
            // key after CanonicalizePath, including nested-class qualification
            // (`Outer/Inner/M`) and the syntactic param-type spelling. The
            // semantic form diverged on both, leaving resolved-but-unbindable
            // `calls` edges (Fathom row dotnet-l0-internal-call-resolution
            // 5.0.68.1). Returns null when the declaration isn't a named
            // member shape (kept conservative — no guessed target).
            var declNode = declRefs[0].GetSyntax();
            var declName = GetDeclarationName(declNode);
            if (declName == null) return null;
            var qualifiedRaw = GetQualifiedRawName(declNode, declName) + NamingHelpers.GetParamSignature(declNode);
            return (declFilePath, qualifiedRaw);
        }
        catch
        {
            return null;
        }
    }

    // Resolve a type reference (e.g. the `T` in `new T(...)`) to its
    // declaring file + the type's qualified raw name, built from the
    // declaration SYNTAX via the same key functions as element emission so
    // the targetRef binds to the type's element key (nested types included).
    // Returns null for external/BCL types (no DeclaringSyntaxReferences) — the
    // caller then emits no edge, which also drops the false-positive
    // constructor edges the old name-only `allNames` match produced when a
    // BCL type name (e.g. `ResourceManager`) collided with a same-named local
    // member (Fathom row dotnet-l0-internal-call-resolution 5.0.68.1).
    (string FilePath, string QualifiedRawName)? ResolveTypeTarget(SyntaxNode typeNode)
    {
        try
        {
            var symbolInfo = semanticModel.GetSymbolInfo(typeNode);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            if (symbol is not INamedTypeSymbol named) return null;
            var declRefs = named.DeclaringSyntaxReferences;
            if (declRefs.Length == 0) return null;
            var declFilePath = canonicalizeFilePath(declRefs[0].SyntaxTree.FilePath);
            if (string.IsNullOrEmpty(declFilePath)) return null;
            var declNode = declRefs[0].GetSyntax();
            var declName = GetDeclarationName(declNode);
            if (declName == null) return null;
            // Types carry no parameter signature — the qualified raw name is
            // the canonical element key for the class / struct / interface.
            var qualifiedRaw = GetQualifiedRawName(declNode, declName);
            return (declFilePath, qualifiedRaw);
        }
        catch
        {
            return null;
        }
    }

    // Resolve a property / indexer access (`obj.Prop`, `obj[i]`) to the
    // accessor element it invokes — get-accessor for a read, set-accessor for
    // a write (LHS of assignment). C# property access IS a method call on the
    // accessor (Fathom row dotnet-l0-property-accessor-edges, Gate 5 / 5.0.68.4;
    // mirrors the TS analyzer's 5.0.66/5.0.67). Targets the accessor element
    // `<class>/<prop>/get|set` when the property declares an explicit/auto
    // accessor block (an emitted element), else the property/indexer element
    // itself (expression-bodied `=> ...` properties have no accessor element).
    // Plain fields and external/BCL properties (no declaration) → null (no edge).
    (string FilePath, string QualifiedRawName)? ResolveAccessorTarget(SyntaxNode memberAccess, bool isWrite)
    {
        try
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is not IPropertySymbol prop) return null; // properties + indexers only (not fields)
            var declRefs = prop.DeclaringSyntaxReferences;
            if (declRefs.Length == 0) return null; // external / BCL → no edge
            var declFilePath = canonicalizeFilePath(declRefs[0].SyntaxTree.FilePath);
            if (string.IsNullOrEmpty(declFilePath)) return null;
            var declNode = declRefs[0].GetSyntax();
            var declName = GetDeclarationName(declNode);
            if (declName == null) return null;
            var baseQualified = GetQualifiedRawName(declNode, declName);
            // The get/set accessor is a separate emitted element only when the
            // declaration has an explicit accessor block (`{ get; set; }` /
            // `{ get { } }`). Expression-bodied (`=> expr`) has no accessor
            // element → target the property/indexer element directly so the
            // edge still binds.
            var wantKind = isWrite ? SyntaxKind.SetAccessorDeclaration : SyntaxKind.GetAccessorDeclaration;
            var accessors = (declNode as BasePropertyDeclarationSyntax)?.AccessorList?.Accessors;
            var hasAccessorElement = accessors?.Any(a => a.IsKind(wantKind)) ?? false;
            // The accessor element's natural key does NOT carry the property /
            // indexer parameter signature — its `GetQualifiedRawName` recurses
            // through the property name without re-adding the indexer's params
            // (`DateRanges/indexer/get` → `dateranges:indexer:get`). So target
            // the accessor WITHOUT the sig. Only the bare property / indexer
            // element (expression-bodied, no accessor block) carries the sig
            // (`dateranges:indexer-daterangetypes`). Indexers with explicit
            // accessor blocks dangled pre-fix (EnvisionWeb, Fathom 5.0.68.4).
            var target = hasAccessorElement
                ? $"{baseQualified}/{(isWrite ? "set" : "get")}"
                : baseQualified + NamingHelpers.GetParamSignature(declNode);
            return (declFilePath, target);
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
                // Semantic resolution failed for an in-file call. The bare-name
                // fallback that used to fire here emitted an unqualified,
                // unsignatured target that could never bind to the callee's
                // element key — a phantom edge that obscured the resolution
                // failure (Fathom row dotnet-l0-internal-call-resolution
                // 5.0.68.1). Instead record a structured limitation; never
                // emit an unbindable edge. Common causes: event / delegate
                // `Invoke` (not a method declaration) and degraded compilations
                // (unresolved references on unrestored legacy csprojs). Same-
                // class method calls are still captured as `callsMethod`
                // cohesion edges, which resolve via the signatured target.
                var callSpan = invocation.GetLocation().GetLineSpan();
                limitations.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "csharp-unresolved-call",
                    ["severity"] = "minor",
                    ["location"] = new Dictionary<string, object?>
                    {
                        ["file"] = currentFilePath,
                        ["startLine"] = callSpan.StartLinePosition.Line + 1,
                        ["endLine"] = callSpan.EndLinePosition.Line + 1,
                    },
                    ["description"] = $"Call to in-file symbol '{calledName}' could not be resolved "
                        + "to a method declaration (event/delegate invoke or degraded compilation); "
                        + "calls edge omitted rather than emitting an unbindable bare-name target.",
                    ["metadata"] = new Dictionary<string, object?>
                    {
                        ["calledName"] = calledName,
                    },
                });
            }
        }
    }

    {
        foreach (var creation in node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            // B1 calls subtype core (Stage 3a): `constructor` for `new T(...)`.
            // Resolve the type semantically so the target binds to the type's
            // element key cross-file, and so external/BCL types (no declaration)
            // emit no edge — replacing the old name-only `allNames` match that
            // both missed cross-file types and false-matched BCL type names
            // against same-named local members (Fathom row 5.0.68.1).
            var typeTarget = ResolveTypeTarget(creation.Type);
            if (typeTarget != null)
            {
                AddWithTargetRef("calls", "constructor", typeTarget.Value.QualifiedRawName, typeTarget.Value.FilePath);
            }
        }
    }

    if (node is TypeDeclarationSyntax overrideContainer)
    {
        // Fathom row `l2-overrides-edge-first-class` (3.1.2.1 P4a): emit
        // `overrides` edges for class methods that override a parent's
        // method — either via `override` keyword (class extension /
        // abstract override) OR implicit/explicit interface implementation.
        // Direction: source = OVERRIDING method (this class's member);
        // target = OVERRIDDEN method (parent class or interface member).
        // Same convention as TS analyzer (nodegraph-analyzer-typescript@0.36.0).
        var seenOverrides = new HashSet<string>();
        foreach (var member in overrideContainer.Members.OfType<MethodDeclarationSyntax>())
        {
            var memberSymbol = semanticModel.GetDeclaredSymbol(member) as IMethodSymbol;
            if (memberSymbol == null) continue;

            // Collect the set of parent methods this member overrides.
            var parents = new List<IMethodSymbol>();

            // 1. Class override (explicit `override` keyword on virtual/abstract).
            if (memberSymbol.OverriddenMethod != null)
            {
                parents.Add(memberSymbol.OverriddenMethod);
            }

            // 2. Interface implementations (implicit + explicit).
            var containingType = memberSymbol.ContainingType;
            if (containingType != null)
            {
                foreach (var iface in containingType.AllInterfaces)
                {
                    foreach (var ifaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
                    {
                        var impl = containingType.FindImplementationForInterfaceMember(ifaceMethod);
                        if (impl != null && SymbolEqualityComparer.Default.Equals(impl, memberSymbol))
                        {
                            parents.Add(ifaceMethod);
                        }
                    }
                }
            }

            foreach (var parent in parents)
            {
                if (parent.ContainingType == null) continue;
                var parentTypeName = parent.ContainingType.Name;
                var parentMethodName = parent.Name;
                // Param-sig matching `GetParamSignature` convention: (type1,type2)
                // with spaces stripped + `/` → `-`. Use ToDisplayString for the
                // Roslyn IParameterSymbol.Type — gives a canonical representation
                // the substrate's tail-match can bridge to declaration-side keys.
                string parentParamSig;
                if (parent.Parameters.Length == 0)
                {
                    parentParamSig = "()";
                }
                else
                {
                    parentParamSig = "(" + string.Join(",", parent.Parameters.Select(p =>
                        p.Type.ToDisplayString().Replace("/", "-").Replace(" ", ""))) + ")";
                }
                var parentQualified = $"{parentTypeName}/{parentMethodName}{parentParamSig}";
                var canonical = CanonicalizePath(parentQualified);
                if (string.IsNullOrEmpty(canonical)) continue;
                if (!seenOverrides.Add(canonical)) continue;

                // Resolve parent's declaring file for cross-file targetRef.
                var parentTargetFile = parent.DeclaringSyntaxReferences
                    .Select(r => r.SyntaxTree.FilePath)
                    .FirstOrDefault();
                if (parentTargetFile == currentFilePath) parentTargetFile = null;
                if (parentTargetFile != null && parentTargetFile.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    // Don't emit overrides for external (npm-style; rare in .NET
                    // but defensive). Strict-emit budget consistent with TS analyzer.
                    parentTargetFile = null;
                    continue;
                }

                if (parentTargetFile != null)
                {
                    var targetRef = MakeNaturalKey(parentTargetFile, canonical);
                    relationships.Add(new { type = "overrides", targetName = canonical, targetRef });
                }
                else
                {
                    relationships.Add(new { type = "overrides", targetName = canonical });
                }
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
            if (handlerName == null || !allNames.Contains(handlerName)) continue;
            // `event += handler` — resolve the handler method to its signatured
            // element key so the delegate edge binds (was a bare name that
            // could not bind — Fathom row 5.0.68.1). Unresolvable handlers
            // (method-group of an external type, etc.) record a limitation
            // rather than emit an unbindable bare edge.
            var handlerTarget = ResolveCallTarget(assign.Right);
            if (handlerTarget != null)
            {
                AddWithTargetRef("calls", "delegates", handlerTarget.Value.QualifiedRawName, handlerTarget.Value.FilePath);
            }
            else
            {
                var hSpan = assign.GetLocation().GetLineSpan();
                limitations.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "csharp-unresolved-call",
                    ["severity"] = "minor",
                    ["location"] = new Dictionary<string, object?>
                    {
                        ["file"] = currentFilePath,
                        ["startLine"] = hSpan.StartLinePosition.Line + 1,
                        ["endLine"] = hSpan.EndLinePosition.Line + 1,
                    },
                    ["description"] = $"Event-handler '{handlerName}' in a `+=` subscription could not be "
                        + "resolved to a method declaration; delegates edge omitted rather than emitting "
                        + "an unbindable bare-name target.",
                    ["metadata"] = new Dictionary<string, object?> { ["calledName"] = handlerName },
                });
            }
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

    // Property / indexer access → `calls` to the accessor element. C# property
    // access is a method call on the get/set accessor; modeling it only as a
    // generic `references` left the call graph blind to it (Gate 5 /
    // dotnet-l0-property-accessor-edges; mirrors TS 5.0.66/5.0.67). Reads → get,
    // writes (LHS of assignment) → set. External/BCL accessors and plain fields
    // resolve to null → no edge (strict-emit budget). Invocation callees are
    // skipped here — `obj.M()` is a method call handled by the invocation loop.
    foreach (var access in node.DescendantNodes())
    {
        bool isMember = access is MemberAccessExpressionSyntax;
        bool isElement = access is ElementAccessExpressionSyntax;
        if (!isMember && !isElement) continue;
        // Skip the callee position of an invocation (that's a method call).
        if (access.Parent is InvocationExpressionSyntax inv && inv.Expression == access) continue;
        // Read vs write: LHS of any assignment → set-accessor; else get.
        var isWrite = access.Parent is AssignmentExpressionSyntax asgn && asgn.Left == access;
        var accessorTarget = ResolveAccessorTarget(access, isWrite);
        if (accessorTarget != null)
        {
            AddWithTargetRef(
                "calls",
                isWrite ? "property-set" : "property-get",
                accessorTarget.Value.QualifiedRawName,
                accessorTarget.Value.FilePath);
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

// --- Group E entry-point detection ---

/// <summary>
/// Per language-conformance Group E. Detect the entry-point kind for a
/// syntax node. Subset of E1 enum at v1 (main / http-handler /
/// library-export / other / none); E3 aggressive heuristic seed fires on
/// suggestive class-name patterns when no first-class kind matches.
/// </summary>
/// <summary>
/// Compute the suggestive-name heuristic note for a class declaration,
/// regardless of which first-class entry-point kind matched. Carries the
/// triage signal alongside the closed-enum entryPoint kind so
/// library-export-tagged classes still surface for catalogue growth.
/// </summary>
static Dictionary<string, object?>? SuggestiveNameNote(SyntaxNode node)
{
    if (node is not ClassDeclarationSyntax cls) return null;
    var className = cls.Identifier.Text;
    foreach (var pattern in EntryPointHelpers.E3ClassNamePatterns)
    {
        if (className.EndsWith(pattern.Suffix) && className != pattern.Suffix)
        {
            return new Dictionary<string, object?>
            {
                ["matchedHeuristic"] = $"class-name-suffix:{pattern.Suffix}",
                ["proposedKind"] = pattern.ProposedKind,
            };
        }
    }
    return null;
}

static (string Kind, Dictionary<string, object?>? Trigger, Dictionary<string, object?>? Limitation, Dictionary<string, object?>? HeuristicNote)
    DetectEntryPoint(SyntaxNode node, string? accessibility, string filePath)
{
    var heuristicNote = SuggestiveNameNote(node);

    // E1 — main: method named Main on a class.
    if (node is MethodDeclarationSyntax m && m.Identifier.Text == "Main")
    {
        return ("main", null, null, null);
    }

    // E1 — http-handler: method carrying an HTTP routing attribute.
    if (node is MethodDeclarationSyntax method)
    {
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString().Split('<')[0].Split('.').Last();
                if (attrName.EndsWith("Attribute")) attrName = attrName[..^9];
                if (EntryPointHelpers.HttpAttributeNames.Contains(attrName))
                {
                    var trigger = new Dictionary<string, object?> { ["method"] = attrName };
                    var args = attr.ArgumentList?.Arguments;
                    if (args != null && args.Value.Count > 0
                        && args.Value[0].Expression is LiteralExpressionSyntax lit
                        && lit.Token.Value is string routePath)
                    {
                        trigger["path"] = routePath;
                    }
                    return ("http-handler", trigger, null, null);
                }
            }
        }
    }

    // E1 — event-handler: a method matching the .NET event-callback convention
    // `(object sender, TEventArgs e)` — exactly two params, the first `object`
    // (or `object?`), the second a type whose simple name ends in `EventArgs`
    // (WinForms / WebForms / WPF / `EventHandler<T>`). The framework invokes it
    // on an event, so it is an entry point (the deferred E1 `event-handler`
    // kind). The L1 `methodStereotype` derivation consumes this entryPoint to
    // classify the method's role — keeping the framework-specific shape
    // detection in the analyzer and the stereotype derivation language-agnostic
    // (Fathom L1-.NET fix #2). Methods named `Main` and HTTP-attributed methods
    // are already classified above.
    if (node is MethodDeclarationSyntax ehMethod && ehMethod.ParameterList.Parameters.Count == 2)
    {
        var p0 = ehMethod.ParameterList.Parameters[0].Type?.ToString();
        var p1 = ehMethod.ParameterList.Parameters[1].Type?.ToString();
        if ((p0 == "object" || p0 == "object?")
            && p1 != null
            && p1.Split('<')[0].Split('.').Last().EndsWith("EventArgs", System.StringComparison.Ordinal))
        {
            return ("event-handler", null, null, null);
        }
    }

    // E1 — http-controller: class whose name ends in `Controller`. Per
    // row 4.4.2.1 — promoted out of the E3 catalogue based on PNE/PNP
    // triage (688 unambiguous cases). Fires whether the class is public
    // or not; library-export comes after so Controller wins.
    // heuristicNote rides along for the triage flywheel.
    if (node is ClassDeclarationSyntax controllerCls)
    {
        var controllerName = controllerCls.Identifier.Text;
        if (controllerName.EndsWith("Controller") && controllerName != "Controller")
        {
            return ("http-controller", null, null, heuristicNote);
        }
    }

    // E1 — wcf-service: top-level public type declared in a `.svc.cs`
    // source file. Per row 4.4.2.2 — PNE/PNP triage surfaced WCF
    // services as a distinct, file-path-detectable subset of the broad
    // `service-class` heuristic. heuristicNote rides along for any
    // residual signal interest.
    if (node is TypeDeclarationSyntax wcfType
        && accessibility == "public"
        && filePath.EndsWith(".svc.cs", System.StringComparison.OrdinalIgnoreCase)
        && wcfType.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == null)
    {
        return ("wcf-service", null, null, heuristicNote);
    }

    // E1 — library-export: public type at namespace level. Heuristic note
    // rides along when the class name also matches a suggestive pattern.
    if (node is TypeDeclarationSyntax typeDecl
        && accessibility == "public"
        && typeDecl.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == null)
    {
        return ("library-export", null, null, heuristicNote);
    }

    // E1 — library-export-method: a PUBLIC method on a library-export type
    // (public top-level type) is externally reachable via `new T().M()`, so it
    // is a method-level entry point — not `none`. Mirrors the TS analyzer's
    // `library-export-method` (Fathom rows 5.0.55 / 5.3.4.3.1) and closes the
    // L0-.NET Gate 3 finding (public methods on library types defaulted to
    // `none`, starving L1 stereotype / L2 capability-unit seeding). Methods
    // named `Main` and HTTP-attributed methods are already classified above;
    // constructors are `ConstructorDeclarationSyntax` (not `MethodDeclarationSyntax`)
    // so excluded — they're reachable via the type. Accessibility-gated:
    // private / protected / internal methods are not externally callable.
    // Methods AND get/set accessors: a public accessor on a public property of
    // a library-export type is externally callable via `new T().Prop` /
    // `new T().Prop = v`, same as a method (Fathom 5.0.68.2.1; TS parity with
    // 5.3.4.3.3). The accessor's `accessibility` now correctly inherits the
    // property's (DeriveAccessibility fix), so the public gate works.
    if ((node is MethodDeclarationSyntax || node is AccessorDeclarationSyntax) && accessibility == "public")
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType != null
            && containingType.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
            && containingType.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == null)
        {
            return ("library-export-method", null, null, null);
        }
    }

    // E3 — auto-discovery heuristic: class-name pattern matched but no
    // first-class kind. Emit `other` + J1 limitation per closed-enum rule.
    if (heuristicNote != null && node is ClassDeclarationSyntax cls)
    {
        var className = cls.Identifier.Text;
        var matchedHeuristic = (string)heuristicNote["matchedHeuristic"]!;
        var proposedKind = (string)heuristicNote["proposedKind"]!;
        var suffix = matchedHeuristic.StartsWith("class-name-suffix:")
            ? matchedHeuristic.Substring("class-name-suffix:".Length)
            : matchedHeuristic;
        var lineSpan = node.GetLocation().GetLineSpan();
        var limitation = new Dictionary<string, object?>
        {
            ["kind"] = "entry-point-pattern-unmatched",
            ["severity"] = "minor",
            ["location"] = new Dictionary<string, object?>
            {
                ["file"] = filePath,
                ["startLine"] = lineSpan.StartLinePosition.Line + 1,
                ["endLine"] = lineSpan.EndLinePosition.Line + 1,
            },
            ["description"] = $"Class name '{className}' matches suggestive pattern '*{suffix}' but no known framework binding",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["matchedHeuristic"] = matchedHeuristic,
                ["proposedKind"] = proposedKind,
                ["className"] = className,
            },
        };
        return ("other", null, limitation, heuristicNote);
    }

    return ("none", null, null, null);
}

/// <summary>
/// Derive the language-conformance D1 accessibility facet from a syntax
/// node + its already-populated flavors dictionary. Maps C#'s richer
/// hierarchy (private-protected, protected-internal) onto the uniform
/// four-value enum (public / protected / internal / private). Defaults
/// follow C# semantics: interface members → public; class-level types →
/// internal; class members → private.
/// </summary>
static string? DeriveAccessibility(SyntaxNode node, Dictionary<string, object?> flavors)
{
    if (flavors.TryGetValue("access", out var accessObj) && accessObj is string explicitAccess)
    {
        // Map C#-specific combined modifiers to the uniform enum.
        return explicitAccess switch
        {
            "private-protected" => "private",
            "protected-internal" => "internal",
            "file" => "internal",
            _ => explicitAccess,
        };
    }
    // Defaults: interface members are implicitly public.
    var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
    if (containingType is InterfaceDeclarationSyntax) return "public";
    // Class-level type declarations (no containing type) default to internal.
    if (node is TypeDeclarationSyntax && containingType == null) return "internal";
    if (node is BaseTypeDeclarationSyntax && containingType == null) return "internal";
    // An accessor with no explicit modifier inherits the enclosing property's
    // accessibility — a `get`/`set` on a `public` property is public, not
    // private (Fathom 5.0.68.2.1). Without this, accessor elements reported
    // `private` on public properties, hiding them from library-export-method
    // classification (and mis-stating the `access` facet).
    if (node is AccessorDeclarationSyntax accessor
        && accessor.Parent?.Parent is BasePropertyDeclarationSyntax prop)
    {
        var propAccess = UniformAccessFromModifiers(prop.Modifiers);
        if (propAccess != null) return propAccess;
        // Property with no access modifier → class-member default (private).
    }
    // Class members default to private.
    if (containingType != null && (node is MemberDeclarationSyntax || node is AccessorDeclarationSyntax))
    {
        return "private";
    }
    return null;
}

/// <summary>
/// Map a declaration's modifier list to the uniform accessibility enum
/// (public / internal / protected / private), applying C#'s combined-modifier
/// rules (`private protected` → private, `protected internal` → internal).
/// Returns null when no access modifier is present (caller applies the
/// context default). Mirrors the per-modifier mapping in
/// <c>Program.Analysis.cs</c>.
/// </summary>
static string? UniformAccessFromModifiers(SyntaxTokenList modifiers)
{
    string? access = null;
    foreach (var m in modifiers)
    {
        switch (m.Text)
        {
            case "public": access = "public"; break;
            case "private": access = access == "protected" ? "private" : "private"; break;
            case "protected": access = access == "internal" ? "internal" : "protected"; break;
            case "internal": access = access == "protected" ? "internal" : "internal"; break;
        }
    }
    return access;
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

static string Canonicalize(string raw) => NamingHelpers.Canonicalize(raw);

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

/// <summary>
/// Language-conformance Group E helpers — aggressive heuristic seeds for
/// entry-point detection. Per operator decision: false positives
/// generate triage entries (via the entry-point-pattern-unmatched
/// limitation path), which IS the design — recurring patterns advance to
/// first-class taxonomy via the triage flywheel.
/// </summary>
static class EntryPointHelpers
{
    /// <summary>
    /// Attribute names that indicate HTTP routing (ASP.NET Core, Web API,
    /// WCF / ASMX legacy).
    /// </summary>
    public static readonly HashSet<string> HttpAttributeNames = new()
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpPatch", "HttpDelete", "HttpHead", "HttpOptions",
        "Route", "RoutePrefix",
        "ApiController", "Controller",
        "WebMethod", "OperationContract",
    };

    /// <summary>
    /// Class-name suffix patterns that match suggestive entry-point
    /// shapes. Aggressive seed; covers common framework conventions.
    ///
    /// `Controller` was promoted to first-class `http-controller` E1
    /// kind in row 4.4.2.1 (688 unambiguous PNE+PNP cases) and
    /// removed from this catalogue. The dedicated detection branch
    /// fires earlier in DetectEntryPoint so Controller-named classes
    /// never reach E3 here.
    ///
    /// `Handler` was DROPPED in row 4.4.2.3 — PNE+PNP triage found 26
    /// cases spanning ≥5 overlapping semantics (HTTP message handlers,
    /// database wrappers, DICOM file-format handlers, native event-
    /// callback handlers, misc). Triage signal too noisy to act on;
    /// narrowing to MessageHandler / EventHandler would have caught
    /// zero cases in the observed corpora. Re-introduce as a specific
    /// subkind if a future framework binding warrants it.
    /// </summary>
    public static readonly (string Suffix, string ProposedKind)[] E3ClassNamePatterns = new[]
    {
        ("Service", "service-class"),
        ("Endpoint", "endpoint-class"),
        ("Hub", "hub-class"),
        ("Worker", "worker-class"),
        ("Consumer", "consumer-class"),
        ("Job", "job-class"),
        ("Function", "function-class"),
    };
}
