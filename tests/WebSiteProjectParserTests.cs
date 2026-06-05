/// <summary>
/// Tests for Web Site Project detection from a `.sln` (Fathom row
/// <c>dotnet-web-site-project-support</c> 5.0.73). Fixture shape mirrors the
/// real EnvisionWeb `.sln`: two WSPs (`EnvisionAnywhere.com`,
/// `login.envisiongo.com`) plus a regular `.csproj` that must be ignored.
/// </summary>

namespace NodegraphAnalyzerDotnet.Tests;

public class WebSiteProjectParserTests
{
    // Project-type GUIDs: E24C... = Web Site Project; FAE0... = C# csproj.
    private const string Sln = """
Microsoft Visual Studio Solution File, Format Version 12.00
Project("{E24C65DC-7377-472B-9ABA-BC803B73C61A}") = "EnvisionAnywhere.com", "EnvisionAnywhere.com\", "{D0590350-E1CD-460D-9D5C-C0E223F2446E}"
	ProjectSection(WebsiteProperties) = preProject
		TargetFrameworkMoniker = ".NETFramework,Version%3Dv4.7.2"
		ProjectReferences = "{e27666e0-2cd0-4c6d-acfd-a72c38a9892c}|CloudCore.dll;"
		Debug.AspNetCompiler.VirtualPath = "/localhost_20538"
		Debug.AspNetCompiler.PhysicalPath = "EnvisionAnywhere.com\"
	EndProjectSection
EndProject
Project("{E24C65DC-7377-472B-9ABA-BC803B73C61A}") = "login.envisiongo.com", "login.envisiongo.com\", "{DEB12D26-67A1-4685-A5F0-10C1DB2863CD}"
	ProjectSection(WebsiteProperties) = preProject
		TargetFrameworkMoniker = ".NETFramework,Version%3Dv4.8"
		Debug.AspNetCompiler.PhysicalPath = "login.envisiongo.com\"
	EndProjectSection
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CloudCore", "CloudCore\CloudCore.csproj", "{E27666E0-2CD0-4C6D-ACFD-A72C38A9892C}"
EndProject
""";

    [Fact]
    public void Detects_both_web_site_projects_and_ignores_csproj()
    {
        var wsps = WebSiteProjectParser.Parse(Sln, "/repo");
        Assert.Equal(2, wsps.Count);
        Assert.Equal(new[] { "EnvisionAnywhere.com", "login.envisiongo.com" }, wsps.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void Resolves_physical_path_absolute_against_sln_dir()
    {
        var wsps = WebSiteProjectParser.Parse(Sln, "/repo");
        var ea = wsps.Single(w => w.Name == "EnvisionAnywhere.com");
        Assert.Equal(Path.GetFullPath("/repo/EnvisionAnywhere.com"), ea.PhysicalPath);
    }

    [Fact]
    public void Decodes_target_framework_moniker()
    {
        var wsps = WebSiteProjectParser.Parse(Sln, "/repo");
        Assert.Equal(".NETFramework,Version=v4.7.2", wsps.Single(w => w.Name == "EnvisionAnywhere.com").TargetFrameworkMoniker);
        Assert.Equal(".NETFramework,Version=v4.8", wsps.Single(w => w.Name == "login.envisiongo.com").TargetFrameworkMoniker);
    }

    [Fact]
    public void Parses_project_references_guid_and_output()
    {
        var wsps = WebSiteProjectParser.Parse(Sln, "/repo");
        var ea = wsps.Single(w => w.Name == "EnvisionAnywhere.com");
        var pr = Assert.Single(ea.ProjectReferences);
        Assert.Equal("e27666e0-2cd0-4c6d-acfd-a72c38a9892c", pr.ProjectGuid);
        Assert.Equal("CloudCore.dll", pr.OutputName);
        // A WSP with no ProjectReferences line → empty list, not null.
        Assert.Empty(wsps.Single(w => w.Name == "login.envisiongo.com").ProjectReferences);
    }

    [Fact]
    public void ParseProjectReferences_handles_multiple_and_blanks()
    {
        var refs = WebSiteProjectParser.ParseProjectReferences("{a1}|One.dll;{b2}|Two.dll; ; ");
        Assert.Equal(2, refs.Count);
        Assert.Equal("a1", refs[0].ProjectGuid);
        Assert.Equal("Two.dll", refs[1].OutputName);
    }

    [Fact]
    public void Empty_or_csproj_only_sln_yields_no_wsps()
    {
        var none = WebSiteProjectParser.Parse(
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"X\", \"X\\X.csproj\", \"{1}\"\nEndProject\n",
            "/repo");
        Assert.Empty(none);
    }
}

public class WebConfigParserTests
{
    [Fact]
    public void Extracts_declared_assembly_simple_names_and_skips_wildcard()
    {
        var webConfig = """
<configuration><system.web><compilation debug="true" targetFramework="4.7.2">
  <assemblies>
    <add assembly="System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=B77A5C561934E089" />
    <add assembly="Telerik.Reporting, Version=18.2.24.924, Culture=neutral, PublicKeyToken=a9d7983dfcc261be" />
    <add assembly="*" />
  </assemblies>
</compilation></system.web></configuration>
""";
        var names = WebConfigParser.ParseAssemblyNames(webConfig);
        Assert.Contains("System.Core", names);
        Assert.Contains("Telerik.Reporting", names);
        Assert.DoesNotContain("*", names);
        Assert.Equal(2, names.Count);
    }
}
