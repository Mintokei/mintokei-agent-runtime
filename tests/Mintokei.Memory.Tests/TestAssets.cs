using System.Runtime.CompilerServices;
using Mintokei.Memory.Postgres;

internal static class TestAssets
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Mintokei.slnx")))
            root = root.Parent;
        MemoryAssets.EnsureFiles(root!.FullName);
    }
}
