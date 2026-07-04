namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Direct unit tests over <see cref="SemanticCatalog"/> (no spawn — links
/// <c>Program.SemanticCatalog.cs</c> directly, mirroring
/// <c>NamingTests</c>'s pattern for <see cref="NamingHelpers"/>).
/// </summary>
public class SemanticCatalogTests
{
    // ---------- ClassifyApiCategory — sanctioned delta #4 ----------

    [Fact]
    public void ClassifyApiCategory_ExecuteNonQuery_ClassifiesWrite_NotMixed()
    {
        // Pins sanctioned delta #4 (Fathom boundary-drift wave 3.4.1,
        // reviewer finding 2026-07-04): `executenonquery` (a WriteOps
        // entry) contains the substring `query` (a ReadOps entry). The
        // DELETED engine tracked read/write as independent booleans, so a
        // single ExecuteNonQuery edge set BOTH from itself and classified
        // "mixed" (pre-wave engine value: "mixed" — a substring-collision
        // artifact). This analyzer's per-edge classification is
        // write-wins: WriteOps checked first, unconditionally short-
        // circuiting the ReadOps check — so the same target now
        // classifies "write" (accepted as MORE correct; not parity-
        // restored).
        var category = SemanticCatalog.ClassifyApiCategory(
            "system-data-sqlclient-sqlcommand-executenonquery");
        Assert.NotNull(category);
        Assert.Equal("write", category!["operation"]);
    }

    [Fact]
    public void ClassifyApiCategory_ReadOnlyTarget_ClassifiesRead()
    {
        var category = SemanticCatalog.ClassifyApiCategory(
            "system-data-sqlclient-sqlcommand-executereader");
        Assert.NotNull(category);
        Assert.Equal("read", category!["operation"]);
    }

    [Fact]
    public void ClassifyApiCategory_NonPersistenceTarget_ReturnsNull()
    {
        Assert.Null(SemanticCatalog.ClassifyApiCategory("system-console-writeline"));
    }

    // ---------- MapControlKind — JS-vs-.NET `\w` parity (fix D) ----------

    [Fact]
    public void MapControlKind_NonAsciiTypeSegment_DoesNotMatchAsciiOnlyRule()
    {
        // Parity fix D (Fathom boundary-drift wave 3.4.1): the deleted
        // engine's CONTROL_KIND_RULES were JS RegExp literals, where `\w`
        // is ALWAYS ASCII-only (`[A-Za-z0-9_]`). .NET's `\w` defaults to
        // Unicode word characters, so a non-ASCII type-name segment
        // immediately preceding the "Button" suffix (no ASCII boundary to
        // stop the Unicode `\w*` run) used to match the "button" rule where
        // the JS engine's ASCII-only `\w*` would not — a byte-for-byte
        // parity break on non-ASCII (e.g. transliterated) type names.
        // ECMAScript-mode restores JS parity: no match → falls through to
        // "other" (a known-but-unlisted control type, never a guess).
        Assert.Equal("other", SemanticCatalog.MapControlKind("MyNamespace.PörButton"));
    }

    [Fact]
    public void MapControlKind_AsciiTypeSegment_StillMatchesButtonRule()
    {
        // Sanity companion to the non-ASCII pin above — the ASCII case is
        // unaffected by the ECMAScript-mode fix.
        Assert.Equal("button", SemanticCatalog.MapControlKind("MyNamespace.SubmitButton"));
    }
}
