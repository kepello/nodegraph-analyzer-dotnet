/// <summary>
/// Cross-implementation conformance guard for <see cref="NaturalKeyCodec"/>
/// (Fathom row <c>5.0.128</c> <c>naturalkey-codec-consolidation</c>, WP-8).
///
/// Loads the shared vector file
/// <c>nodegraph-analyzer-conformance/fixtures/natural-key-codec/vectors.json</c>
/// (same fixture the TS conformance suite's
/// <c>natural-key-codec-vectors.test.ts</c> exercises) and asserts the C#
/// mirror reproduces every vector byte-exactly. This is the harness that
/// witnessed finding F1: the pre-WP-8 <c>MakeNaturalKey</c> was a bare
/// <c>'/' -&gt; ':'</c> substitution with no escaping, so every vector
/// containing a literal <c>:</c>/<c>\</c>/<c>#</c> failed.
///
/// SKIPS GRACEFULLY (loud notice via test output, not silent) when the
/// sibling <c>nodegraph-analyzer-conformance</c> checkout isn't present
/// on disk — a standalone checkout of this repo alone, with no
/// <c>nodegraph/</c> siblings.
/// </summary>

using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Abstractions;

namespace NodegraphAnalyzerDotnet.Tests;

public class NaturalKeyCodecTests
{
    private readonly ITestOutputHelper _output;

    public NaturalKeyCodecTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Assembly.Location = .../nodegraph-analyzer-dotnet/tests/bin/Debug/net9.0/<asm>.dll
    // 6 ups: net9.0/ → Debug/ → bin/ → tests/ → nodegraph-analyzer-dotnet/ → nodegraph/ → Fathom/
    // then down into nodegraph/nodegraph-analyzer-conformance/fixtures/natural-key-codec.
    // Precedent: CrossLangFixturesTests.cs:25.
    private static readonly string VectorsPath = Path.GetFullPath(
        Path.Combine(
            Path.GetDirectoryName(typeof(NaturalKeyCodecTests).Assembly.Location)!,
            "../../../../../../nodegraph/nodegraph-analyzer-conformance/fixtures/natural-key-codec/vectors.json"
        )
    );

    private record ComponentVector(string Raw, string Escaped);

    private record ElementKeyVector(
        [property: JsonPropertyName("artifactId")] string ArtifactId,
        [property: JsonPropertyName("elementName")] string ElementName,
        string Key
    );

    private record InjectivityPair(string[] A, string[] B, bool MustDiffer);

    private record VectorsFile(
        int SpecVersion,
        ComponentVector[] ComponentVectors,
        ElementKeyVector[] ElementKeyVectors,
        InjectivityPair[] InjectivityPairs
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private VectorsFile? LoadVectors()
    {
        if (!File.Exists(VectorsPath))
        {
            _output.WriteLine(
                $"SKIP: sibling vectors file not found at {VectorsPath} — " +
                "standalone checkout of nodegraph-analyzer-dotnet without " +
                "the nodegraph-analyzer-conformance sibling. Skipping " +
                "cross-implementation conformance check gracefully."
            );
            return null;
        }
        var json = File.ReadAllText(VectorsPath);
        return JsonSerializer.Deserialize<VectorsFile>(json, JsonOptions);
    }

    [Fact]
    public void VectorsFile_SpecVersion_IsTheVersionThisSuiteUnderstands()
    {
        var vectors = LoadVectors();
        if (vectors is null) return;
        Assert.Equal(1, vectors.SpecVersion);
    }

    [Fact]
    public void ComponentVectors_EscapeNaturalKeyComponent_ReproducesEveryEscapedValueByteExactly()
    {
        var vectors = LoadVectors();
        if (vectors is null) return;
        Assert.NotEmpty(vectors.ComponentVectors);

        var failures = new List<string>();
        foreach (var v in vectors.ComponentVectors)
        {
            var actual = NaturalKeyCodec.EscapeNaturalKeyComponent(v.Raw);
            if (actual != v.Escaped)
                failures.Add($"raw={JsonSerializer.Serialize(v.Raw)}: expected {JsonSerializer.Serialize(v.Escaped)}, got {JsonSerializer.Serialize(actual)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} component-vector mismatch(es):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void ElementKeyVectors_MakeNaturalKey_ReproducesEveryKeyValueByteExactly()
    {
        var vectors = LoadVectors();
        if (vectors is null) return;
        Assert.NotEmpty(vectors.ElementKeyVectors);

        var failures = new List<string>();
        foreach (var v in vectors.ElementKeyVectors)
        {
            var actual = NaturalKeyCodec.MakeNaturalKey(v.ArtifactId, v.ElementName);
            if (actual != v.Key)
                failures.Add($"artifactId={JsonSerializer.Serialize(v.ArtifactId)} elementName={JsonSerializer.Serialize(v.ElementName)}: expected {JsonSerializer.Serialize(v.Key)}, got {JsonSerializer.Serialize(actual)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} element-key-vector mismatch(es):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void InjectivityPairs_EveryPairMarkedMustDiffer_ActuallyDiffersUnderMakeNaturalKey()
    {
        var vectors = LoadVectors();
        if (vectors is null) return;
        Assert.NotEmpty(vectors.InjectivityPairs);

        var failures = new List<string>();
        foreach (var pair in vectors.InjectivityPairs)
        {
            var keyA = NaturalKeyCodec.MakeNaturalKey(pair.A[0], pair.A[1]);
            var keyB = NaturalKeyCodec.MakeNaturalKey(pair.B[0], pair.B[1]);
            var differs = keyA != keyB;
            if (pair.MustDiffer && !differs)
                failures.Add($"pair a=[{pair.A[0]},{pair.A[1]}] b=[{pair.B[0]},{pair.B[1]}] both encode to {JsonSerializer.Serialize(keyA)} — expected to differ");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} injectivity-pair failure(s):\n{string.Join("\n", failures)}");
    }
}
