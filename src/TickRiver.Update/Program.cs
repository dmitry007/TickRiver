using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TickRiver.Core;

namespace TickRiver.Update;

internal static class Program
{
    private static readonly string _userSid = WindowsIdentity.GetCurrent().User!.Value;
    private static readonly uint _session = SessionId(Environment.ProcessId);
    private sealed class Target(Process process, SafeProcessHandle handle, string image, long created) : IDisposable
    {
        public Process Process { get; } = process;
        public SafeProcessHandle Handle { get; } = handle;
        public string Image { get; } = image;
        public long Created { get; } = created;
        public int Id => Process.Id;
        public bool HasExited => WaitForSingleObject(Handle, 0) == 0;
        public void Dispose() { Process.Dispose(); Handle.Dispose(); }
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--guard") { HoldGate(args[1]); return 0; }
            if (args.Length < 3 || args[0] != "--shutdown-for-update") throw new ArgumentException("Expected --shutdown-for-update <application-root> <installation-directory> [--msi] [--ui-level 0..5] [--timeout milliseconds].");
            var roots = args.Skip(1).Take(2).Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
            if (args.Skip(1).Take(2).Any(path => !Path.IsPathFullyQualified(path))) throw new ArgumentException("Update paths must be absolute.");
            var msi = false; var ui = 0; var timeout = 30000;
            for (var index = 3; index < args.Length; index++)
            {
                if (args[index] == "--msi") msi = true;
                else if (args[index] == "--ui-level" && ++index < args.Length && int.TryParse(args[index], out ui) && ui is >= 0 and <= 5) { }
                else if (args[index] == "--timeout" && ++index < args.Length && int.TryParse(args[index], out timeout) && timeout is >= 100 and <= 120000) { }
                else throw new ArgumentException("Invalid update helper arguments.");
            }
            using var gate = InstanceControl.CreateUpdateGate();
            if (msi) StartGuard();
            await Shutdown(roots, ui, timeout);
            return 0;
        }
        catch (Exception error) { InstanceControl.Log("Setup shutdown failed: " + error); return 1603; }
    }

    private static Mutex InstallerMutex()
    {
        try { return MutexAcl.OpenExisting("Global\\_MSIExecute", MutexRights.Synchronize | MutexRights.Modify); }
        catch (WaitHandleCannotBeOpenedException) { return MutexAcl.OpenExisting("_MSIExecute", MutexRights.Synchronize | MutexRights.Modify); }
    }

    private static void HoldGate(string readyName)
    {
        using var installer = InstallerMutex();
        using var gate = InstanceControl.CreateUpdateGate();
        using var ready = EventWaitHandle.OpenExisting(readyName);
        ready.Set();
        try { installer.WaitOne(); } catch (AbandonedMutexException) { }
        installer.ReleaseMutex();
        InstanceControl.Log("Installation finished; startup gate released.");
    }

    private static void StartGuard()
    {
        using (var installer = InstallerMutex())
        {
            var acquired = false;
            try { acquired = installer.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (acquired) { installer.ReleaseMutex(); throw new InvalidOperationException("--msi requires an active Windows Installer execute sequence."); }
        }
        var readyName = "Local\\TickRiver.Update.Ready." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--guard"); start.ArgumentList.Add(readyName);
        using var guard = Process.Start(start) ?? throw new IOException("Could not start the installer startup guard.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("The installer startup guard did not initialize.");
    }

    private static async Task Shutdown(string[] roots, int ui, int timeout)
    {
        var tracked = new Dictionary<int, Target>();
        var acknowledged = new HashSet<int>();
        var normalClose = new HashSet<int>();
        var started = Stopwatch.StartNew(); var stable = 0;
        try
        {
            while (true)
            {
                foreach (var name in new[] { "TickRiver", "TickRiver.Mcp" })
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (tracked.ContainsKey(process.Id)) { process.Dispose(); continue; }
                    var target = Capture(process, roots);
                    if (target == null) { process.Dispose(); continue; }
                    tracked.Add(process.Id, target);
                    InstanceControl.Log($"Found {name}: PID={process.Id}; path={target.Image}");
                }
                var pending = tracked.Values.Where(process => !process.HasExited).ToArray();
                if (pending.Length == 0)
                {
                    if (++stable == 2) { InstanceControl.Log("All matching TickRiver instances exited."); return; }
                }
                else stable = 0;
                foreach (var process in pending.Where(process => !acknowledged.Contains(process.Id)))
                {
                    if (await InstanceControl.RequestShutdown(process.Process, timeout: 2500))
                    {
                        acknowledged.Add(process.Id); InstanceControl.Log("Shutdown acknowledged: PID=" + process.Id);
                    }
                    else if (Path.GetFileName(process.Image).Equals("TickRiver.exe", StringComparison.OrdinalIgnoreCase) && normalClose.Add(process.Id)) process.Process.CloseMainWindow();
                }
                pending = tracked.Values.Where(process => !process.HasExited).ToArray();
                var legacy = pending.Any(process => !acknowledged.Contains(process.Id));
                if (pending.Length > 0 && (started.ElapsedMilliseconds >= timeout || (ui >= 4 && legacy && started.ElapsedMilliseconds >= 3000)))
                {
                    var ids = string.Join(", ", pending.Select(process => process.Id));
                    InstanceControl.Log("Still running: " + ids);
                    if (ui < 4) throw new TimeoutException("Close TickRiver and disconnect its AI connections, then retry setup. Still running: " + ids);
                    var choice = RecoveryPrompt.Show(pending.Length, legacy);
                    if (choice == 101)
                        foreach (var process in pending)
                            if (!process.HasExited) ForceClose(process);
                    started.Restart();
                }
                await Task.Delay(100);
            }
        }
        finally { foreach (var process in tracked.Values) process.Dispose(); }
    }

    private static Target? Capture(Process process, string[] roots)
    {
        SafeProcessHandle? handle = null;
        try
        {
            if (process.Id == Environment.ProcessId || SessionId(process.Id) != _session) return null;
            handle = OpenProcess(0x1000 | 0x100000, false, process.Id); // Query-limited + synchronize, including elevated same-user instances.
            if (handle.IsInvalid || WaitForSingleObject(handle, 0) == 0) return null;
            var image = ImagePath(handle);
            if (!roots.Any(root => image.StartsWith(root, StringComparison.OrdinalIgnoreCase))) return null;
            if (!OpenProcessToken(handle, 8, out var token)) return null;
            using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                if (identity.User?.Value != _userSid) return null;
            var target = new Target(process, handle, image, CreationTime(handle));
            handle = null; // The retained handle identifies the original process even after it exits.
            return target;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { InstanceControl.Log($"Could not inspect PID={process.Id}: {error.Message}. Windows Installer files-in-use handling remains available."); return null; }
        finally { handle?.Dispose(); }
    }

    private static string ImagePath(SafeProcessHandle handle)
    {
        var image = new StringBuilder(32768); var size = image.Capacity;
        if (!QueryFullProcessImageName(handle, 0, image, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return image.ToString();
    }

    private static uint SessionId(int processId)
    {
        if (!ProcessIdToSessionId((uint)processId, out var session)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return session;
    }

    private static long CreationTime(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return created;
    }

    private static void ForceClose(Target target)
    {
        using var handle = OpenProcess(0x1000 | 0x100000 | 1, false, target.Id);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows denied force close. Close any elevated TickRiver instance yourself and retry setup.");
        if (CreationTime(handle) != target.Created || !ImagePath(handle).Equals(target.Image, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Force close refused because the selected process identity changed.");
        if (!TerminateProcess(handle, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        InstanceControl.Log("User-approved force close: PID=" + target.Id);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
