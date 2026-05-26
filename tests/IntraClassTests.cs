using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Intra-class edge extraction tests. Pins the LCOM4-input emission
/// rules in <c>Program.IntraClass.cs</c>. Per Fathom work row
/// <c>analyzers-intraclass-shadow</c> (2.2.4): bare-identifier matches
/// MUST suppress when a parameter or local variable shadows the
/// class member name; otherwise the cohesion graph over-counts.
/// </summary>
public class IntraClassTests
{
    private static List<(string Type, string? Subtype, string Target)> Extract(
        string classBody)
    {
        var code = $"class C {{ {classBody} }}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var typeDecl = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First();
        var index = IntraClassHelpers.BuildIndex(typeDecl);
        var edges = new List<(string, string?, string)>();
        foreach (var member in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            edges.AddRange(IntraClassHelpers.ExtractEdges(member, index));
        }
        return edges;
    }

    // ---------- Positive baselines (non-shadowed cases still fire) ----------

    [Fact]
    public void ThisField_Read_FiresAccessesFieldRead()
    {
        var edges = Extract(@"
            private int total;
            void M() { var x = this.total; }
        ");
        Assert.Contains(("accessesField", (string?)"read", "total"), edges);
    }

    [Fact]
    public void BareField_NoShadow_FiresAccessesFieldRead()
    {
        var edges = Extract(@"
            private int total;
            void M() { var x = total; }
        ");
        Assert.Contains(("accessesField", (string?)"read", "total"), edges);
    }

    [Fact]
    public void BareMethodCall_NoShadow_FiresCallsMethod()
    {
        var edges = Extract(@"
            void Reset() { }
            void M() { Reset(); }
        ");
        Assert.Contains(("callsMethod", (string?)null, "Reset"), edges);
    }

    // ---------- Shadowing cases (these should NOT fire) ----------

    [Fact]
    public void Parameter_ShadowsField_DoesNotFireAccessesField()
    {
        // Parameter named `count` shadows field `count`.
        // The assignment writes the parameter, not the field.
        var edges = Extract(@"
            private int count;
            void M(int count) { count = 0; }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "accessesField" && e.Target == "count");
    }

    [Fact]
    public void Parameter_ShadowsMethod_DoesNotFireCallsMethod()
    {
        // Parameter `Reset` is a delegate; calling it does NOT
        // invoke the same-named method on the class.
        var edges = Extract(@"
            void Reset() { }
            void M(System.Action Reset) { Reset(); }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "callsMethod" && e.Target == "Reset");
    }

    [Fact]
    public void LocalVariable_ShadowsField_DoesNotFireAccessesField()
    {
        var edges = Extract(@"
            private int total;
            void M() {
                int total = 5;
                var x = total;
            }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "accessesField" && e.Target == "total");
    }

    [Fact]
    public void LocalVariable_ShadowsMethod_DoesNotFireCallsMethod()
    {
        var edges = Extract(@"
            void Reset() { }
            void M() {
                System.Action Reset = () => { };
                Reset();
            }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "callsMethod" && e.Target == "Reset");
    }

    [Fact]
    public void ForEachVariable_ShadowsField_DoesNotFireAccessesField()
    {
        var edges = Extract(@"
            private int item;
            void M(System.Collections.Generic.IEnumerable<int> xs) {
                foreach (var item in xs) {
                    var x = item;
                }
            }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "accessesField" && e.Target == "item");
    }

    [Fact]
    public void CatchVariable_ShadowsField_DoesNotFireAccessesField()
    {
        var edges = Extract(@"
            private System.Exception ex;
            void M() {
                try { } catch (System.Exception ex) {
                    var x = ex;
                }
            }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "accessesField" && e.Target == "ex");
    }

    [Fact]
    public void PatternVariable_ShadowsField_DoesNotFireAccessesField()
    {
        var edges = Extract(@"
            private string name;
            void M(object o) {
                if (o is string name) {
                    var x = name;
                }
            }
        ");
        Assert.DoesNotContain(
            edges,
            e => e.Type == "accessesField" && e.Target == "name");
    }

    // ---------- Negative: `this.X` still fires even under shadow ----------

    [Fact]
    public void ThisField_FiresEvenWhenParameterShadows()
    {
        // The whole point of `this.` is to disambiguate; if the
        // analyzer suppressed `this.count` because `count` is a
        // parameter name, the fix would have over-corrected.
        var edges = Extract(@"
            private int count;
            void M(int count) { this.count = count; }
        ");
        Assert.Contains(("accessesField", (string?)"write", "count"), edges);
    }
}
