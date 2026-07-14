using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Runs command line processes with merged stdout/stderr streaming output.
/// </summary>
public sealed class CommandLineRunner : ICommandLineRunner
{
    public IAsyncEnumerable<CommandOutput> RunAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken = default)
    {
        var (handle, output) = Start(options, cancellationToken);
        return WrapWithAutoDispose(handle, output, cancellationToken);
    }

    public (IProcessHandle Handle, IAsyncEnumerable<CommandOutput> Output) Start(
        CommandLineOptions options,
        CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var process = CreateProcess(options);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            linkedCts.Dispose();
            throw new InvalidOperationException(
                $"Failed to start '{options.Executable}': executable not found. " +
                "Ensure the CLI tool is installed and available on PATH, or configure a remote " +
                "runner machine (RunnerMachineId) on the workspace so tasks run via RemoteCommandLineRunner.",
                ex);
        }

        var handle = new ProcessHandle(process, linkedCts);
        var output = StreamOutputAsync(process, options.CaptureStdErr, linkedCts.Token);

        return (handle, output);
    }

    private static string BuildArguments(IReadOnlyDictionary<string, string?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "";

        var sb = new StringBuilder();

        foreach (var (key, value) in arguments)
        {
            if (sb.Length > 0)
                sb.Append(' ');

            if (value is null)
            {
                sb.Append(key);
            }
            else if (value.Contains(' ') || value.Contains('"'))
            {
                var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
                sb.Append(key).Append(' ').Append('"').Append(escaped).Append('"');
            }
            else
            {
                sb.Append(key).Append(' ').Append(value);
            }
        }

        return sb.ToString();
    }

    private static Process CreateProcess(CommandLineOptions options)
    {
        var resolvedExecutable = ExecutableResolver.Resolve(options.Executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = options.CaptureStdErr,
            RedirectStandardInput = options.RedirectStdIn,
            WorkingDirectory = options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (options.ArgumentList is { Count: > 0 })
        {
            // Per-argument escaping — values may contain whitespace, newlines, or quotes.
            foreach (var arg in options.ArgumentList)
                startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.Arguments = BuildArguments(options.Arguments);
        }

        if (options.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in options.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        return new Process { StartInfo = startInfo };
    }

    private static async IAsyncEnumerable<CommandOutput> StreamOutputAsync(
        Process process,
        bool captureStdErr,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<CommandOutput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var stdoutTask = ReadStreamAsync(
            process.StandardOutput, OutputType.StdOut, channel.Writer, cancellationToken);

        var stderrTask = captureStdErr
            ? ReadStreamAsync(
                process.StandardError, OutputType.StdErr, channel.Writer, cancellationToken)
            : Task.CompletedTask;

        _ = Task.WhenAll(stdoutTask, stderrTask).ContinueWith(
            _ => channel.Writer.TryComplete(),
            TaskContinuationOptions.ExecuteSynchronously);

        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        OutputType type,
        ChannelWriter<CommandOutput> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;

                var output = new CommandOutput(line, type, DateTimeOffset.UtcNow);
                await writer.WriteAsync(output, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private static async IAsyncEnumerable<CommandOutput> WrapWithAutoDispose(
        IProcessHandle handle,
        IAsyncEnumerable<CommandOutput> output,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in output.WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }
}
