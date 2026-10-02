using System.Reflection;

namespace TickRiver;

internal static class CrashLog
{
    private static readonly object _lock = new();
    internal static string? Write(object error, string context, string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickRiver", "logs");
            var path = Path.Combine(directory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
            var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            lock (_lock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {context}\nExecutable: {Environment.ProcessPath}\nBuild: {version}\nRuntime: {Environment.Version}\n{error}\n\n");
            }
            return path;
        }
        catch { return null; } // Reporting must not replace the original exception.
    }
}
