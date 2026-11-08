using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Koalix.SystemIntegration;

/// <summary>v0.4.38 — one hardware sample (MSI-Afterburner-style overlay data). Every value is nullable: the
/// widget shows "—" for whatever this PC cannot report instead of inventing a number.</summary>
public sealed record HardwareSnapshot
{
    public double? CpuUsage { get; init; }
    public double? CpuTemp { get; init; }
    public double? CpuClockMhz { get; init; }
    public double? RamUsage { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double? GpuUsage { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }
    public double? GpuMemTemp { get; init; }
    public double? VramUsedGb { get; init; }
    public double? GpuFanRpm { get; init; }
    public double? Fps { get; init; }
    public string? GpuName { get; init; }
    /// <summary>"Windows", or the external tool the temperatures came from ("HWiNFO" / "Afterburner").</summary>
    public string Source { get; init; } = "Windows";
}

/// <summary>Readings published by an external monitoring tool through shared memory (all optional).</summary>
public sealed record ExternalSensors(string Source)
{
    public double? CpuTemp { get; init; }
    public double? CpuUsage { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }
    public double? GpuMemTemp { get; init; }
    public double? GpuUsage { get; init; }
    public double? Fps { get; init; }
    public bool IsEmpty => CpuTemp is null && GpuTemp is null && GpuHotspot is null && GpuMemTemp is null && Fps is null && CpuUsage is null && GpuUsage is null;
}

/// <summary>
/// v0.4.38 (user: «مث msi afterburner که میاد مثلا دما cpu دما gpu و دما gpu hotspot ک نشون میده») — hardware
/// telemetry WITHOUT admin rights or a kernel driver:
///  • CPU load: GetSystemTimes deltas · clock: PDH "% Processor Performance" × base frequency · RAM: GlobalMemoryStatusEx
///  • GPU load / VRAM: PDH "GPU Engine" / "GPU Adapter Memory" (the counters Task Manager uses)
///  • GPU temperature + fan: D3DKMTQueryAdapterInfo(ADAPTERPERFDATA) — Task Manager's own GPU-temperature source
///  • NVIDIA hot spot + memory junction: NVAPI ThermalGetSensors (Turing and newer)
///  • CPU temperature: ACPI "Thermal Zone Information" (approximate on many boards)
///  • When MSI Afterburner or HWiNFO is running, their shared memory wins for temperatures and FPS — they read
///    the real sensors with a driver, which is exactly what the user expects to see.
/// Every source fails soft: a missing API just leaves its fields null.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly object _gate = new();
    private Pdh? _pdh;
    private Kmt? _kmt;
    private NvApi? _nv;
    private bool _nvTried, _kmtTried, _pdhTried;
    private ulong _lastIdle, _lastKernel, _lastUser;
    private bool _haveTimes;

    public HardwareSnapshot Sample()
    {
        lock (_gate)
        {
            double? cpu = CpuLoad();
            var (ramPct, usedGb, totalGb) = Memory();
            if (!_pdhTried) { _pdhTried = true; _pdh = Pdh.TryCreate(); }
            if (!_kmtTried) { _kmtTried = true; _kmt = Kmt.TryCreate(); }
            if (!_nvTried) { _nvTried = true; _nv = NvApi.TryCreate(); }

            var p = _pdh?.Collect();
            var gpu = _kmt?.Read();
            var nv = _nv?.Read();

            double? gpuLoad = null, vram = null;
            if (p is not null)
            {
                gpuLoad = PickByLuid(p.GpuLoadByLuid, gpu?.Luid);
                if (PickByLuid(p.VramByLuid, gpu?.Luid) is { } bytes) vram = bytes / 1073741824.0;
            }
            var snap = new HardwareSnapshot
            {
                CpuUsage = cpu,
                CpuTemp = p?.ThermalZoneC,
                CpuClockMhz = p?.CpuClockMhz,
                RamUsage = ramPct,
                RamUsedGb = usedGb,
                RamTotalGb = totalGb,
                GpuUsage = gpuLoad is { } g ? Math.Clamp(g, 0, 100) : null,
                GpuTemp = gpu?.TempC ?? nv?.Core,
                GpuHotspot = nv?.Hotspot,
                GpuMemTemp = nv?.Memory,
                VramUsedGb = vram,
                GpuFanRpm = gpu?.FanRpm,
                GpuName = gpu?.Name,
            };
            var ext = ReadExternal();
            return ext is null ? snap : Merge(snap, ext);
        }
    }

