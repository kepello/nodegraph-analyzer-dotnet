/// <summary>
/// MSBuildWorkspace integration for the .NET analyzer. Opens discovered
/// .csproj files via the real MSBuild + Roslyn workspace API so the
/// per-.cs SemanticModel resolves PackageReference (NuGet) and
/// ProjectReference (cross-project) symbols natively — not just System.
///
/// Phase 2 of Fathom row <c>dotnet-csproj-sln-handling</c> (1.11.15).
/// Replaces the System-runtime-only Compilation in <c>Program.RunOutput</c>
/// when a .cs file maps to a loaded project; falls through to the existing
/// sharedCompilation path for orphan files (files not under any csproj's
/// directory, hosts without .NET SDK, broken csproj that failed to load).
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

static class MSBuildIntegration
{
    /// <summary>
    /// Open each .csproj via MSBuildWorkspace, obtain its Compilation,
    /// and produce a per-document map: absolute file path → (Compilation,
    /// SyntaxTree). The Compilation is shared across all of a project's
    /// documents; each Document's SyntaxTree is its own.
    ///
    /// Failures per-project (broken syntax, missing assets, unsupported
    /// SDK) emit a problem to <paramref name="problems"/> and fall
    /// through. Other projects continue loading. Returns an empty map
    /// when every project fails.
    ///
    /// Caller is responsible for having called
    /// <c>MSBuildLocator.RegisterDefaults()</c> exactly once before the
    /// first invocation. The analyzer's <c>Program.cs</c> entry-point
    /// does this; the test harness uses an <c>IsRegistered</c> guard for
    /// the same effect across multiple test invocations.
    /// </summary>
    public static Dictionary<string, (Compilation Compilation, SyntaxTree SyntaxTree)>
        LoadProjects(IReadOnlyList<string> csprojPaths, List<object> problems)
    {
        var map = new Dictionary<string, (Compilation, SyntaxTree)>();

        // Single workspace shared across all .csproj — MSBuildWorkspace
        // memoizes references, so opening sibling ProjectReference
        // projects twice (once standalone, once transitively) hits cache.
        MSBuildWorkspace workspace;
        try
        {
            workspace = MSBuildWorkspace.Create();
        }
        catch (Exception ex)
        {
            problems.Add(new
            {
                severity = "error",
                message = $"MSBuildWorkspace.Create failed: {ex.Message}",
            });
            return map;
        }

        // Workspace-level diagnostics fire continuously; collect them but
        // don't halt per-project loading on warnings.
        workspace.WorkspaceFailed += (_, args) =>
        {
            // Failures (vs warnings) get surfaced as problems; warnings
            // are noise on most real workspaces (missing optional analyzers,
            // SDK version skew) and would drown the wire.
            if (args.Diagnostic.Kind == Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
            {
                problems.Add(new
                {
                    severity = "warning",
                    message = $"MSBuildWorkspace: {args.Diagnostic.Message}",
                });
            }
        };

        // MSBuildWorkspace auto-loads ProjectReference siblings into the
        // same Solution on the first OpenProjectAsync, then throws
        // "<name> is already part of the workspace" on an explicit
        // second open for the same project. Treat that exception as
        // a benign no-op (the project is already loaded transitively).
        foreach (var csprojPath in csprojPaths)
        {
            try
            {
                workspace.OpenProjectAsync(csprojPath).GetAwaiter().GetResult();
            }
            catch (ArgumentException ex) when (ex.Message.Contains("already part of the workspace"))
            {
                // Already loaded via a transitive ProjectReference. Fine.
            }
            catch (Exception ex)
            {
                problems.Add(new
                {
                    severity = "warning",
                    message = $"Failed to load project {csprojPath}: {ex.Message}",
                });
            }
        }

        // Map every document of every loaded project. Transitively-loaded
        // sibling projects show up here even when their .csproj path
        // wasn't in csprojPaths, which matches the analyzer's intent:
        // any .cs file the workspace can resolve gets the workspace's
        // Compilation.
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            Compilation? compilation;
            try
            {
                compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                problems.Add(new
                {
                    severity = "warning",
                    message = $"GetCompilationAsync failed for {project.FilePath ?? project.Name}: {ex.Message}",
                });
                continue;
            }
            if (compilation == null)
            {
                problems.Add(new
                {
                    severity = "warning",
                    message = $"Project produced no Compilation: {project.FilePath ?? project.Name}",
                });
                continue;
            }

            foreach (var document in project.Documents)
            {
                var filePath = document.FilePath;
                if (string.IsNullOrEmpty(filePath)) continue;
                var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                if (tree == null) continue;
                // Last-write wins across projects (a file claimed by
                // two .csprojs gets the later compilation; .NET's
                // own MSBuild evaluator complains via WorkspaceFailed
                // when this happens for non-shared-project setups).
                map[filePath] = (compilation, tree);
            }
        }

        return map;
    }
}
