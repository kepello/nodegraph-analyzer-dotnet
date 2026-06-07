/// <summary>
/// MSBuildWorkspace integration tests. Pins the load + cross-boundary
/// resolution semantics that Phase 2 of Fathom row
/// <c>dotnet-csproj-sln-handling</c> (1.11.15) introduces.
///
/// Test surface coverage per `feedback_test_fixture_pattern_catalog`:
///   1. Positive load — restored fixture opens cleanly.
///   2. Broken csproj — emits problem, falls through.
///   3. Cross-project resolution — ProjectReference symbols resolve to
///      the referenced project's source file.
///   4. Cross-package resolution — PackageReference symbols resolve to
///      a metadata-only symbol (no DeclaringSyntaxReferences). Pins the
///      "external symbol → no edge" invariant the existing Program.cs
///      resolver depends on (ResolveCallTarget returns null when
///      DeclaringSyntaxReferences is empty → no edge emitted).
///   5. Orphan fallback — a .cs not under any csproj's directory is
///      NOT in the loaded map, so Program.cs falls back to the existing
///      sharedCompilation path.
///
/// Tests 1 / 3 / 4 require `dotnet restore` to have produced
/// `obj/project.assets.json` for the fixture's App.csproj. Skipped when
/// MSBuild can't be located on the host (CI without .NET SDK).
/// </summary>

using System.Reflection;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NodegraphAnalyzerDotnet.Tests;

public class MSBuildIntegrationTests
{
    // Assembly.Location = .../nodegraph-analyzer-dotnet/tests/bin/Debug/net9.0/<asm>.dll
    // 6 ups → Fathom/; then into fathom/fathom-test-fixtures/dotnet-msbuild/
    private static readonly string FixtureDir = Path.GetFullPath(
        Path.Combine(
            Path.GetDirectoryName(typeof(MSBuildIntegrationTests).Assembly.Location)!,
            "../../../../../../fathom/fathom-test-fixtures/dotnet-msbuild"
        )
    );

    private static string AppCsproj => Path.Combine(FixtureDir, "App", "App.csproj");
    private static string LibCsproj => Path.Combine(FixtureDir, "Lib", "Lib.csproj");
    private static string WorkerCs => Path.Combine(FixtureDir, "App", "Worker.cs");
    private static string HelperCs => Path.Combine(FixtureDir, "Lib", "Helper.cs");
    private static string OrphanCs => Path.Combine(FixtureDir, "Orphan.cs");

    private static bool IsFixtureRestored() =>
        File.Exists(Path.Combine(FixtureDir, "App", "obj", "project.assets.json"))
        && File.Exists(Path.Combine(FixtureDir, "Lib", "obj", "project.assets.json"));

