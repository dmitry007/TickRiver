using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TickRiver;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly MemoryMappedFile _owner;
    private readonly RegisteredWaitHandle? _wait;
    public bool IsFirst { get; }
    public int OwnerProcessId { get; }

    public SingleInstance(string name, Action activate)
    {
        // Create the signal before acquiring ownership: an early second launch can queue activation.
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Activate");
        _owner = MemoryMappedFile.CreateOrOpen(name + "-Owner", sizeof(int));
        _mutex = new Mutex(true, name, out var created);
        IsFirst = created;
        using var owner = _owner.CreateViewAccessor();
        if (created)
        {
            OwnerProcessId = Environment.ProcessId; owner.Write(0, OwnerProcessId);
            _wait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => activate(), null, Timeout.Infinite, false);
        }
        else
        {
            // A launch from Explorer can grant focus; the background process cannot grant it to itself.
            // Wait briefly if the first launch is still publishing its PID.
            var deadline = Stopwatch.StartNew();
            while ((OwnerProcessId = owner.ReadInt32(0)) == 0 && deadline.Elapsed < TimeSpan.FromSeconds(1)) Thread.Sleep(10);
            if (OwnerProcessId > 0) AllowSetForegroundWindow((uint)OwnerProcessId);
            _activation.Set();
        }
    }
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
    public void Dispose() { _wait?.Unregister(null); _activation.Dispose(); _mutex.Dispose(); _owner.Dispose(); }
}
