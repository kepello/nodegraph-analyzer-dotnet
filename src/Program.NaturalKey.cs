/// <summary>
/// Natural-key synthesis — the C# mirror of the canonical escape chain.
///
/// SPEC-PINNED MIRROR: this file must never drift independently. The
/// canonical implementation lives in
/// <c>nodegraph-core/src/natural-key.ts</c> (<c>escapeNaturalKeyComponent</c>)
/// and its composition into an element key in
/// <c>nodegraph-analysis/src/overlay/types.ts</c>
/// (<c>elementNaturalKey</c>). Cross-implementation conformance is pinned
/// by the shared vector file
/// <c>nodegraph-analyzer-conformance/fixtures/natural-key-codec/vectors.json</c>,
/// exercised here via <c>tests/NaturalKeyCodecTests.cs</c>.
///
/// Fathom row <c>5.0.128</c> (<c>naturalkey-codec-consolidation</c>) WP-8,
/// finding F1: prior to this row, this file's logic was a bare
/// <c>'/' -&gt; ':'</c> substitution with no escaping of pre-existing
/// <c>\</c>/<c>:</c>/<c>#</c> — the seventh, divergent copy of the codec
/// that the rest of the arc's mirror wave missed. Any C# artifactId or
/// element name containing one of those characters emitted a ref that
/// silently dangled (or, worse, collided with a different element's key)
/// on the ingest side. This is a LIVE-bug fix: emitted key BYTES change
/// for special-char-bearing names. Pre-prod migration is delete + re-
/// analyze the graph.
///
/// Escape order is load-bearing — see the canonical spec's doc comment
/// for the full injectivity argument. Do not reorder.
/// </summary>

static class NaturalKeyCodec
{
    /// <summary>
    /// Escape a single natural-key component so it is safe to embed on
    /// either side of the structural <c>#</c> delimiter. Mirrors
    /// <c>escapeNaturalKeyComponent</c> in <c>nodegraph-core/src/natural-key.ts</c>
    /// byte-for-byte; see that file for the full escape-scheme rationale.
    /// </summary>
    public static string EscapeNaturalKeyComponent(string component) =>
        component
            .Replace("\\", "\\\\")
            .Replace(":", "\\:")
            .Replace("#", "\\#")
            .Replace("/", ":");

    /// <summary>
    /// Synthesize a URI-safe natural key per the language-conformance A2/A4
    /// convention. Mirrors <c>elementNaturalKey</c> in
    /// <c>nodegraph-analysis/src/overlay/types.ts</c>: an empty
    /// <paramref name="name"/> escapes to <c>""</c>, which falls through to
    /// the bare-artifact form (no trailing <c>#</c>).
    /// </summary>
    public static string MakeNaturalKey(string artifactId, string name)
    {
        var safeArtifact = EscapeNaturalKeyComponent(artifactId);
        var safeName = EscapeNaturalKeyComponent(name);
        return string.IsNullOrEmpty(safeName)
            ? safeArtifact
            : $"{safeArtifact}#{safeName}";
    }
}