    /// <summary>External tool readings override the native ones they cover (temperatures, FPS); loads stay native when present.</summary>
    public static HardwareSnapshot Merge(HardwareSnapshot s, ExternalSensors e) => s with
    {
        CpuTemp = e.CpuTemp ?? s.CpuTemp,
        GpuTemp = e.GpuTemp ?? s.GpuTemp,
        GpuHotspot = e.GpuHotspot ?? s.GpuHotspot,
        GpuMemTemp = e.GpuMemTemp ?? s.GpuMemTemp,
        Fps = e.Fps ?? s.Fps,
        CpuUsage = s.CpuUsage ?? e.CpuUsage,
        GpuUsage = s.GpuUsage ?? e.GpuUsage,
        Source = e.Source,
    };

    private static double? PickByLuid(IReadOnlyDictionary<string, double> byLuid, string? luid)
    {
        if (byLuid.Count == 0) return null;
        if (luid is not null && byLuid.TryGetValue(luid, out var v)) return v;
        return byLuid.Values.Max();
    }

    /* ───────────── CPU / RAM ───────────── */

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    private double? CpuLoad()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            double? load = null;
            if (_haveTimes)
            {
                var di = idle - _lastIdle; var dk = kernel - _lastKernel; var du = user - _lastUser;
                var total = dk + du; // kernel time already includes idle time
                if (total > 0) load = Math.Clamp((total - di) * 100.0 / total, 0, 100);
            }
            (_lastIdle, _lastKernel, _lastUser, _haveTimes) = (idle, kernel, user, true);
            return load;
        }
        catch { return null; }
    }

    private static (double?, double, double) Memory()
    {
        try
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m)) return (null, 0, 0);
            var total = m.TotalPhys / 1073741824.0;
            var used = (m.TotalPhys - m.AvailPhys) / 1073741824.0;
            return (m.MemoryLoad, used, total);
        }
        catch { return (null, 0, 0); }
    }

    /* ───────────── external tools (shared memory) ───────────── */

    private static ExternalSensors? ReadExternal()
    {
        var hw = TryMap(["Global\\HWiNFO_SENS_SM2", "HWiNFO_SENS_SM2"]) is { } h ? HwInfoParser.Parse(h) : null;
        var ab = TryMap(["MAHMSharedMemory"]) is { } a ? AfterburnerParser.Parse(a) : null;
        if (hw is { IsEmpty: false } && ab is { IsEmpty: false })
            return hw with { Fps = hw.Fps ?? ab.Fps, GpuHotspot = hw.GpuHotspot ?? ab.GpuHotspot, Source = "HWiNFO + Afterburner" };
        if (hw is { IsEmpty: false }) return hw;
        return ab is { IsEmpty: false } ? ab : null;
    }

    private static byte[]? TryMap(string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(n, MemoryMappedFileRights.Read);
                using var s = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
                var len = (int)Math.Min(s.Length, 8 * 1024 * 1024);
                var buf = new byte[len];
                var read = 0;
                while (read < len) { var r = s.Read(buf, read, len - read); if (r <= 0) break; read += r; }
                return buf;
            }
            catch { /* not running / no access */ }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate) { _pdh?.Dispose(); _kmt?.Dispose(); _pdh = null; _kmt = null; }
    }

    /* ───────────── PDH (performance counters) ───────────── */

    private sealed record PdhSample(Dictionary<string, double> GpuLoadByLuid, Dictionary<string, double> VramByLuid, double? ThermalZoneC, double? CpuClockMhz);

    private sealed class Pdh : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr q);
        private const uint FmtDouble = 0x200, NoCap100 = 0x8000;
        private const int MoreData = unchecked((int)0x800007D2);

        private IntPtr _q, _gpu, _vram, _tz, _perf, _freq;

        public static Pdh? TryCreate()
        {
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return null;
                var p = new Pdh { _q = q };
                PdhAddEnglishCounterW(q, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out p._gpu);
                PdhAddEnglishCounterW(q, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out p._vram);
                PdhAddEnglishCounterW(q, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out p._tz);
                PdhAddEnglishCounterW(q, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out p._perf);
                PdhAddEnglishCounterW(q, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out p._freq);
                PdhCollectQueryData(q); // rate counters need a first baseline
                return p;
            }
            catch { return null; }
        }

        public PdhSample? Collect()
        {
            if (_q == IntPtr.Zero || PdhCollectQueryData(_q) != 0) return null;
            var gpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, v) in Array(_gpu))
                if (GpuLuid(name) is { } l) gpu[l] = gpu.GetValueOrDefault(l) + v;
            var vram = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, v) in Array(_vram))
                if (GpuLuid(name) is { } l) vram[l] = vram.GetValueOrDefault(l) + v;
            double? tz = null;
            foreach (var (_, k) in Array(_tz))
            {
                var c = k - 273.15;
                if (c is > 28.5 and < 125) tz = Math.Max(tz ?? 0, c); // 27.85 °C (301 K) is the classic dummy ACPI zone — not a real CPU reading
            }
            double? clock = null;
            var perf = Array(_perf).Select(x => x.Value).FirstOrDefault();
            var freq = Array(_freq).Select(x => x.Value).FirstOrDefault();
            if (perf > 0 && freq > 0) clock = freq * perf / 100.0;
            return new PdhSample(gpu, vram, tz is null ? null : Math.Round(tz.Value, 1), clock);
        }

        private List<(string Name, double Value)> Array(IntPtr counter)
        {
            var list = new List<(string, double)>();
            if (counter == IntPtr.Zero) return list;
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, FmtDouble | NoCap100, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, FmtDouble | NoCap100, ref size, out var count, buf) != 0) return list;
                var stride = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM_W: name ptr + {CStatus, pad, double}
                for (var i = 0; i < count; i++)
                {
                    var item = buf + i * stride;
                    var status = Marshal.ReadInt32(item + IntPtr.Size);
                    if (status is not (0 or 1)) continue;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    var v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                    if (double.IsFinite(v)) list.Add((name, v));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        public void Dispose() { if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; } }
    }

    /// <summary>"pid_12_luid_0x00000000_0x0000C2F3_phys_0_eng_3_engtype_3D" → "luid_0x00000000_0x0000C2F3".</summary>
    public static string? GpuLuid(string instance)
    {
        var i = instance.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || instance.Length < i + 26) return null;
        return instance.Substring(i, 26).ToLowerInvariant();
    }

    public static string FormatLuid(uint low, int high) => $"luid_0x{(uint)high:x8}_0x{low:x8}";

    /* ───────────── D3DKMT (GPU temperature / fan, Task Manager's source) ───────────── */

    private sealed record KmtReading(string Luid, string? Name, double? TempC, double? FanRpm);

    private sealed class Kmt : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters2 { public uint NumAdapters; public IntPtr Adapters; }
        [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint Handle; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int Precise; }
        [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
        [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Handle; }
        [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 e);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter c);
        private const int RegistryInfo = 8, PerfData = 62;

        private readonly List<(uint Handle, string Luid, string? Name)> _adapters = [];

        public static Kmt? TryCreate()
        {
            try
            {
                var e = new EnumAdapters2();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return null;
                var size = Marshal.SizeOf<AdapterInfo>();
                e.Adapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
                var k = new Kmt();
                try
                {
                    if (D3DKMTEnumAdapters2(ref e) != 0) return null;
                    for (var i = 0; i < e.NumAdapters; i++)
                    {
                        var a = Marshal.PtrToStructure<AdapterInfo>(e.Adapters + i * size);
                        var name = Name(a.Handle);
                        if (name?.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) == true) { Close(a.Handle); continue; }
                        k._adapters.Add((a.Handle, FormatLuid(a.LuidLow, a.LuidHigh), name));
                    }
                }
                finally { Marshal.FreeHGlobal(e.Adapters); }
                return k._adapters.Count > 0 ? k : null;
            }
            catch { return null; }
        }

        private static string? Name(uint handle)
        {
            const int size = 4 * 260 * 2;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var q = new QueryAdapterInfo { Handle = handle, Type = RegistryInfo, Data = buf, Size = size };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                var s = Marshal.PtrToStringUni(buf)?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static void Close(uint h) { try { var c = new CloseAdapter { Handle = h }; D3DKMTCloseAdapter(ref c); } catch { } }

        /// <summary>The discrete GPU wins (NVIDIA/AMD) — on a laptop the iGPU's temperature is not what a gamer wants.</summary>
        public KmtReading? Read()
        {
            KmtReading? best = null; var bestRank = -1;
            foreach (var (h, luid, name) in _adapters)
            {
                var (t, fan) = Perf(h);
                var n = name ?? "";
                var rank = (t is not null ? 2 : 0)
                         + (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || n.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                if (rank > bestRank) { bestRank = rank; best = new KmtReading(luid, name, t, fan); }
            }
            return best;
        }

        private static (double?, double?) Perf(uint handle)
        {
            const int size = 64; // D3DKMT_ADAPTER_PERFDATA
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                for (var i = 0; i < size; i += 4) Marshal.WriteInt32(buf + i, 0); // PhysicalAdapterIndex = 0
                var q = new QueryAdapterInfo { Handle = handle, Type = PerfData, Data = buf, Size = size };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return (null, null);
                var fan = (uint)Marshal.ReadInt32(buf + 48);
                var deci = (uint)Marshal.ReadInt32(buf + 56);
                double? t = deci is > 0 and < 1500 ? deci / 10.0 : null;
                return (t, fan is > 0 and < 20000 ? fan : null);
            }
            catch { return (null, null); }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose() { foreach (var a in _adapters) Close(a.Handle); _adapters.Clear(); }
    }

    /* ───────────── NVAPI (NVIDIA hot spot / memory junction) ───────────── */

    private sealed record NvReading(double? Core, double? Hotspot, double? Memory);

    private sealed unsafe class NvApi
    {
        private delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _thermal;
        private IntPtr _gpu;
        private uint _mask;

        public static NvApi? TryCreate()
        {
            try
            {
                if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return null;
                if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr)) return null;
                var qi = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qiPtr;
                var init = (delegate* unmanaged[Cdecl]<int>)qi(0x0150E828);
                var enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, int*, int>)qi(0xE5AC921F);
                var thermal = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)qi(0x65FE3AAD);
                if (init == null || enumGpus == null || thermal == null || init() != 0) return null;
                var handles = stackalloc IntPtr[64];
                int count;
                if (enumGpus(handles, &count) != 0 || count < 1) return null;
                var nv = new NvApi { _thermal = thermal, _gpu = handles[0] };
                // Find the widest sensor mask this GPU accepts (LibreHardwareMonitor's probe).
                uint mask = 0;
                for (var bit = 0; bit < 32; bit++)
                {
                    if (nv.Query(1u << bit, out _) != 0) { mask = bit == 0 ? 0 : (1u << bit) - 1; break; }
                    mask = bit == 31 ? uint.MaxValue : mask;
                }
                if (mask == 0) return null;
                nv._mask = mask;
                return nv;
            }
            catch { return null; }
        }

        private int Query(uint mask, out int[] temps)
        {
            const int size = 168; // NV_GPU_THERMAL_SENSORS v2: version, mask, 8 reserved, 32 temps
            var buf = stackalloc byte[size];
            new Span<byte>(buf, size).Clear();
            *(uint*)buf = size | (2u << 16);
            *(uint*)(buf + 4) = mask;
            var status = _thermal(_gpu, buf);
            temps = new int[32];
            for (var i = 0; i < 32; i++) temps[i] = *(int*)(buf + 40 + i * 4);
            return status;
        }

        public NvReading? Read()
        {
            try
            {
                if (Query(_mask, out var t) != 0) return null;
                static double? C(int raw) { var c = raw / 256.0; return c is > 1 and < 150 ? Math.Round(c, 1) : null; }
                return new NvReading(C(t[0]), C(t[1]), C(t[9]));
            }
            catch { return null; }
        }
    }
}

