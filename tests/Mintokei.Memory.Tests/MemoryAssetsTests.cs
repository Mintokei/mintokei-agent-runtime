namespace Mintokei.Memory.Tests;

public sealed class MemoryAssetsTests
{
    [Fact]
    public void Packaged_assets_work_without_a_product_checkout_and_keep_custom_versions()
    {
        var root = Path.Combine(Path.GetTempPath(), "runtime-assets-" + Guid.NewGuid());
        try
        {
            MemoryAssets.EnsureFiles(root);
            Assert.True(File.Exists(Path.Combine(root, "prompts", "v2", "extraction.txt")));
            Assert.True(File.Exists(Path.Combine(root, "schemas", "extraction.json")));
            Assert.True(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length >= 40);
            var custom = Path.Combine(root, "prompts", "v2", "extraction.txt");
            File.WriteAllText(custom, "operator-owned version");
            Parallel.For(0, 4, _ => MemoryAssets.EnsureFiles(root));
            Assert.Equal("operator-owned version", File.ReadAllText(custom));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Core_contracts_do_not_depend_on_database_host_or_product_assemblies()
    {
        var references = typeof(IMemoryService).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, r => r.Name!.StartsWith("Npgsql") || r.Name.Contains("EntityFramework") ||
            r.Name.StartsWith("Microsoft.AspNetCore") || r.Name.StartsWith("Mintokei.Api") || r.Name.Contains("Playground"));
    }
}
