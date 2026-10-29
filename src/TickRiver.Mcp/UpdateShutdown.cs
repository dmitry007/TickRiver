using Microsoft.Extensions.Hosting;

namespace TickRiver.Mcp;

internal static class UpdateShutdown
{
    private static readonly object _lock = new();
    private static int _active;
    private static bool _stopping;

    public static IDisposable BeginChange()
    {
        lock (_lock)
        {
            if (_stopping) throw new InvalidOperationException("TickRiver is closing for setup. Reconnect your AI client after setup finishes.");
            _active++;
            return new Change();
        }
    }

    public static void Request(IHostApplicationLifetime lifetime)
    {
        lock (_lock)
        {
            _stopping = true;
            while (_active > 0) Monitor.Wait(_lock);
        }
        lifetime.StopApplication();
    }

    private sealed class Change : IDisposable
    {
        public void Dispose() { lock (_lock) { _active--; Monitor.PulseAll(_lock); } }
    }
}