/// <summary>v0.4.38 — parser for MSI Afterburner's "MAHMSharedMemory" (MAHM v2 layout).</summary>
public static class AfterburnerParser
{
    public const uint Signature = 0x4D41484D; // 'MAHM'
    private const int NameLen = 260, DataOffset = NameLen * 5;

    public static ExternalSensors? Parse(ReadOnlySpan<byte> m)
    {
        if (m.Length < 32 || BitConverter.ToUInt32(m) != Signature) return null;
        var headerSize = (int)BitConverter.ToUInt32(m[8..]);
        var count = (int)BitConverter.ToUInt32(m[12..]);
        var entrySize = (int)BitConverter.ToUInt32(m[16..]);
        if (headerSize < 20 || entrySize < DataOffset + 4 || count is < 0 or > 4096) return null;
        var r = new ExternalSensors("Afterburner");
        for (var i = 0; i < count; i++)
        {
            var at = headerSize + i * entrySize;
            if (at + DataOffset + 4 > m.Length) break;
            var name = Str(m.Slice(at, NameLen));
            var v = BitConverter.ToSingle(m[(at + DataOffset)..]);
            if (!float.IsFinite(v) || v >= float.MaxValue / 2) continue;
            var n = name.ToLowerInvariant();
            double d = Math.Round(v, 1);
            if (n.Contains("hot spot") || n.Contains("hotspot")) r = r with { GpuHotspot = r.GpuHotspot ?? Temp(d) };
            else if (n.StartsWith("gpu") && n.Contains("temperature") && !n.Contains("memory")) r = r with { GpuTemp = r.GpuTemp ?? Temp(d) };
            else if (n.Contains("memory") && n.Contains("temperature")) r = r with { GpuMemTemp = r.GpuMemTemp ?? Temp(d) };
            else if (n == "cpu temperature") r = r with { CpuTemp = Temp(d) };
            else if (n == "framerate") r = r with { Fps = d > 0 ? d : null };
            else if (n == "cpu usage") r = r with { CpuUsage = d };
            else if (n.StartsWith("gpu") && n.EndsWith("usage") && !n.Contains("memory") && !n.Contains("bus") && !n.Contains("fb")) r = r with { GpuUsage = r.GpuUsage ?? d };
        }
        return r;
    }

