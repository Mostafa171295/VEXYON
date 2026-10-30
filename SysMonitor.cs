using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VEXYON;

/// <summary>One reading of the machine's load and temperature.</summary>
public sealed class SysSnapshot
{
    public double CpuPct;
    public double RamPct, RamUsedGb, RamTotalGb;
    public double? GpuPct;
    public double? CpuTemp;
    public double VexyonMb, OllamaMb;
    public string CpuName = "";
    public string GpuDetail = "";
}

/// <summary>Thin wrapper over the Windows PDH API (supports wildcard counters, needs no extra NuGet package).</summary>
sealed class PdhCounter : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct Item { public IntPtr Name; public uint Status; public double Value; }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    IntPtr _query, _counter;

    public static PdhCounter? TryOpen(string path)
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var q) != 0) return null;
            if (PdhAddEnglishCounter(q, path, IntPtr.Zero, out var c) != 0) { PdhCloseQuery(q); return null; }
            PdhCollectQueryData(q); // first sample (rate counters need two)
            return new PdhCounter { _query = q, _counter = c };
        }
        catch { return null; }
    }

    /// <summary>Returns (instance name, value) for every valid instance.</summary>
    public List<(string name, double value)> Read()
    {
        var res = new List<(string, double)>();
        try
        {
            if (_query == IntPtr.Zero) return res;
            if (PdhCollectQueryData(_query) != 0) return res;
            uint size = 0, count = 0;
            uint r = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return res;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                r = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buf);
                if (r != 0) return res;
                int sz = Marshal.SizeOf<Item>();
                for (int i = 0; i < count; i++)
                {
                    var it = Marshal.PtrToStructure<Item>(buf + i * sz);
                    if (it.Status != 0 && it.Status != 1) continue; // valid data / new data only
                    if (double.IsNaN(it.Value) || it.Value < 0) continue;
                    string name = Marshal.PtrToStringUni(it.Name) ?? "";
                    res.Add((name, it.Value));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return res;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } catch { }
            _query = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Samples CPU load, RAM, GPU load (all GPUs, any vendor) and CPU temperature on a background thread.
/// CPU temperature comes from the Windows "Thermal Zone Information" counter (works without admin rights
/// on many laptops, but not on every machine - then CpuTemp stays null).
/// </summary>
public sealed class SysMonitor : IDisposable
{
    const double GB = 1073741824.0;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public event Action<SysSnapshot>? Updated;

    readonly CancellationTokenSource _cts = new();
    long _pi, _pk, _pu;
    double _lastCpu;
    double? _lastGpu;
    string _lastGpuDetail = "";
    double _olMb;

    public void Start() => _ = Task.Run(Loop);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }

    async Task Loop()
    {
        var ct = _cts.Token;
        PdhCounter? gpu = null, mem = null, therm = null;
        try
        {
            gpu = PdhCounter.TryOpen(@"\GPU Engine(*)\Utilization Percentage");
            mem = PdhCounter.TryOpen(@"\GPU Adapter Memory(*)\Dedicated Usage");
            therm = PdhCounter.TryOpen(@"\Thermal Zone Information(*)\Temperature");
            GetSystemTimes(out _pi, out _pk, out _pu);
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
                int t = tick++;
                try
                {
                    // re-open the GPU counters now and then so newly started programs are picked up
                    bool fresh = t % 80 == 79;
                    if (fresh)
                    {
                        gpu?.Dispose(); mem?.Dispose();
                        gpu = PdhCounter.TryOpen(@"\GPU Engine(*)\Utilization Percentage");
                        mem = PdhCounter.TryOpen(@"\GPU Adapter Memory(*)\Dedicated Usage");
                    }
                    var snap = Sample(t, fresh, gpu, mem, therm);
                    Updated?.Invoke(snap);
                }
                catch { }
            }
        }
        finally { gpu?.Dispose(); mem?.Dispose(); therm?.Dispose(); }
    }

    SysSnapshot Sample(int tick, bool fresh, PdhCounter? gpu, PdhCounter? mem, PdhCounter? therm)
    {
        var s = new SysSnapshot { CpuName = CpuNameCached };

        // ---- CPU load ----
        if (GetSystemTimes(out long i, out long k, out long u))
        {
            long dI = i - _pi, dK = k - _pk, dU = u - _pu;
            _pi = i; _pk = k; _pu = u;
            long total = dK + dU; // kernel time already includes idle time
            if (total > 0) _lastCpu = Math.Max(0, Math.Min(100, (1.0 - (double)dI / total) * 100.0));
        }
        s.CpuPct = _lastCpu;

        // ---- RAM ----
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            s.RamTotalGb = m.ullTotalPhys / GB;
            s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / GB;
            s.RamPct = m.dwMemoryLoad;
        }

        // ---- memory used by VEXYON itself and by Ollama (the model) ----
        s.VexyonMb = Environment.WorkingSet / 1048576.0;
        if (tick % 3 == 0)
        {
            double sum = 0;
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try { if (p.ProcessName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)) sum += p.WorkingSet64; }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            _olMb = sum / 1048576.0;
        }
        s.OllamaMb = _olMb;

        // ---- GPU load: per adapter = busiest engine type (same idea as Task Manager); overall = busiest adapter ----
        if (gpu != null && !fresh)
        {
            var items = gpu.Read();
            if (items.Count > 0)
            {
                var per = new SortedDictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
                foreach (var (name, val) in items)
                {
                    string luid = LuidOf(name);
                    string eng = EngTypeOf(name);
                    if (!per.TryGetValue(luid, out var engs)) { engs = new Dictionary<string, double>(); per[luid] = engs; }
                    engs[eng] = (engs.TryGetValue(eng, out var cur) ? cur : 0) + val;
                }

                var vram = new Dictionary<string, double>();
                if (mem != null)
                    foreach (var (name, val) in mem.Read())
                    {
                        string l = LuidOf(name);
                        vram[l] = (vram.TryGetValue(l, out var x) ? x : 0) + val;
                    }

                double max = 0;
                var lines = new List<string>();
                int n = 0;
                foreach (var kv in per)
                {
                    n++;
                    double util = Math.Min(100.0, kv.Value.Values.Max());
                    if (util > max) max = util;
                    string line = $"كارت {n}: {util:0}%";
                    if (vram.TryGetValue(kv.Key, out var vb) && vb > 0) line += $"  —  ذاكرة مخصصة {vb / GB:0.0} جيجا";
                    lines.Add(line);
                }
                _lastGpu = max;
                _lastGpuDetail = string.Join("\n", lines);
            }
        }
        s.GpuPct = _lastGpu;
        s.GpuDetail = _lastGpuDetail;

        // ---- CPU temperature (ACPI thermal zones; Kelvin on most systems) ----
        if (therm != null)
        {
            double best = 0;
            foreach (var (_, val) in therm.Read())
            {
                double c = val > 200 ? val - 273.15 : val;
                if (c >= 15 && c <= 125 && c > best) best = c;
            }
            if (best > 0) s.CpuTemp = best;
        }
        return s;
    }

    static string LuidOf(string instance)
    {
        int l = instance.IndexOf("luid_", StringComparison.Ordinal);
        int p = instance.IndexOf("_phys_", StringComparison.Ordinal);
        return l >= 0 && p > l ? instance.Substring(l + 5, p - (l + 5)) : "0";
    }

    static string EngTypeOf(string instance)
    {
        int e = instance.IndexOf("engtype_", StringComparison.Ordinal);
        return e >= 0 ? instance.Substring(e + 8) : "x";
    }

    // ---------- static info for the "About" tab ----------
    static readonly string CpuNameCached = ReadCpuName();
    public static string CpuName => CpuNameCached;

    static string ReadCpuName()
    {
        string? name = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = k?.GetValue("ProcessorNameString") as string;
        }
        catch { }
        if (string.IsNullOrWhiteSpace(name)) name = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
        return string.Join(" ", name!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static double TotalRamGb()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m)) return m.ullTotalPhys / GB;
        }
        catch { }
        return 0;
    }

    public static List<string> GpuNames()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
                foreach (var n in cls.GetSubKeyNames())
                {
                    if (n.Length != 4 || !n.All(char.IsDigit)) continue;
                    using var k = cls.OpenSubKey(n);
                    var d = k?.GetValue("DriverDesc") as string;
                    if (!string.IsNullOrWhiteSpace(d) && !list.Contains(d!)) list.Add(d!);
                }
        }
        catch { }
        return list;
    }
}
