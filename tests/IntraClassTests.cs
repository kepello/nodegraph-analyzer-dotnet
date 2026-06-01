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
        => ExtractFull(classBody).Edges;

    private static (List<(string Type, string? Subtype, string Target)> Edges,
        List<IntraClassHelpers.AmbiguousCall> Ambiguous) ExtractFull(string classBody)
    {
        var code = $"class C {{ {classBody} }}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var typeDecl = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First();
        var index = IntraClassHelpers.BuildIndex(typeDecl);
        var edges = new List<(string, string?, string)>();
        var ambiguous = new List<IntraClassHelpers.AmbiguousCall>();
        foreach (var member in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var result = IntraClassHelpers.ExtractEdges(member, index);
            edges.AddRange(result.Edges);
            ambiguous.AddRange(result.AmbiguousCalls);
        }
        return (edges, ambiguous);
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
        Assert.Contains(("callsMethod", (string?)null, "Reset()"), edges);
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
            e => e.Type == "callsMethod" && e.Target == "Reset()");
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
            e => e.Type == "callsMethod" && e.Target == "Reset()");
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

    // ---------- callsMethod signature resolution (Fathom 5.0.68.1) ----------

    [Fact]
    public void CallsMethod_WithParams_EmitsSignaturedTarget()
    {
        // A same-class call to a method with parameters must carry the
        // parameter signature so the target matches the method element key
        // (`process(int,string)` → canonical `process-int-string`). The
        // pre-fix bare `process` could only bind to a zero-arg method.
        var edges = Extract(@"
            void Process(int a, string b) { }
            void M() { Process(1, ""x""); }
        ");
        Assert.Contains(("callsMethod", (string?)null, "Process(int,string)"), edges);
        Assert.DoesNotContain(("callsMethod", (string?)null, "Process"), edges);
    }

    [Fact]
    public void CallsMethod_DifferentArityOverloads_ResolvesByArgCount()
    {
        // Two overloads of different arity: the call's argument count selects
        // exactly one. Each call site resolves to its matching overload.
        var edges = Extract(@"
            void Log(string m) { }
            void Log(string m, int level) { }
            void M() { Log(""a""); Log(""b"", 3); }
        ");
        Assert.Contains(("callsMethod", (string?)null, "Log(string)"), edges);
        Assert.Contains(("callsMethod", (string?)null, "Log(string,int)"), edges);
    }

    [Fact]
    public void CallsMethod_SameArityOverloads_EmitsAmbiguityNotEdge()
    {
        // Two overloads of the SAME arity differing only by type cannot be
        // disambiguated syntactically → ambiguity surfaced, no guessed edge
        // (Trade-off dotnet-callsmethod-overload-ambiguity 2.2.17).
        var result = ExtractFull(@"
            void Send(int x) { }
            void Send(string x) { }
            void M() { Send(""hi""); }
        ");
        Assert.DoesNotContain(result.Edges, e => e.Type == "callsMethod" && e.Target.StartsWith("Send"));
        Assert.Contains(result.Ambiguous, a => a.MethodName == "Send" && a.OverloadCount == 2);
    }
}