    internal static double? Temp(double c) => c is > 1 and < 150 ? c : null;

    internal static string Str(ReadOnlySpan<byte> s)
    {
        var z = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(z < 0 ? s : s[..z]).Trim();
    }
}

/// <summary>v0.4.38 — parser for HWiNFO's sensor shared memory ("Global\HWiNFO_SENS_SM2", packed layout).</summary>
public static class HwInfoParser
{
    public const uint Signature = 0x53695748; // 'HWiS'
    private const int LabelLen = 128, UnitLen = 16;
    private const int LabelOrig = 12, LabelUser = LabelOrig + LabelLen, ValueOffset = LabelUser + LabelLen + UnitLen;

    public static ExternalSensors? Parse(ReadOnlySpan<byte> m)
    {
        if (m.Length < 44 || BitConverter.ToUInt32(m) != Signature) return null;
        var readOff = (int)BitConverter.ToUInt32(m[32..]);
        var readSize = (int)BitConverter.ToUInt32(m[36..]);
        var readCount = (int)BitConverter.ToUInt32(m[40..]);
        if (readSize < ValueOffset + 8 || readCount is < 0 or > 100000) return null;
        double? cpuPkg = null, cpuTctl = null, cpuMax = null, gpu = null, hot = null, mem = null, fps = null, cpuUse = null, gpuUse = null;
        for (var i = 0; i < readCount; i++)
        {
            var at = readOff + i * readSize;
            if (at + ValueOffset + 8 > m.Length) break;
            var type = BitConverter.ToInt32(m[at..]);
            var label = AfterburnerParser.Str(m.Slice(at + LabelOrig, LabelLen)).ToLowerInvariant();
            var v = BitConverter.ToDouble(m[(at + ValueOffset)..]);
            if (!double.IsFinite(v)) continue;
            v = Math.Round(v, 1);
            switch (type)
            {
                case 1: // temperature
                    if (label == "cpu package") cpuPkg ??= AfterburnerParser.Temp(v);
                    else if (label.StartsWith("cpu (tctl/tdie)")) cpuTctl ??= AfterburnerParser.Temp(v);
                    else if (label is "core max" or "cpu die (average)") cpuMax ??= AfterburnerParser.Temp(v);
                    else if (label.Contains("hot spot") || label.Contains("hotspot")) hot ??= AfterburnerParser.Temp(v);
                    else if (label.StartsWith("gpu memory junction")) mem ??= AfterburnerParser.Temp(v);
                    else if (label == "gpu temperature") gpu ??= AfterburnerParser.Temp(v);
                    break;
                case 7: // usage
                    if (label == "total cpu usage") cpuUse ??= v;
                    else if (label is "gpu core load" or "gpu utilization" or "gpu d3d usage") gpuUse ??= v;
                    break;
                case 8: // other
                    if (label.Contains("framerate") && v > 0) fps ??= v;
                    break;
            }
        }
        return new ExternalSensors("HWiNFO")
        {
            CpuTemp = cpuPkg ?? cpuTctl ?? cpuMax,
            GpuTemp = gpu,
            GpuHotspot = hot,
            GpuMemTemp = mem,
            Fps = fps,
            CpuUsage = cpuUse,
            GpuUsage = gpuUse,
        };
    }
}
