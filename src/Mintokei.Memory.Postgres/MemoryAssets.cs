namespace Mintokei.Memory.Postgres;

/// <summary>Materializes bundled prompt/schema versions without overwriting operator-owned files.</summary>
public static class MemoryAssets
{
    public static void EnsureFiles(string root)
    {
        var assembly = typeof(MemoryAssets).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Assets/", StringComparison.Ordinal)))
        {
            var path = Path.Combine(root, name[7..]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) continue;
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = assembly.GetManifestResourceStream(name)!)
                using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(destination);
                try { File.Move(temporaryPath, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { /* Another host published this immutable version. */ }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
