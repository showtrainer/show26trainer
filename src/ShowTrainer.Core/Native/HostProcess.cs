using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ShowTrainer.Core.Infra;

namespace ShowTrainer.Core.Native;

public readonly record struct HostRegion(ulong Base, ulong Size, ulong AllocationBase, uint State, uint Type, uint Protect)
{
    public ulong End => Base + Size;
    public bool IsCommitted => State == Win32.MEM_COMMIT;
    public bool IsReadable => IsCommitted && (Protect & (Win32.PAGE_GUARD | Win32.PAGE_NOACCESS)) == 0 && Protect != 0;
    public bool IsWritable => IsCommitted && (Protect & (Win32.PAGE_READWRITE | Win32.PAGE_EXECUTE_READWRITE | Win32.PAGE_WRITECOPY)) != 0;
    public override string ToString() =>
        $"{Base:X12}-{End:X12} size={Size:X} state={State:X} type={Type:X} prot={Protect:X} alloc={AllocationBase:X}";
}

public sealed record EmulatorCandidate(int Pid, string Name, string? Path, string WindowTitle);

/// <summary>Handle to the emulator process with raw host-side memory access.</summary>
public sealed class HostProcess : IDisposable
{
    private static readonly Regex EmuName = new("eden|yuzu|suyu|citron|sudachi|torzu", RegexOptions.IgnoreCase);

    private IntPtr _handle;
    public int Pid { get; }
    public string Name { get; }
    public string? ExePath { get; }

    private HostProcess(IntPtr handle, Process p)
    {
        _handle = handle;
        Pid = p.Id;
        Name = p.ProcessName;
        try { ExePath = p.MainModule?.FileName; } catch { ExePath = null; }
    }

    /// <summary>Finds processes that look like Eden (or another Yuzu fork), including renamed builds
    /// identified by window title or file description.</summary>
    public static List<EmulatorCandidate> FindCandidates()
    {
        var list = new List<EmulatorCandidate>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string title = "";
                try { title = p.MainWindowTitle ?? ""; } catch { }
                string? path = null, desc = null;
                try { path = p.MainModule?.FileName; desc = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                bool match = EmuName.IsMatch(p.ProcessName)
                             || (desc != null && EmuName.IsMatch(desc))
                             || title.StartsWith("Eden", StringComparison.OrdinalIgnoreCase)
                             || (path != null && EmuName.IsMatch(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "")));
                if (match) list.Add(new EmulatorCandidate(p.Id, p.ProcessName, path, title));
            }
            catch { }
            finally { p.Dispose(); }
        }
        // Eden first, then anything with a game-looking title.
        return list.OrderByDescending(c => c.Name.Contains("eden", StringComparison.OrdinalIgnoreCase))
                   .ThenByDescending(c => c.WindowTitle.Length).ToList();
    }

    public static HostProcess Open(int pid)
    {
        using var p = Process.GetProcessById(pid);
        var h = Win32.OpenProcess(Win32.PROCESS_VM_READ | Win32.PROCESS_VM_WRITE | Win32.PROCESS_VM_OPERATION | Win32.PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            throw new InvalidOperationException($"OpenProcess({pid}) failed, Win32 error {Marshal.GetLastWin32Error()}. If Eden runs as admin, run the trainer as admin too.");
        Log.Info($"Attached to {p.ProcessName} pid={pid}");
        return new HostProcess(h, p);
    }

    public bool IsAlive => _handle != IntPtr.Zero && Win32.GetExitCodeProcess(_handle, out var code) && code == 259 /*STILL_ACTIVE*/;

    public string CurrentWindowTitle()
    {
        try { using var p = Process.GetProcessById(Pid); return p.MainWindowTitle ?? ""; } catch { return ""; }
    }

    public List<HostRegion> EnumerateRegions()
    {
        var list = new List<HostRegion>(4096);
        ulong addr = 0;
        int sz = Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000)
        {
            if (Win32.VirtualQueryEx(_handle, (IntPtr)(long)addr, out var m, sz) == IntPtr.Zero) break;
            var r = new HostRegion((ulong)m.BaseAddress, (ulong)m.RegionSize, (ulong)m.AllocationBase, m.State, m.Type, m.Protect);
            list.Add(r);
            if (r.Size == 0) break;
            addr = r.End;
        }
        return list;
    }

    public HostRegion? Query(ulong addr)
    {
        if (Win32.VirtualQueryEx(_handle, (IntPtr)(long)addr, out var m, Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero) return null;
        return new HostRegion((ulong)m.BaseAddress, (ulong)m.RegionSize, (ulong)m.AllocationBase, m.State, m.Type, m.Protect);
    }

    public unsafe bool Read(ulong hostAddr, Span<byte> dst)
    {
        fixed (byte* p = dst)
            return Win32.ReadProcessMemory(_handle, (IntPtr)(long)hostAddr, p, dst.Length, out var n) && (int)n == dst.Length;
    }

    public unsafe bool Write(ulong hostAddr, ReadOnlySpan<byte> src)
    {
        fixed (byte* p = src)
            return Win32.WriteProcessMemory(_handle, (IntPtr)(long)hostAddr, p, src.Length, out var n) && (int)n == src.Length;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { Win32.CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}
