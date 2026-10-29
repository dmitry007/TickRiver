using System.Runtime.InteropServices;

namespace TickRiver.Update;

internal static class RecoveryPrompt
{
    public static int Show(int count, bool legacy)
    {
        var strings = new List<IntPtr>();
        IntPtr Text(string value) { var pointer = Marshal.StringToHGlobalUni(value); strings.Add(pointer); return pointer; }
        var buttons = Marshal.AllocHGlobal(3 * Marshal.SizeOf<Button>());
        try
        {
            Marshal.StructureToPtr(new Button { Id = 100, Text = Text("Retry") }, buttons, false);
            Marshal.StructureToPtr(new Button { Id = 101, Text = Text("Force close and continue") }, buttons + Marshal.SizeOf<Button>(), false);
            Marshal.StructureToPtr(new Button { Id = 2, Text = Text("Cancel setup") }, buttons + 2 * Marshal.SizeOf<Button>(), false);
            var config = new Config {
                Size = (uint)Marshal.SizeOf<Config>(), Flags = 8, WindowTitle = Text("TickRiver Setup"), MainIcon = new IntPtr(ushort.MaxValue),
                Instruction = Text("TickRiver is still running"),
                Content = Text((count == 1 ? "One TickRiver window or AI connection has not closed." : $"{count} TickRiver windows or AI connections have not closed.")
                    + (legacy ? " Older versions do not support automatic shutdown." : " Finish or cancel any open edits and let pending time-entry changes complete.")
                    + "\n\nRetry after closing TickRiver or disconnecting its AI connections. Force close can interrupt a time-entry change. Your saved time data will be retained."),
                ButtonCount = 3, Buttons = buttons, DefaultButton = 100, Width = 340
            };
            Marshal.ThrowExceptionForHR(TaskDialogIndirect(ref config, out var result, out _, out _));
            if (result == 2) throw new OperationCanceledException("The user cancelled setup while waiting for TickRiver to close.");
            return result;
        }
        finally { Marshal.FreeHGlobal(buttons); foreach (var text in strings) Marshal.FreeHGlobal(text); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Button { public int Id; public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Config
    {
        public uint Size; public IntPtr Parent, Instance; public uint Flags, CommonButtons;
        public IntPtr WindowTitle, MainIcon, Instruction, Content; public uint ButtonCount; public IntPtr Buttons;
        public int DefaultButton; public uint RadioCount; public IntPtr Radios; public int DefaultRadio;
        public IntPtr Verification, Expanded, ExpandedControl, CollapsedControl, FooterIcon, Footer, Callback, CallbackData;
        public uint Width;
    }
    [DllImport("comctl32.dll", PreserveSig = true)]
    private static extern int TaskDialogIndirect(ref Config config, out int button, out int radio, out int verified);
}
