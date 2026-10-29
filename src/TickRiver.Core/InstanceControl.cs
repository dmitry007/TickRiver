using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TickRiver.Core;

[SupportedOSPlatform("windows")]
public sealed class InstanceControl : IDisposable
{
    public const int UpdateInProgressExitCode = 1618;
    public static string GateName { get { using var identity = WindowsIdentity.GetCurrent(); return "Local\\TickRiver.Update." + identity.User!.Value; } }
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _listener;
    private readonly Action _shutdown;
    private readonly byte _mode;

    public InstanceControl(Action shutdown, byte mode)
    {
        _shutdown = shutdown; _mode = mode;
        var security = Security();
        try
        {
            var handle = CreateNamedPipe("\\\\.\\pipe\\TickRiver.Control." + Environment.ProcessId,
                3 | 0x40000000 | 0x80000, 8, 1, 512, 512, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try { _pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(security.Descriptor); }
        _listener = Task.Run(Listen);
    }

    public static bool UpdateInProgress
    {
        get { if (!EventWaitHandle.TryOpenExisting(GateName, out var gate)) return false; gate.Dispose(); return true; }
    }

    public static EventWaitHandle CreateUpdateGate()
    {
        var security = Security();
        try
        {
            var handle = CreateEvent(ref security, true, true, GateName);
            if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            var gate = new EventWaitHandle(false, EventResetMode.ManualReset);
            gate.SafeWaitHandle.Dispose(); gate.SafeWaitHandle = handle;
            return gate;
        }
        finally { LocalFree(security.Descriptor); }
    }

    private static SecurityAttributes Security()
    {
        // Supply the medium integrity label at creation, without requesting ACCESS_SYSTEM_SECURITY.
        using var identity = WindowsIdentity.GetCurrent();
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;" + identity.User!.Value + ")S:(ML;;NW;;;ME)",
            1, out var descriptor, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
    }

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);
                connected = true;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(3));
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId)
                    || !ProcessIdToSessionId(clientId, out var clientSession)
                    || !ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession) || clientSession != ownSession) continue;
                var command = new byte[1];
                await _pipe.ReadExactlyAsync(command, request.Token);
                if (command[0] is not (1 or 2)) continue;
                var reply = new byte[9];
                "TR01"u8.CopyTo(reply); BitConverter.TryWriteBytes(reply.AsSpan(4), Environment.ProcessId); reply[8] = _mode;
                await _pipe.WriteAsync(reply, request.Token);
                await _pipe.FlushAsync(request.Token);
                var acknowledgement = new byte[1];
                await _pipe.ReadExactlyAsync(acknowledgement, request.Token);
                if (acknowledgement[0] != 6) continue;
                if (command[0] == 2)
                {
                    Log("Cooperative shutdown accepted; mode=" + _mode);
                    _shutdown();
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { if (!_stop.IsCancellationRequested && error is not OperationCanceledException) Log("Control request ended: " + error.Message); }
            finally { try { if (connected) _pipe.Disconnect(); } catch (Exception error) when (error is ObjectDisposedException or IOException) { } }
        }
    }

    public static async Task<bool> RequestShutdown(Process process, bool shutdown = true, int timeout = 1500)
    {
        if (!WaitNamedPipe("\\\\.\\pipe\\TickRiver.Control." + process.Id, 10)) return false;
        using var cancel = new CancellationTokenSource(timeout);
        using var pipe = new NamedPipeClientStream(".", "TickRiver.Control." + process.Id, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(cancel.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != process.Id) { Log($"Control pipe owner mismatch: expected={process.Id}; actual={serverId}"); return false; }
            await pipe.WriteAsync(new byte[] { shutdown ? (byte)2 : (byte)1 }, cancel.Token);
            await pipe.FlushAsync(cancel.Token);
            var reply = new byte[9]; await pipe.ReadExactlyAsync(reply, cancel.Token);
            if (!reply.AsSpan(0, 4).SequenceEqual("TR01"u8) || BitConverter.ToInt32(reply, 4) != process.Id) { Log("Invalid control response from PID=" + process.Id); return false; }
            await pipe.WriteAsync(new byte[] { 6 }, cancel.Token); await pipe.FlushAsync(cancel.Token);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { Log($"Control request to PID={process.Id} failed: {error.Message}"); return false; }
    }

    public static void Log(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickRiver", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "installer-shutdown.log"), $"{DateTimeOffset.Now:O} PID={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel(); _pipe.Dispose();
        try { _listener.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint access, uint mode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(ref SecurityAttributes security, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
