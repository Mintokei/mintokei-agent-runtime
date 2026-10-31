using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using J = Mintokei.Memory.Json;

namespace Mintokei.Memory.Tests;
public sealed class UnixProcessFactAttribute : FactAttribute
{
    public UnixProcessFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Scripted protocol subprocess uses POSIX Python; live adapter is cross-platform .NET.";
    }
}
