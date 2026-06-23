/// <summary>
/// Analysis-component helpers used by the .NET analyzer plugin.
///
/// The plugin produces the structural and observation fields the
/// 0.4.0 `@kepello/nodegraph-analysis` wire contract defines:
///   - AnalyzerObservation.size — per-element size measurements
///   - role / flavors / capabilities — declarable taxonomy metadata
///
/// Per the 0.4.0 contract (Fathom row 3.1.1.1.9.1b — span-consistent
/// per-line classification, matching the Swift reference and the TS
/// fix shipped in analyzer-typescript@0.44.0):
///   - `linesOfCode` = in-span code lines (physical − blanks − in-span
///     comment-only lines; the element's leading XML-doc block is EXCLUDED
///     — it is out-of-span trivia and must NOT enter the LOC formula)
///   - `physicalLinesOfCode` is the geometric span from
///     GetLocation().GetLineSpan() (excludes leading trivia)
///   - `commentDensity` = in-span comment lines / physicalLinesOfCode
///     (bounded [0,1] by construction — every physical line is exactly
///     one of blank | comment-only | code)
///   - A negative linesOfCode after span-consistent classification is
///     impossible by construction; if it occurs, we throw rather than
///     floor (no-silent-degradation, Fathom row 3.1.1.1.9.1b)
///
/// Statement-level body measurements (the old `logicalLinesOfCode`)
/// move into per-method scalars in slice 3 — not emitted today.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class AnalysisHelpers
{
    // --- Size observations ---------------------------------------------

    /// <summary>
    /// Extract size-domain + documentation-domain measurements for a C#
    /// declaration node. Always returns a populated `size` sub-record;
    /// `documentation` is populated whenever the analyzer can scan
    /// leading-comment trivia (every node with a span qualifies, so this
    /// is essentially always).
    /// </summary>
    public static object ExtractObservation(SyntaxNode node, SyntaxTree tree)
    {
        // GetLocation().GetLineSpan() returns the PHYSICAL span of the node's
        // token range — it EXCLUDES leading trivia (XML-doc comments, leading
        // blank lines). This is the correct anchor for span-consistent
        // classification (Fathom row 3.1.1.1.9.1b).
        var lineSpan = node.GetLocation().GetLineSpan();
        var startLine = lineSpan.StartLinePosition.Line + 1; // 1-based
        var endLine = lineSpan.EndLinePosition.Line + 1;
        var physicalLinesOfCode = endLine - startLine + 1;

        // Per-line classification strictly within [startLine, endLine].
        // The leading XML-doc block (out-of-span) MUST NOT enter the LOC
        // formula — its signal lives in the documentation sub-record.
        // A line is classified as exactly ONE of:
        //   blank        — TrimmedText is empty
        //   comment-only — line is inside a block comment (/* ... */ tracked
        //                  statefully), OR trimmed starts with `//`, `/*`, or `*`
        //                  (`*` prefix convention for block interiors).
        //                  Lines inside a verbatim string are always code even if
        //                  their trimmed form starts with `//` (F1 fix — 3.1.1.1.9.1c).
        //   code         — everything else (incl. `code; // trailing`)
        //                  A trailing-comment line starts with code, so it
        //                  falls here (mixed-line policy).
        var sourceText = tree.GetText();
        var blankLineCount = 0;
        var commentLineCount = 0;
        var inBlockComment = false;    // inside /* ... */ spanning multiple lines
        var inVerbatimString = false;  // inside @"..." spanning multiple lines
        for (var i = startLine; i <= endLine && i <= sourceText.Lines.Count; i++)
        {
            var lineText = sourceText.Lines[i - 1].ToString();
            var trimmed = lineText.Trim();
            if (trimmed.Length == 0)
            {
                blankLineCount++;
                continue;
            }
            if (inBlockComment)
            {
                // Interior of a multi-line block comment — comment line regardless
                // of prefix (F2 fix: non-star block interiors were miscounted as code).
                commentLineCount++;
                if (lineText.Contains("*/")) inBlockComment = false;
                continue;
            }
            if (inVerbatimString)
            {
                // Inside a verbatim string — code line regardless of prefix
                // (F1 fix: `//` inside @"..." was miscounted as comment).
                if (HasVerbatimStringClose(lineText)) inVerbatimString = false;
                continue;
            }
            // Normal state: classify the line.
            if (trimmed.StartsWith("//"))
                commentLineCount++;
            else if (trimmed.StartsWith("/*"))
            {
                commentLineCount++;
                // Multi-line opener: no `*/` after the `/*` → block continues.
                if (!trimmed.Substring(2).Contains("*/")) inBlockComment = true;
            }
            else if (trimmed.StartsWith("*"))
                // Interior block-comment line using the `*` prefix convention.
                commentLineCount++;
            else
            {
                // Code line — check for verbatim string open or mid-line block comment.
                var atIdx = lineText.IndexOf("@\"");
                if (atIdx >= 0 && !HasVerbatimStringClose(lineText.Substring(atIdx + 2)))
                    inVerbatimString = true;
                var bcIdx = lineText.IndexOf("/*");
                if (bcIdx >= 0 && !lineText.Substring(bcIdx + 2).Contains("*/"))
                    inBlockComment = true;
            }
        }

        // Span-consistent classification guarantees linesOfCode ≥ 0 by
        // construction (every physical line is exactly one of blank/comment/code).
        // A negative result here is a bug — throw rather than silently floor
        // (no-silent-degradation; Fathom row 3.1.1.1.9.1b).
        var linesOfCode = physicalLinesOfCode - blankLineCount - commentLineCount;
        if (linesOfCode < 0)
            throw new InvalidOperationException(
                $"[Analysis] linesOfCode went negative ({linesOfCode}) for node at " +
                $"{tree.FilePath}:{startLine}-{endLine}. " +
                $"This is a bug in span-consistent classification; report to Fathom 3.1.1.1.9.1b."
            );

        // commentDensity = inSpanCommentLines / physicalSpan — bounded [0,1]
        // by construction (numerator ≤ denominator, both non-negative).
        double? commentDensity = null;
        if (physicalLinesOfCode > 0)
            commentDensity = (double)commentLineCount / physicalLinesOfCode;

        var size = new Dictionary<string, object?>
        {
            ["linesOfCode"] = linesOfCode,
            ["physicalLinesOfCode"] = physicalLinesOfCode,
            ["blankLineCount"] = blankLineCount,
            ["commentLineCount"] = commentLineCount,
        };
        if (commentDensity.HasValue)
            size["commentDensity"] = commentDensity.Value;

        return new Dictionary<string, object?>
        {
            ["size"] = size,
            ["documentation"] = ExtractDocumentation(node),
            ["codeSmells"] = new Dictionary<string, object?>
            {
                ["magicNumberCount"] = ExtractMagicNumberCount(node),
            },
        };
    }

    // --- Documentation sub-record (per metrics-dictionary.md §2.7) -----

    private static readonly (string Tag, System.Text.RegularExpressions.Regex Re)[] TagPatterns = new[]
    {
        ("TODO", new System.Text.RegularExpressions.Regex(@"\bTODO\b")),
        ("FIXME", new System.Text.RegularExpressions.Regex(@"\bFIXME\b")),
        ("HACK", new System.Text.RegularExpressions.Regex(@"\bHACK\b")),
        ("XXX", new System.Text.RegularExpressions.Regex(@"\bXXX\b")),
        ("NOTE", new System.Text.RegularExpressions.Regex(@"\bNOTE\b")),
    };

    /// <summary>
    /// Build the documentation sub-record. `hasDocComment` is true iff
    /// the node has a leading XML doc-comment trivia (Roslyn's
    /// SingleLineDocumentationCommentTrivia or
    /// MultiLineDocumentationCommentTrivia — i.e. /// or /** */ in C#).
    /// `commentTagCounts` regex-scans the union of leading + descendant
    /// comment trivia for TODO/FIXME/HACK/XXX/NOTE word-boundary matches.
    /// </summary>
    static Dictionary<string, object?> ExtractDocumentation(SyntaxNode node)
    {
        var hasDocComment = false;
        int? docCommentLineCount = null;

        // Roslyn emits each `///` line as its own SingleLineDocumentationCommentTrivia.
        // Counting via Split('\n').Length on the first such trivia would return 2 for
        // a single-line doc (the trailing \n splits the string into two parts).
        // Instead, sum the actual newline characters across ALL contiguous leading
        // doc-comment trivia — each `///` line's ToFullString() ends in exactly one
        // `\n`, so the total newline count equals the exact number of doc lines
        // (no trailing-newline +1 overcount; Fathom row 3.1.1.1.9.1b).
        var docLineAccumulator = 0;
        var inDocBlock = false;
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                hasDocComment = true;
                inDocBlock = true;
                // Count \n characters rather than Split('\n').Length to avoid
                // the trailing-newline +1 (each `///` line ends in exactly one \n).
                foreach (var ch in trivia.ToFullString())
                    if (ch == '\n') docLineAccumulator++;
            }
            else if (inDocBlock)
            {
                // Stop accumulating once we exit the contiguous doc block.
                break;
            }
        }
        if (hasDocComment)
            docCommentLineCount = docLineAccumulator > 0 ? docLineAccumulator : 1;

        var counts = new Dictionary<string, int>
        {
            ["TODO"] = 0,
            ["FIXME"] = 0,
            ["HACK"] = 0,
            ["XXX"] = 0,
            ["NOTE"] = 0,
        };

        // DescendantTrivia walks leading + trailing trivia of every
        // descendant token, including the first token's leading trivia
        // (i.e. the node's own leading trivia). Don't add GetLeadingTrivia
        // separately — it would double-count.
        foreach (var trivia in node.DescendantTrivia())
        {
            if (!IsCommentTrivia(trivia)) continue;
            var text = trivia.ToFullString();
            foreach (var (tag, re) in TagPatterns)
            {
                counts[tag] += re.Matches(text).Count;
            }
        }

        var doc = new Dictionary<string, object?>
        {
            ["hasDocComment"] = hasDocComment,
            ["commentTagCounts"] = counts,
        };
        if (docCommentLineCount.HasValue)
        {
            doc["docCommentLineCount"] = docCommentLineCount.Value;
        }
        return doc;
    }

    static bool IsCommentTrivia(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
    }

    // --- Magic number observation (per metrics-dictionary §2.8) -------

    /// <summary>
    /// Count NumericLiteralExpression nodes within `node`'s subtree
    /// that aren't part of a constant-like declaration. Skips
    /// allowlisted small values (|v| ∈ {0, 1, 2} — so -1, -2 also pass)
    /// and literals inside `const` / `readonly` field decls,
    /// `const` local decls, and EnumMember initializers.
    /// </summary>
    static int ExtractMagicNumberCount(SyntaxNode node)
    {
        var count = 0;
        foreach (var literal in node.DescendantNodes())
        {
            if (literal is not LiteralExpressionSyntax lit) continue;
            if (!lit.IsKind(SyntaxKind.NumericLiteralExpression)) continue;
            var raw = lit.Token.Value;
            if (raw == null) continue;
            double v;
            try { v = System.Convert.ToDouble(raw); }
            catch { continue; }
            var abs = System.Math.Abs(v);
            if (abs == 0 || abs == 1 || abs == 2) continue;
            if (IsInsideConstantContext(lit)) continue;
            count++;
        }
        return count;
    }

    static bool IsInsideConstantContext(SyntaxNode node)
    {
        var cur = node.Parent;
        while (cur != null)
        {
            if (cur is LocalDeclarationStatementSyntax local)
            {
                foreach (var m in local.Modifiers)
                    if (m.IsKind(SyntaxKind.ConstKeyword)) return true;
                return false;
            }
            if (cur is FieldDeclarationSyntax field)
            {
                foreach (var m in field.Modifiers)
                {
                    if (m.IsKind(SyntaxKind.ConstKeyword)) return true;
                    if (m.IsKind(SyntaxKind.ReadOnlyKeyword)) return true;
                }
                return false;
            }
            if (cur is EnumMemberDeclarationSyntax) return true;
            cur = cur.Parent;
        }
        return false;
    }

    // Returns true when `line` contains a `"` that is NOT part of an escaped `""`
    // pair — i.e. the closing delimiter of a C# verbatim string (Fathom 3.1.1.1.9.1c).
    private static bool HasVerbatimStringClose(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                    i++; // skip escaped "" pair
                else
                    return true; // found unescaped closing "
            }
        }
        return false;
    }

    // CountCommentLines removed (Fathom row 3.1.1.1.9.1b): LOC counting is now
    // strictly per-line within [startLine, endLine] — see ExtractObservation.
    // The trivia-based approach (DescendantTrivia) included the first token's
    // leading XML-doc trivia (out-of-span), causing docLines to be subtracted
    // from an in-span physical count and flooring to 0 under Math.Max(0,…).
    // Additionally, ToFullString().Split('\n').Length overcounted by 1 (trailing
    // newline). Both defects are resolved by the per-line source-text scan.

    // --- Declarable Taxonomy Mapping ----------------------------------

    /// <summary>
    /// Compute role + flavors + capabilities for a C# syntax node.
    /// Returns null for nodes whose element type does not map to a
    /// declarable role.
    ///
    /// Mirrors the role/flavor vocabulary the TS analyzer uses so a
    /// consumer's bridge sees structurally identical metadata across
    /// the two languages.
    /// </summary>
    public static (string role, Dictionary<string, object?> flavors, Dictionary<string, bool> capabilities)?
        MapToDeclarable(SyntaxNode node)
    {
        string? role = null;
        string? typeKind = null;

        switch (node)
        {
            case ClassDeclarationSyntax:
                role = "type";
                typeKind = "class";
                break;
            case InterfaceDeclarationSyntax:
                role = "type";
                typeKind = "interface";
                break;
            case StructDeclarationSyntax:
                role = "type";
                typeKind = "struct";
                break;
            case EnumDeclarationSyntax:
                role = "type";
                typeKind = "enum";
                break;
            case MethodDeclarationSyntax:
                role = "method";
                break;
            case PropertyDeclarationSyntax:
                role = "property";
                break;
            case LocalFunctionStatementSyntax:
                role = "method";
                break;
            case ConstructorDeclarationSyntax:
                role = "constructor";
                break;
            case DestructorDeclarationSyntax:
                role = "method";
                break;
            case EnumMemberDeclarationSyntax:
                role = "enumMember";
                break;
            case FieldDeclarationSyntax:
                role = "field";
                break;
            case EventDeclarationSyntax:
            case EventFieldDeclarationSyntax:
                role = "event";
                break;
            case IndexerDeclarationSyntax:
                role = "indexer";
                break;
            case OperatorDeclarationSyntax:
            case ConversionOperatorDeclarationSyntax:
                role = "operator";
                break;
            case ParameterSyntax:
                role = "parameter";
                break;
            case TypeParameterSyntax:
                role = "typeParameter";
                break;
            case AccessorDeclarationSyntax:
                role = "accessor";
                break;
            case AttributeSyntax:
                role = "annotation";
                break;
        }

        if (role == null) return null;

        var flavors = new Dictionary<string, object?>();
        if (typeKind != null) flavors["typeKind"] = typeKind;

        if (node is OperatorDeclarationSyntax opDecl)
        {
            flavors["operatorKind"] = opDecl.ParameterList.Parameters.Count == 1 ? "unary" : "binary";
        }
        else if (node is ConversionOperatorDeclarationSyntax convDecl)
        {
            flavors["operatorKind"] = convDecl.ImplicitOrExplicitKeyword.Text == "implicit"
                ? "conversion-implicit"
                : "conversion-explicit";
        }
        else if (node is IndexerDeclarationSyntax)
        {
            flavors["indexerKind"] = "cs-callable";
        }
        else if (node is ParameterSyntax paramSyntax)
        {
            foreach (var pm in paramSyntax.Modifiers)
            {
                switch (pm.Text)
                {
                    case "ref": flavors["parameterModifier"] = "ref"; break;
                    case "out": flavors["parameterModifier"] = "out"; break;
                    case "in": flavors["parameterModifier"] = "in"; break;
                    case "params": flavors["parameterModifier"] = "params"; break;
                }
            }
            if (paramSyntax.Default != null) flavors["hasDefault"] = true;
        }

        var modifiers = node switch
        {
            MemberDeclarationSyntax m => m.Modifiers,
            LocalFunctionStatementSyntax l => l.Modifiers,
            _ => default
        };

        foreach (var m in modifiers)
        {
            switch (m.Text)
            {
                case "abstract": flavors["abstract"] = true; break;
                case "static": flavors["static"] = true; break;
                case "sealed": flavors["sealed"] = true; break;
                case "virtual": flavors["virtual"] = true; break;
                case "override": flavors["override"] = true; break;
                case "readonly": flavors["readonly"] = true; break;
                case "async": flavors["async"] = true; break;
                case "partial": flavors["partial"] = true; break;
                case "public": flavors["access"] = "public"; break;
                case "private":
                    flavors["access"] = flavors.TryGetValue("access", out var pa) && (string?)pa == "protected"
                        ? "private-protected"
                        : "private";
                    break;
                case "protected":
                    flavors["access"] = flavors.TryGetValue("access", out var pra) && (string?)pra == "internal"
                        ? "protected-internal"
                        : "protected";
                    break;
                case "internal":
                    flavors["access"] = flavors.TryGetValue("access", out var ia) && (string?)ia == "protected"
                        ? "protected-internal"
                        : "internal";
                    break;
            }
        }

        return (role, flavors, CapabilitiesForRole(role));
    }

    /// <summary>
    /// Fixed per-role capability mask. Mirrors the same table TS / HTML
    /// / CSS analyzers use; a consumer that maps role → capabilities
    /// gets identical results across languages.
    /// </summary>
    static Dictionary<string, bool> CapabilitiesForRole(string role) => role switch
    {
        "namespace" or "type" or "section" =>
            new Dictionary<string, bool> { ["scope"] = true, ["behavior"] = false, ["binding"] = false },
        "method" or "constructor" or "function" or "accessor" or "operator" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = true, ["binding"] = false },
        "property" or "event" or "indexer" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = true, ["binding"] = true },
        "field" or "variable" or "parameter" or "enumMember" or "typeParameter" or "frontmatterField" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = false, ["binding"] = true },
        "ambient" or "annotation" =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = false, ["binding"] = false },
        _ =>
            new Dictionary<string, bool> { ["scope"] = false, ["behavior"] = false, ["binding"] = false }
    };
}
