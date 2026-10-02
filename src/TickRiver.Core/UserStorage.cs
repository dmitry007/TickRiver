using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TickRiver.Core;

public static class UserStorage
{
    public static string DatabasePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickRiver", "tickriver.sqlite");

    public static Store Open(bool readOnly = false)
    {
        Validate(DatabasePath, readOnly);
        return new Store(DatabasePath, readOnly);
    }

    // Packaged AI hosts can redirect AppData even when given an absolute path.
    // Refuse a private copy rather than let different clients track different data.
    public static void Validate(string path, bool readOnly = false)
    {
        path = System.IO.Path.GetFullPath(path);
        string? probe = null;
        try
        {
            if (!File.Exists(path) && !readOnly)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".storage-check-" + Guid.NewGuid().ToString("N"));
            }
            using var stream = new FileStream(probe ?? path, probe == null ? FileMode.Open : FileMode.CreateNew,
                probe == null ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (!OperatingSystem.IsWindows()) return;
            var physical = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, physical, (uint)physical.Capacity, 0);
            if (length == 0 || length >= physical.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            var resolved = physical.ToString();
            if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
            if (!string.Equals(resolved, probe ?? path, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Windows redirected TickRiver's data to {resolved}. Start TickRiver from its Windows desktop shortcut to use the shared user-data folder: {System.IO.Path.GetDirectoryName(path)}.");
        }
        finally { if (probe != null && File.Exists(probe)) File.Delete(probe); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