    private static bool RegisterMsbuild()
    {
        // Idempotent — MSBuildLocator throws on second register; swallow.
        if (MSBuildLocator.IsRegistered) return true;
        try
        {
            MSBuildLocator.RegisterDefaults();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 1. Positive load ----------

    [Fact]
    public void LoadProjects_RestoredFixture_ReturnsNonEmptyMap()
    {
        if (!IsFixtureRestored())
        {
            Assert.Fail($"Fixture not restored. Run {FixtureDir}/setup.sh first.");
        }
        if (!RegisterMsbuild())
        {
            // No MSBuild on host — skip gracefully.
            return;
        }

        var problems = new List<object>();
        var map = MSBuildIntegration.LoadProjects(new[] { AppCsproj, LibCsproj }, problems);

        Assert.NotEmpty(map);
        // App.csproj contributes both Worker.cs and Program.cs;
        // Lib.csproj contributes Helper.cs. Each is keyed by its
        // absolute path (case-sensitive on macOS/Linux).
        Assert.True(map.ContainsKey(WorkerCs), $"map missing Worker.cs key={WorkerCs}");
        Assert.True(map.ContainsKey(HelperCs), $"map missing Helper.cs key={HelperCs}");
        Assert.Empty(problems);
    }

    // ---------- 2. Broken csproj ----------

    [Fact]
    public void LoadProjects_MalformedCsproj_EmitsProblemAndSkipsProject()
    {
        if (!RegisterMsbuild())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "fathom-msbuild-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var brokenCsproj = Path.Combine(tempDir, "Broken.csproj");
            File.WriteAllText(brokenCsproj, "<Project>not closed");

            var problems = new List<object>();
            var map = MSBuildIntegration.LoadProjects(new[] { brokenCsproj }, problems);

            Assert.Empty(map);
            Assert.NotEmpty(problems);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------- 3. Cross-project resolution ----------

    [Fact]
    public void LoadProjects_CrossProjectCall_ResolvesToReferencedProjectFile()
    {
        if (!IsFixtureRestored())
        {
            Assert.Fail($"Fixture not restored. Run {FixtureDir}/setup.sh first.");
        }
        if (!RegisterMsbuild())
        {
            return;
        }

        var problems = new List<object>();
        var map = MSBuildIntegration.LoadProjects(new[] { AppCsproj, LibCsproj }, problems);

        Assert.True(map.ContainsKey(WorkerCs));
        var (compilation, workerTree) = map[WorkerCs];
        var workerModel = compilation.GetSemanticModel(workerTree);

        // Find the `helper.DoThing()` invocation in Worker.Run().
        var invocations = workerTree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToList();
        var doThingCall = invocations.FirstOrDefault(inv =>
            inv.Expression is MemberAccessExpressionSyntax mae
            && mae.Name.Identifier.Text == "DoThing");
        Assert.NotNull(doThingCall);

        var symbolInfo = workerModel.GetSymbolInfo(doThingCall!.Expression);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        Assert.NotNull(symbol);
        Assert.IsAssignableFrom<IMethodSymbol>(symbol);

        var declRefs = symbol!.DeclaringSyntaxReferences;
        Assert.NotEmpty(declRefs);
        // The declaring file is Lib/Helper.cs — absolute path match.
        Assert.Equal(HelperCs, declRefs[0].SyntaxTree.FilePath);
    }

    // ---------- 4. Cross-package resolution (external) ----------

    [Fact]
    public void LoadProjects_CrossPackageCall_ResolvesAsExternalMetadataOnlySymbol()
    {
        if (!IsFixtureRestored())
        {
            Assert.Fail($"Fixture not restored. Run {FixtureDir}/setup.sh first.");
        }
        if (!RegisterMsbuild())
        {
            return;
        }

        var problems = new List<object>();
        var map = MSBuildIntegration.LoadProjects(new[] { AppCsproj, LibCsproj }, problems);

        var (compilation, workerTree) = map[WorkerCs];
        var workerModel = compilation.GetSemanticModel(workerTree);

        // Find `JsonConvert.SerializeObject(...)` invocation.
        var invocations = workerTree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToList();
        var serializeCall = invocations.FirstOrDefault(inv =>
            inv.Expression is MemberAccessExpressionSyntax mae
            && mae.Name.Identifier.Text == "SerializeObject");
        Assert.NotNull(serializeCall);

        var symbolInfo = workerModel.GetSymbolInfo(serializeCall!.Expression);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        // PackageReference resolution succeeds: symbol is bound.
        Assert.NotNull(symbol);
        Assert.IsAssignableFrom<IMethodSymbol>(symbol);

        // External symbols (from referenced assemblies) have no
        // DeclaringSyntaxReferences — they're metadata-only. Pins the
        // invariant Program.cs's ResolveCallTarget depends on: external
        // calls return null (declRefs.Length == 0) and no edge is
        // emitted into the substrate.
        Assert.Empty(symbol!.DeclaringSyntaxReferences);
    }

    // ---------- 6. Old-style csproj bare framework ref (5.0.76.a) ----------

    [Fact]
    public void LoadProjects_OldStyleCsproj_BareFrameworkRef_ResolvesSystemDataOnMacLinux()
    {
        // Fathom row dotnet-l0-external-symbol-resolution-residual 5.0.76.a.
        // An OLD-STYLE (non-SDK) .NET Framework csproj references System.Data as
        // a BARE GAC reference (no HintPath) — the EnvisionWeb ReportLib shape.
        // On macOS/Linux there's no GAC, and old-style csprojs don't auto-import
        // the Microsoft.NETFramework.ReferenceAssemblies pack, so System.Data was
        // an error symbol → `this._t.Rows.Add(r)` didn't resolve → no edge → the
        // method fell to `unclassified`. The fix feeds MSBuildWorkspace the pack
        // via TargetFrameworkRootPath so MSBuild's native RAR resolves it.
        if (OperatingSystem.IsWindows()) return; // Windows resolves bare refs from the GAC — N/A.
        if (!RegisterMsbuild()) return;          // no MSBuild on host — skip.
        // Host-conditional: only meaningful when the net48 reference-assemblies
        // pack is actually restored (it is on this dev machine; CI without it skips).
        if (FrameworkReferenceResolver.ResolveReferenceAssemblyDir(
                ".NETFramework,Version=v4.8", FrameworkReferenceResolver.GlobalPackagesDir()) == null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "fathom-oldcsproj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Old.csproj"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Project ToolsVersion=\"12.0\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <Import Project=\"$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\" Condition=\"Exists('$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props')\" />\n" +
                "  <PropertyGroup>\n" +
                "    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>\n" +
                "    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>\n" +
                "    <OutputType>Library</OutputType>\n" +
                "    <RootNamespace>Old</RootNamespace>\n" +
                "    <AssemblyName>Old</AssemblyName>\n" +
                "    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n" +
                "  </PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Reference Include=\"System\" />\n" +
                "    <Reference Include=\"System.Data\" />\n" +
                "  </ItemGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Compile Include=\"Box.cs\" />\n" +
                "  </ItemGroup>\n" +
                "  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />\n" +
                "</Project>\n");
            var boxCs = Path.Combine(dir, "Box.cs");
            File.WriteAllText(boxCs,
                "using System.Data;\n" +
                "public class Box {\n" +
                "    private DataTable _t = new DataTable();\n" +
                "    public void Add(DataRow r) { this._t.Rows.Add(r); }\n" +
                "}\n");

            var problems = new List<object>();
            var map = MSBuildIntegration.LoadProjects(new[] { Path.Combine(dir, "Old.csproj") }, problems);

            Assert.True(map.ContainsKey(boxCs), "Box.cs not loaded from the old-style csproj");
            var (compilation, tree) = map[boxCs];
            var model = compilation.GetSemanticModel(tree);

            // `this._t.Rows.Add(r)` must resolve to System.Data.DataRowCollection.Add
            // (an external metadata symbol). Pre-fix, System.Data is an error symbol
            // and GetSymbolInfo().Symbol is null → the call edge is never emitted.
            var addCall = tree.GetRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .First(inv => inv.Expression is MemberAccessExpressionSyntax m
                    && m.Name.Identifier.Text == "Add");
            var symbol = model.GetSymbolInfo(addCall.Expression).Symbol;
            Assert.NotNull(symbol); // System.Data resolved
            Assert.IsAssignableFrom<IMethodSymbol>(symbol);
            Assert.Equal("DataRowCollection", symbol!.ContainingType?.Name);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------- 5. Orphan fallback ----------

    [Fact]
    public void LoadProjects_FileNotUnderAnyCsproj_NotInLoadedMap()
    {
        if (!IsFixtureRestored())
        {
            Assert.Fail($"Fixture not restored. Run {FixtureDir}/setup.sh first.");
        }
        if (!RegisterMsbuild())
        {
            return;
        }
        // Orphan.cs lives at fixture root, not under Lib/ or App/.
        // SDK-style projects auto-include only files under their own
        // directory, so neither csproj's Documents contain Orphan.cs.
        Assert.True(File.Exists(OrphanCs), $"fixture orphan missing: {OrphanCs}");

        var problems = new List<object>();
        var map = MSBuildIntegration.LoadProjects(new[] { AppCsproj, LibCsproj }, problems);

        // Sanity: load actually succeeded (WorkerCs IS present), so the
        // !ContainsKey(OrphanCs) assertion isn't passing vacuously on an
        // empty map.
        Assert.True(map.ContainsKey(WorkerCs), "load failed — Worker.cs absent from map");
        Assert.False(map.ContainsKey(OrphanCs),
            "Orphan.cs must NOT be in the loaded map — Program.cs's sharedCompilation fallback is what analyzes it.");
    }
}
