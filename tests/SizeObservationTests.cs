using Microsoft.CodeAnalysis.CSharp;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Regression tests for the span-consistent LOC classification fix
/// (Fathom row 3.1.1.1.9.1b <c>l0-dotnet-linesofcode-outofspan</c>).
///
/// Root cause: <c>ExtractObservation</c> used the physical span
/// (GetLocation().GetLineSpan().Span, EXCLUDES leading XML-doc trivia)
/// as the denominator for <c>physicalLinesOfCode</c>, but then subtracted
/// <c>CountCommentLines</c> which used <c>DescendantTrivia()</c> and
/// <em>included</em> the first token's leading XML-doc trivia (FullSpan).
/// When docLines ≥ physicalLines the <c>Math.Max(0,…)</c> floor silently
/// clamped linesOfCode to 0; for larger methods it silently understated
/// LOC. Additionally, <c>trivia.ToFullString().Split('\n').Length</c>
/// over-counted a 10-line doc block as 11 (trailing newline +1).
///
/// Fix (Swift-policy, mirrors the TS fix in analyzer-typescript@0.44.0):
/// per-line classification strictly within [startLine, endLine] only;
/// no Math.Max floor (negative throws — no-silent-degradation);
/// <c>docCommentLineCount</c> counts newline characters in the doc trivia
/// string rather than using Split('\n').Length (avoids trailing-newline +1).
///
/// Five failure-shape categories witnessed RED before the fix.
/// </summary>
public class SizeObservationTests
{
    // --- harness -------------------------------------------------------

    /// <summary>
    /// Parse <paramref name="code"/>, find the first method declaration,
    /// and call <see cref="AnalysisHelpers.ExtractObservation"/>.
    /// Returns the raw observation dictionary.
    /// </summary>
    private static object ExtractFirstMethod(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var node = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();
        return AnalysisHelpers.ExtractObservation(node, tree);
    }

    private static Dictionary<string, object?> GetSection(object obs, string key)
        => (Dictionary<string, object?>)((Dictionary<string, object?>)obs)[key]!;

    // The size/doc dictionaries store int values (C# ints, boxed as object).
    // Use Convert.ToInt32 to unbox safely regardless of int/long storage.
    private static int I(Dictionary<string, object?> d, string k)
        => Convert.ToInt32(d[k]);

    // --- cat-1: XML-doc'd method, docLines > codeLines → loc floors to 0 ----
    //
    // Hand-computation:
    //   Doc block occupies 11 `///` lines (lines 2-12 in the class, 1-based).
    //   Roslyn's GetLocation().GetLineSpan().Span starts at `public int` (line 12).
    //   Physical span = declaration + body + `}` = 3 lines (lines 12-14).
    //   OLD: CountCommentLines = DescendantTrivia picked up 11 leading-trivia lines
    //        → Math.Max(0, 3-0-11) = 0.  LOC silently zeroed.
    //   FIX: per-line scan over [12,14] → all 3 lines are code.
    //        commentLineCount = 0, linesOfCode = 3.

