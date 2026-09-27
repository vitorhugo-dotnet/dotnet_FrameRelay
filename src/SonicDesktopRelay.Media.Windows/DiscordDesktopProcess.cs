using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonicDesktopRelay.Media.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class DiscordDesktopProcess
{
    // A single process-loopback target can exclude only one tree. Multiple independent
    // desktop clients are ambiguous: silence rather than leak another Discord tree.
    public static uint? FindRoot()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            var processes = new Dictionary<uint, (uint Parent, string Name)>();
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { processes[entry.Id] = (entry.ParentId, entry.ExeFile); }
            while (Process32Next(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
            return SelectRoot(processes);
        }
        finally { CloseHandle(snapshot); }
    }

    internal static uint? SelectRoot(IReadOnlyDictionary<uint, (uint Parent, string Name)> processes)
    {
        var discord = processes.Where(p => IsDiscord(p.Value.Name)).Select(p => p.Key).ToHashSet();
        var roots = discord.Where(p => !discord.Contains(processes[p].Parent)).ToArray();
        return roots.Length switch
        {
            0 => null,
            1 => roots[0],
            _ => throw new InvalidOperationException("Multiple independent Discord desktop process trees are running.")
        };
    }

    private static bool IsDiscord(string name) => name.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DiscordPTB.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DiscordCanary.exe", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
