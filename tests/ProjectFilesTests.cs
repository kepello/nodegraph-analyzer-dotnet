using System.Text.Json;

namespace NodegraphAnalyzerDotnet.Tests;

/// <summary>
/// Tests for .csproj and .sln structural artifact emission
/// (Program.ProjectFiles.cs). Per testing rule 6: decision-making logic
/// covers positive AND negative cases.
/// </summary>
public class ProjectFilesTests
{
    private static JsonElement BuildArtifact(object? artifact)
    {
        var json = JsonSerializer.Serialize(artifact);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // ----- .csproj ----------------------------------------------------------

    [Fact]
    public void Csproj_BasicSdkProject_EmitsProjectElementWithMetadata()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var artifact = ProjectFileHelpers.BuildCsprojArtifact(content, "/repo/MyApp.csproj");
        Assert.NotNull(artifact);
        var json = BuildArtifact(artifact);
        Assert.Equal("csproj", json.GetProperty("language").GetString());
        Assert.Equal("/repo/MyApp.csproj", json.GetProperty("filePath").GetString());

        var elements = json.GetProperty("elements");
        Assert.Equal(1, elements.GetArrayLength());
        var project = elements[0];
        Assert.Equal("MyApp", project.GetProperty("name").GetString());
        Assert.Equal("project", project.GetProperty("kind").GetString());

        var metadata = project.GetProperty("metadata");
        Assert.Equal("Microsoft.NET.Sdk", metadata.GetProperty("sdk").GetString());
        Assert.Equal("Exe", metadata.GetProperty("outputType").GetString());
        var targetFrameworks = metadata.GetProperty("targetFrameworks");
        Assert.Equal(1, targetFrameworks.GetArrayLength());
        Assert.Equal("net9.0", targetFrameworks[0].GetString());
    }

    [Fact]
    public void Csproj_PackageReferences_EmitReferencesEdges()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """;
        var artifact = ProjectFileHelpers.BuildCsprojArtifact(content, "/repo/MyApp.csproj");
        var json = BuildArtifact(artifact);
        var edges = json.GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());

        var packageEdges = new List<string>();
        for (int i = 0; i < edges.GetArrayLength(); i++)
        {
            var e = edges[i];
            Assert.Equal("MyApp", e.GetProperty("sourceName").GetString());
            Assert.Equal("references", e.GetProperty("type").GetString());
            Assert.Equal("package", e.GetProperty("subtype").GetString());
            packageEdges.Add(e.GetProperty("targetName").GetString()!);
        }
        Assert.Contains("Newtonsoft.Json@13.0.3", packageEdges);
        Assert.Contains("Serilog@3.1.1", packageEdges);
    }

    [Fact]
    public void Csproj_ProjectReferences_ResolveRelativePathsToAbsolute()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """;
        var artifact = ProjectFileHelpers.BuildCsprojArtifact(content, "/repo/src/MyApp.csproj");
        var json = BuildArtifact(artifact);
        var edges = json.GetProperty("edges");
        Assert.Equal(1, edges.GetArrayLength());
        var edge = edges[0];
        Assert.Equal("references", edge.GetProperty("type").GetString());
        Assert.Equal("project", edge.GetProperty("subtype").GetString());
        // Backslash normalized to forward slash; relative path resolved
        // against the .csproj's own directory.
        Assert.Equal("/repo/Lib/Lib.csproj", edge.GetProperty("targetName").GetString());
    }

    [Fact]
    public void Csproj_MultiTargetFrameworks_AllListed()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net9.0;net8.0;netstandard2.1</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """;
        var artifact = ProjectFileHelpers.BuildCsprojArtifact(content, "/repo/Lib.csproj");
        var json = BuildArtifact(artifact);
        var targetFrameworks = json.GetProperty("elements")[0]
            .GetProperty("metadata")
            .GetProperty("targetFrameworks");
        Assert.Equal(3, targetFrameworks.GetArrayLength());
    }

    [Fact]
    public void Csproj_Malformed_EmitsArtifactWithProblems()
    {
        var content = "<Project>not closed";
        var artifact = ProjectFileHelpers.BuildCsprojArtifact(content, "/repo/Bad.csproj");
        var json = BuildArtifact(artifact);
        Assert.Equal("csproj", json.GetProperty("language").GetString());
        Assert.True(json.TryGetProperty("problems", out var problems));
        Assert.True(problems.GetArrayLength() > 0);
    }

    // ----- .sln -------------------------------------------------------------

    [Fact]
    public void Sln_WithProjects_EmitsContainsEdges()
    {
        var content = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "lib\Lib.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            EndGlobal
            """;
        var artifact = ProjectFileHelpers.BuildSlnArtifact(content, "/repo/MyApp.sln");
        Assert.NotNull(artifact);
        var json = BuildArtifact(artifact);
        Assert.Equal("sln", json.GetProperty("language").GetString());

        var element = json.GetProperty("elements")[0];
        Assert.Equal("MyApp", element.GetProperty("name").GetString());
        Assert.Equal("solution", element.GetProperty("kind").GetString());
        Assert.Equal(2, element.GetProperty("metadata").GetProperty("projectCount").GetInt32());
        Assert.Equal("12.00", element.GetProperty("metadata").GetProperty("formatVersion").GetString());

        var edges = json.GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());
        var targets = new List<string>();
        for (int i = 0; i < edges.GetArrayLength(); i++)
        {
            var e = edges[i];
            Assert.Equal("MyApp", e.GetProperty("sourceName").GetString());
            Assert.Equal("contains", e.GetProperty("type").GetString());
            targets.Add(e.GetProperty("targetName").GetString()!);
        }
        Assert.Contains("/repo/src/App.csproj", targets);
        Assert.Contains("/repo/lib/Lib.csproj", targets);
    }

    [Fact]
    public void Sln_SolutionFolderEntries_NotEmittedAsContainsEdges()
    {
        // Solution folders look like Project lines but reference
        // themselves (no .csproj extension) — must be ignored.
        var content = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "SolutionItems", "SolutionItems", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """;
        var artifact = ProjectFileHelpers.BuildSlnArtifact(content, "/repo/MyApp.sln");
        var json = BuildArtifact(artifact);
        var edges = json.GetProperty("edges");
        Assert.Equal(1, edges.GetArrayLength());
        Assert.Equal("/repo/App.csproj", edges[0].GetProperty("targetName").GetString());
    }

    [Fact]
    public void Sln_Empty_EmitsArtifactWithSolutionElementOnly()
    {
        var content = "Microsoft Visual Studio Solution File, Format Version 12.00\n";
        var artifact = ProjectFileHelpers.BuildSlnArtifact(content, "/repo/Empty.sln");
        var json = BuildArtifact(artifact);
        Assert.Equal(1, json.GetProperty("elements").GetArrayLength());
        // No edges when no projects referenced.
        Assert.False(json.TryGetProperty("edges", out _));
    }
}
