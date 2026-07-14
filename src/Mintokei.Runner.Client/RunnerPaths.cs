using System.Runtime.InteropServices;

namespace Mintokei.Runner;

/// <summary>
/// Resolves the per-user directory where the runner persists its credentials and
/// local outbox database. Mirrors the API's desktop-mode app-data resolution so
/// both components agree on the Mintokei application folder.
/// </summary>
public static class RunnerPaths
{
    public const string CredentialsFileName = "runner-credentials.json";

    /// <summary>
    /// Resolves the runner data directory and ensures it exists. Precedence:
    /// an explicit override (--data-dir / Runner:DataDir / RUNNER__DataDir) wins,
    /// otherwise the OS per-user app-data directory is used. Point each instance at
    /// its own directory to run multiple runners on a single machine.
    /// </summary>
    public static string ResolveDataDirectory(string? dataDirOverride)
    {
        string dir;
        if (!string.IsNullOrWhiteSpace(dataDirOverride))
        {
            dir = Path.GetFullPath(dataDirOverride);
        }
        else
        {
            string appDataRoot;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                appDataRoot = Path.Combine(home, "Library", "Application Support");
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                appDataRoot = Path.Combine(home, ".config");
            }

            dir = Path.Combine(appDataRoot, "Mintokei", "runner");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Path to the persisted credentials file inside <paramref name="dataDir"/>.</summary>
    public static string CredentialsPath(string dataDir) => Path.Combine(dataDir, CredentialsFileName);
}
