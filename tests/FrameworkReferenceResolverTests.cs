/// <summary>
/// Tests for per-TFM .NET Framework reference-assembly resolution (Fathom row
/// <c>dotnet-web-site-project-support</c> 5.0.73). The TFM→pack mapping is pure;
/// directory resolution is tested against a synthetic pack layout, plus a
/// host-conditional check against the real NuGet cache (skipped when the pack
/// isn't restored, so CI without it stays green).
/// </summary>

namespace NodegraphAnalyzerDotnet.Tests;

public class FrameworkReferenceResolverTests
{
    [Theory]
    [InlineData(".NETFramework,Version=v4.8", "microsoft.netframework.referenceassemblies.net48", "v4.8")]
    [InlineData(".NETFramework,Version=v4.7.2", "microsoft.netframework.referenceassemblies.net472", "v4.7.2")]
    [InlineData(".NETFramework,Version=v4.6.1", "microsoft.netframework.referenceassemblies.net461", "v4.6.1")]
    public void Maps_netframework_tfm_to_pack_id_and_version_dir(string tfm, string packId, string versionDir)
    {
        var parsed = FrameworkReferenceResolver.ParseNetFrameworkTfm(tfm);
        Assert.NotNull(parsed);
        Assert.Equal(packId, parsed!.Value.PackageId);
        Assert.Equal(versionDir, parsed.Value.VersionDir);
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v8.0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Returns_null_for_non_netframework_or_unparsable(string? tfm)
    {
        Assert.Null(FrameworkReferenceResolver.ParseNetFrameworkTfm(tfm));
    }

    [Fact]
    public void Resolves_newest_pack_version_dir_from_a_synthetic_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "fwref-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Two restored versions; resolver must pick the newest (1.0.3).
            foreach (var v in new[] { "1.0.0", "1.0.3" })
            {
                var dir = Path.Combine(root, "microsoft.netframework.referenceassemblies.net48", v, "build", ".NETFramework", "v4.8");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "System.Web.dll"), "");
            }
            var resolved = FrameworkReferenceResolver.ResolveReferenceAssemblyDir(".NETFramework,Version=v4.8", root);
            Assert.NotNull(resolved);
            Assert.Contains("1.0.3", resolved!);
            Assert.True(File.Exists(Path.Combine(resolved, "System.Web.dll")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Returns_null_when_pack_not_restored()
    {
        var root = Path.Combine(Path.GetTempPath(), "fwref-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(FrameworkReferenceResolver.ResolveReferenceAssemblyDir(".NETFramework,Version=v4.8", root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Real_cache_net48_resolves_with_system_web_when_restored()
    {
        // Host-conditional: only asserts when the net48 pack is actually
        // restored (it is on this dev machine; CI without it skips the body).
        var dir = FrameworkReferenceResolver.ResolveReferenceAssemblyDir(
            ".NETFramework,Version=v4.8", FrameworkReferenceResolver.GlobalPackagesDir());
        if (dir == null) return; // pack not restored on this host — nothing to assert
        Assert.True(File.Exists(Path.Combine(dir, "System.Web.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "mscorlib.dll")));
        Assert.NotEmpty(FrameworkReferenceResolver.ReferenceAssemblyDlls(dir));
    }
}
