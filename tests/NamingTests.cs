namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Canonicalization rule tests. Pins the per-segment
/// <see cref="NamingHelpers.Canonicalize"/> behavior — especially the
/// underscore-preservation rule from Fathom row 5.2.1
/// (<c>dotnet-canonical-name-underscore-collision</c>).
/// </summary>
public class NamingTests
{
    // ---------- Underscore preservation (5.2.1) ----------

    [Fact]
    public void LeadingUnderscoreField_StaysDistinctFromUnderscorelessForm()
    {
        Assert.NotEqual(
            NamingHelpers.Canonicalize("_field"),
            NamingHelpers.Canonicalize("Field"));
    }

    [Fact]
    public void LeadingUnderscore_Preserved()
    {
        Assert.Equal("_field", NamingHelpers.Canonicalize("_field"));
    }

    [Fact]
    public void DunderUnderscore_Preserved()
    {
        // Python-style dunders happen in tests / fixtures even in C# codebases.
        Assert.Equal("__init__", NamingHelpers.Canonicalize("__init__"));
    }

    [Fact]
    public void TrailingUnderscore_Preserved()
    {
        Assert.Equal("field_", NamingHelpers.Canonicalize("field_"));
    }

    [Fact]
    public void EnvironmentExample_DistinctFromUnderscoreless()
    {
        // The PNP/Utilities customer case: `_environment` vs `Environment`.
        Assert.NotEqual(
            NamingHelpers.Canonicalize("_environment"),
            NamingHelpers.Canonicalize("Environment"));
    }

    // ---------- Baseline behavior (regression pins) ----------

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NamingHelpers.Canonicalize(""));
    }

    [Fact]
    public void PascalCase_Lowercases()
    {
        Assert.Equal("foobar", NamingHelpers.Canonicalize("FooBar"));
    }

    [Fact]
    public void DotSeparator_BecomesDash()
    {
        Assert.Equal("foo-bar", NamingHelpers.Canonicalize("Foo.Bar"));
    }

    [Fact]
    public void Parens_AndCommas_CollapseToDashes()
    {
        Assert.Equal("method-int-string", NamingHelpers.Canonicalize("Method(int,string)"));
    }

    [Fact]
    public void Digits_Preserved()
    {
        Assert.Equal("foo123", NamingHelpers.Canonicalize("Foo123"));
    }

    [Fact]
    public void TrailingPunctuation_TrimmedToSingleSegment()
    {
        Assert.Equal("foo", NamingHelpers.Canonicalize("foo..."));
    }

    [Fact]
    public void RunsOfPunctuation_CollapseToSingleDash()
    {
        Assert.Equal("a-b", NamingHelpers.Canonicalize("a...b"));
    }
}
