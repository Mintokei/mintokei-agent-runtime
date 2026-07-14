using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Resolves executable names to full paths using the OS PATH lookup
/// (which on Unix, where on Windows). Used by both the API server
/// (local execution) and the runner (remote execution).
/// </summary>
public static class ExecutableResolver
{
    public static string Resolve(string executableName)
    {
        // If already a full path, return as-is
        if (Path.IsPathRooted(executableName))
            return executableName;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var command = isWindows ? "where" : "which";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = executableName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });

            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
                return executableName; // Fallback to name, let OS try PATH lookup

            var candidates = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            if (isWindows)
            {
                return candidates.FirstOrDefault(p =>
                        p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
            }

            return candidates[0];
        }
        catch
        {
            return executableName; // Fallback on any error
        }
    }
}