    [Fact]
    public void Cat1_DocumentedMethod_DocLinesGtCodeLines_LocNotZero()
    {
        const string code = @"public class C {
    /// <summary>
    /// This is a documented method.
    /// </summary>
    /// <param name=""a"">First param.</param>
    /// <param name=""b"">Second param.</param>
    /// <param name=""c"">Third param.</param>
    /// <param name=""d"">Fourth param.</param>
    /// <param name=""e"">Fifth param.</param>
    /// <returns>Sum.</returns>
    /// <remarks>Extra remarks here.</remarks>
    public int DocumentedMethod(int a, int b, int c, int d, int e) {
        return a + b + c + d + e;
    }
}";
        // Lines (1-based):
        //  1: public class C {
        //  2:     /// <summary>
        //  3:     /// This is a documented method.
        //  4:     /// </summary>
        //  5:     /// <param name="a">First param.</param>
        //  6:     /// <param name="b">Second param.</param>
        //  7:     /// <param name="c">Third param.</param>
        //  8:     /// <param name="d">Fourth param.</param>
        //  9:     /// <param name="e">Fifth param.</param>
        // 10:     /// <returns>Sum.</returns>
        // 11:     /// <remarks>Extra remarks here.</remarks>
        // 12:     public int DocumentedMethod(int a, int b, int c, int d, int e) {  ← startLine
        // 13:         return a + b + c + d + e;
        // 14:     }   ← endLine
        // 15: }
        // physicalLinesOfCode = 14 - 12 + 1 = 3.
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        Assert.Equal(3, I(size, "physicalLinesOfCode"));
        // No comment lines within the 3-line physical span.
        Assert.Equal(0, I(size, "commentLineCount"));
        // All 3 lines are code.
        Assert.Equal(3, I(size, "linesOfCode"));
    }

    // --- cat-2: body large enough NOT to floor (silent understatement witness) -
    //
    // Hand-computation:
    //   3-line doc above a 9-line physical span (lines 5-13).
    //   OLD: commentLineCount = 3 (doc lines), linesOfCode = max(0,9-0-3) = 6.
    //        (A 9-line body with 4 doc lines: max(0,9-4)=5 in the design description;
    //         actual depends on exact `///` count — the key claim is loc < physical.)
    //   FIX: 0 in-span comment lines → linesOfCode = 9.

    [Fact]
    public void Cat2_DocumentedLargeMethod_LoCUnderstated()
    {
        const string code = @"public class C {
    /// <summary>Documented large method.</summary>
    /// <param name=""x"">Input.</param>
    /// <returns>Processed value.</returns>
    public int BigEnough(int x) {
        var a = x + 1;
        var b = a * 2;
        var c = b - 3;
        var d = c + 4;
        var e = d * 5;
        var f = e + 6;
        return f;
    }
}";
        // Lines (1-based):
        //  1: public class C {
        //  2:     /// <summary>...</summary>
        //  3:     /// <param name="x">Input.</param>
        //  4:     /// <returns>Processed value.</returns>
        //  5:     public int BigEnough(int x) {   ← startLine
        //  6:         var a = x + 1;
        //  7:         var b = a * 2;
        //  8:         var c = b - 3;
        //  9:         var d = c + 4;
        // 10:         var e = d * 5;
        // 11:         var f = e + 6;
        // 12:         return f;
        // 13:     }   ← endLine
        // 14: }
        // physicalLinesOfCode = 13 - 5 + 1 = 9.
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        Assert.Equal(9, I(size, "physicalLinesOfCode"));
        // No comment lines within the 9-line physical span.
        Assert.Equal(0, I(size, "commentLineCount"));
        // All 9 lines are code.
        Assert.Equal(9, I(size, "linesOfCode"));
    }

    // --- cat-3: trailing same-line comment `Foo(); // note` → CODE line ------
    //
    // Mixed-line policy: a line that begins with non-comment code is a CODE line
    // even when it has a trailing `// comment`. The old trivia walk counted
    // trailing comment trivia as full comment lines.

    [Fact]
    public void Cat3_TrailingSameLineComment_CountsAsCode()
    {
        const string code = @"public class C {
    public int WithTrailing() {
        var x = 1; // trailing comment here
        return x;  // another trailing
    }
}";
        // Lines (1-based):
        //  1: public class C {
        //  2:     public int WithTrailing() {   ← startLine
        //  3:         var x = 1; // trailing comment here
        //  4:         return x;  // another trailing
        //  5:     }   ← endLine
        //  6: }
        // physicalLinesOfCode = 5 - 2 + 1 = 4.
        // trimmed starts with `var` / `return` → CODE lines, not comment.
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        Assert.Equal(4, I(size, "physicalLinesOfCode"));
        Assert.Equal(0, I(size, "commentLineCount"));
        Assert.Equal(4, I(size, "linesOfCode"));
    }

    // --- cat-4: commentDensity bounded to ≤ 1.0 (was > 1.0) ------------------
    //
    // Hand-computation (same shape as cat-1 but smaller):
    //   4 doc lines above a 3-line physical span.
    //   OLD: commentLineCount = 4, physicalLinesOfCode = 3 → density = 4/3 ≈ 1.33.
    //   FIX: commentLineCount = 0, physicalLinesOfCode = 3 → density = 0.

    [Fact]
    public void Cat4_CommentDensityBoundedLeOnePointZero()
    {
        const string code = @"public class C {
    /// <summary>First line of doc comment.</summary>
    /// <param name=""a"">First param.</param>
    /// <param name=""b"">Second param.</param>
    /// <returns>Sum.</returns>
    public int DensityCheck(int a, int b) {
        return a + b;
    }
}";
        // Lines (1-based):
        //  1: public class C {
        //  2:     /// <summary>...</summary>
        //  3:     /// <param .../>
        //  4:     /// <param .../>
        //  5:     /// <returns>Sum.</returns>
        //  6:     public int DensityCheck(int a, int b) {   ← startLine
        //  7:         return a + b;
        //  8:     }   ← endLine
        //  9: }
        // physicalLinesOfCode = 8 - 6 + 1 = 3.
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        var density = (double)size["commentDensity"]!;
        Assert.True(
            density <= 1.0,
            $"commentDensity must be ≤ 1.0, got {density}"
        );
        // After span-consistent fix: 0 in-span comment lines / 3 physical = 0.0.
        Assert.Equal(0.0, density);
    }

    // --- cat-5: docCommentLineCount no trailing-newline +1 overcount ----------
    //
    // A 10-line XML doc block (10 `///` lines).
    // Roslyn emits each `///` line as its OWN SingleLineDocumentationCommentTrivia.
    // The OLD code called trivia.ToFullString().Split('\n').Length on the FIRST
    // trivia item only, which ends in \n — so a 1-line doc block returned 2
    // (the text "    /// <summary>\n" splits into ["    /// <summary>", ""]).
    // The fix: count actual newline characters in the full doc block text.
    // For a 10-line doc block we sum all 10 trivia items' text; each ends in \n
    // giving exactly 10 newlines → docCommentLineCount = 10.

    [Fact]
    public void Cat5_DocCommentLineCount_TenLineDocBlock_IsExactlyTen()
    {
        const string code = @"public class C {
    /// <summary>
    /// Line 1.
    /// Line 2.
    /// Line 3.
    /// Line 4.
    /// Line 5.
    /// Line 6.
    /// Line 7.
    /// Line 8.
    /// </summary>
    public void TenLineDoc() {
        return;
    }
}";
        // Lines (1-based):
        //  1: public class C {
        //  2-11: 10 `///` lines
        // 12:     public void TenLineDoc() {   ← startLine
        // 13:         return;
        // 14:     }   ← endLine
        // 15: }
        var obs = ExtractFirstMethod(code);
        var doc = GetSection(obs, "documentation");

        Assert.True((bool)doc["hasDocComment"]!, "hasDocComment should be true");

        // Must be exactly 10, not 11 (trailing-newline +1 overcount).
        Assert.Equal(10, I(doc, "docCommentLineCount"));
    }

    // --- existing documentation semantics regression ----------------------
    //     Confirm that documentation fields survive the LOC fix intact.

    // --- bug regression: 3.1.1.1.9.1c l0-loc-classifier-prefix-only-string-block ----
    //
    // Root cause: the per-line classifier was stateless (prefix-only), so:
    //   F1: a line inside a C# verbatim string that starts with a comment token
    //       (`//`) was miscounted as a comment line (LOC undercounted).
    //   F2: a line inside a block comment that does NOT start with `*` was
    //       miscounted as a code line (LOC overcounted).
    // Fix: stateful per-line classification tracking inBlockComment + inVerbatimString.

    [Fact]
    public void F1_VerbatimStringInterior_CommentTokenIsCode()
    {
        // A line inside a @"..." verbatim string that starts with // must be
        // classified as CODE, not comment (it is string content, not source comment).
        const string code = @"public class C {
    public string WithVerbatim() {
        var s = @""
// looks like a comment but is string content
   no comment here either
"";
        return s;
    }
}";
        // Lines of WithVerbatim() (startLine=2, endLine=8, physicalLinesOfCode=7):
        //  2:     public string WithVerbatim() {
        //  3:         var s = @"        ← opens verbatim string
        //  4: // looks like a comment   ← F1: currently comment, must be code
        //  5:    no comment here either ← verbatim string interior
        //  6: ";                        ← closes verbatim string
        //  7:         return s;
        //  8:     }
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        Assert.Equal(7, I(size, "physicalLinesOfCode"));
        // Line 4 is inside a verbatim string — must be code, not comment.
        Assert.Equal(0, I(size, "commentLineCount"));
        Assert.Equal(7, I(size, "linesOfCode"));
    }

    [Fact]
    public void F2_NonStarBlockCommentInterior_IsComment()
    {
        // A block comment interior line that does NOT start with `*` must still
        // be counted as a comment line. The stateless classifier missed these.
        const string code = @"public class C {
    public int WithNonStarBlock() {
        /* a comment
           no star here
        */
        return 1;
    }
}";
        // Lines of WithNonStarBlock() (startLine=2, endLine=7, physicalLinesOfCode=6):
        //  2:     public int WithNonStarBlock() {
        //  3:         /* a comment           ← comment (opens block)
        //  4:            no star here        ← F2: currently code, must be comment
        //  5:         */                     ← comment (closes block)
        //  6:         return 1;
        //  7:     }
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        Assert.Equal(6, I(size, "physicalLinesOfCode"));
        // Lines 3, 4, 5 are all part of the block comment.
        Assert.Equal(3, I(size, "commentLineCount"));
        Assert.Equal(3, I(size, "linesOfCode"));
    }

    [Fact]
    public void Existing_DocFieldsStillPopulated_AfterFix()
    {
        const string code = @"public class C {
    /// <summary>A documented method.</summary>
    public void Documented() {}
}";
        var obs = ExtractFirstMethod(code);
        var doc = GetSection(obs, "documentation");

        Assert.True((bool)doc["hasDocComment"]!, "hasDocComment should be true");
        Assert.True(doc.ContainsKey("docCommentLineCount"),
            "docCommentLineCount should be present when hasDocComment=true");
    }

    [Fact]
    public void Existing_UndocumentedMethod_HasDocCommentFalse()
    {
        const string code = @"public class C {
    public void Undocumented() {}
}";
        var obs = ExtractFirstMethod(code);
        var doc = GetSection(obs, "documentation");

        Assert.False((bool)doc["hasDocComment"]!, "hasDocComment should be false");
        Assert.False(doc.ContainsKey("docCommentLineCount"),
            "docCommentLineCount should be absent when hasDocComment=false");
    }

    [Fact]
    public void Existing_BodyComment_CountsInCommentLineCount()
    {
        const string code = @"public class C {
    public int Process(int x) {
        // step 1
        var a = x + 1;
        // step 2
        return a * 2;
    }
}";
        // 2 single-line comments within the method body span — these are
        // in-span (lines where `trimmed.StartsWith("//")`) and must be counted.
        var obs = ExtractFirstMethod(code);
        var size = GetSection(obs, "size");

        Assert.Equal(2, I(size, "commentLineCount"));
    }
}
