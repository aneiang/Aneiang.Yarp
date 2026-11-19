using Aneiang.Yarp.Dashboard.Infrastructure.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-01: a plugin id is a directory name, and installation deletes the target directory before
/// copying into it. These tests pin both halves — the id is rejected, and nothing outside the
/// plugin root is touched.
/// </summary>
public sealed class PluginPathTraversalTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(), "aneiang-plugin-" + Guid.NewGuid().ToString("N"));

    private string PluginRoot => Path.Combine(_sandbox, "plugins");

    public PluginPathTraversalTests()
    {
        Directory.CreateDirectory(PluginRoot);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../etc")]
    [InlineData("/abs")]
    [InlineData("a/../../b")]
    [InlineData("..")]
    [InlineData("C:\\Windows")]
    [InlineData("UPPER")]
    public void A_manifest_id_that_is_not_a_single_safe_segment_is_rejected(string id)
    {
        var source = WritePlugin(id);

        var installed = Host().TryInstall(source, out var error);

        Assert.False(installed);
        Assert.NotNull(error);
        Assert.Contains("not a valid plugin id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_traversing_id_does_not_create_or_delete_anything_outside_the_root()
    {
        // The dangerous part is not the write, it is the recursive delete that happens first.
        var victim = Path.Combine(_sandbox, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep me");

        var source = WritePlugin("../victim");

        Assert.False(Host().TryInstall(source, out _));

        Assert.True(Directory.Exists(victim));
        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")));
    }

    [Fact]
    public void A_valid_id_is_still_installed()
    {
        // The regex must not be so tight that normal ids stop working.
        var source = WritePlugin("acme.waf_v2-beta");

        var installed = Host().TryInstall(source, out var error);

        Assert.True(installed, error);
        Assert.True(Directory.Exists(Path.Combine(PluginRoot, "acme.waf_v2-beta")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("sub/../../outside")]
    [InlineData("/etc/passwd")]
    public void EnsureWithin_refuses_paths_that_resolve_outside_the_root(string relative)
    {
        var root = Path.Combine(_sandbox, "root");
        Directory.CreateDirectory(root);

        Assert.Throws<InvalidDataException>(() => ExternalGatewayPluginHost.EnsureWithin(root, relative));
    }

    [Fact]
    public void EnsureWithin_accepts_a_nested_path_inside_the_root()
    {
        var root = Path.Combine(_sandbox, "root2");
        Directory.CreateDirectory(root);

        var resolved = ExternalGatewayPluginHost.EnsureWithin(root, Path.Combine("sub", "lib.dll"));

        Assert.Equal(Path.Combine(root, "sub", "lib.dll"), resolved);
    }

    [Fact]
    public void A_sibling_directory_sharing_the_prefix_is_not_treated_as_inside()
    {
        // "/plugins-evil" starts with "/plugins", which is why the boundary check appends the
        // separator. A plain StartsWith would have let this through.
        var root = Path.Combine(_sandbox, "root3");
        Directory.CreateDirectory(root);

        Assert.Throws<InvalidDataException>(() => ExternalGatewayPluginHost.EnsureWithin(root, "../root3-evil/x"));
    }

    private ExternalGatewayPluginHost Host()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Dashboard:PluginDirectory"] = PluginRoot
            })
            .Build();

        return new ExternalGatewayPluginHost(
            new FakeEnvironment(_sandbox),
            configuration,
            NullLogger<ExternalGatewayPluginHost>.Instance);
    }

    private string WritePlugin(string id)
    {
        var source = Path.Combine(_sandbox, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plugin.json"), $$"""
        {
          "id": {{System.Text.Json.JsonSerializer.Serialize(id)}},
          "name": "Test",
          "version": "1.0.0",
          "entryAssembly": "Test.dll",
          "entryType": "Test.Plugin"
        }
        """);
        return source;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class FakeEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
