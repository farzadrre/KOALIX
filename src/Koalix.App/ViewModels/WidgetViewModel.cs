using CommunityToolkit.Mvvm.ComponentModel;
using Koalix.App.Localization;
using Koalix.Core;
using Koalix.SystemIntegration;

namespace Koalix.App.ViewModels;

/// <summary>
/// v0.4.38 — what the floating widget shows (user: «ویجت شناور کوچک … چندین حالت داشته باشه بشه انتخاب کرد» +
/// «مث msi afterburner … دما cpu دما gpu و دما gpu hotspot»). Five modes: compact (one line), network, hardware,
/// full (both) and an Afterburner-style gaming OSD. Every number is pre-formatted here; levels 0-3 drive the
/// colour (0 neutral · 1 good · 2 warm · 3 hot) through <see cref="Views.LevelBrushConverter"/>.
/// </summary>
public sealed partial class WidgetViewModel : ObservableObject
{
    public static readonly string[] Modes = ["compact", "network", "hardware", "full", "osd"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeader), nameof(ShowCompact), nameof(ShowNet), nameof(ShowHw), nameof(ShowOsd), nameof(NeedsHardware))]
    private string _mode = "full";

    public bool ShowHeader => Mode is "network" or "hardware" or "full";
    public bool ShowCompact => Mode == "compact";
    public bool ShowNet => Mode is "network" or "full";
    public bool ShowHw => Mode is "hardware" or "full";
    public bool ShowOsd => Mode == "osd";
    public bool NeedsHardware => Mode is "hardware" or "full" or "osd";

    [ObservableProperty] private int _stateLevel;          // 0 off · 1 connecting · 2 connected · 3 error
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _ping = "—";
    [ObservableProperty] private int _pingLevel;
    [ObservableProperty] private string _pingCaption = "";
    [ObservableProperty] private string _down = "0 B/s";
    [ObservableProperty] private string _up = "0 B/s";

    [ObservableProperty] private string _cpuLoad = "—";
    [ObservableProperty] private double _cpuLoadValue;
    [ObservableProperty] private string _cpuTemp = "—";
    [ObservableProperty] private int _cpuTempLevel;
    [ObservableProperty] private string _cpuClock = "";
    [ObservableProperty] private string _gpuLoad = "—";
    [ObservableProperty] private double _gpuLoadValue;
    [ObservableProperty] private string _gpuTemp = "—";
    [ObservableProperty] private int _gpuTempLevel;
    [ObservableProperty] private string _gpuHot = "—";
    [ObservableProperty] private int _gpuHotLevel;
    [ObservableProperty] private string _gpuMem = "";
    [ObservableProperty] private string _gpuName = "GPU";
    [ObservableProperty] private string _ram = "—";
    [ObservableProperty] private double _ramValue;
    [ObservableProperty] private string _vram = "";
    [ObservableProperty] private string _fps = "";
    [ObservableProperty] private bool _hasFps;
    [ObservableProperty] private string _footer = "";

    private static Loc L => Loc.I;

    public void SetNetwork(ConnectionState state, string? server, double? pingMs, bool tunnelPing, long downBps, long upBps)
    {
        (StateLevel, StateText) = state switch
        {
            ConnectionState.Connected or ConnectionState.Degraded => (2, string.IsNullOrEmpty(server) ? L["Widget.On"] : server!),
            ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnecting => (1, L["Widget.Connecting"]),
            ConnectionState.Error => (3, L["Widget.Error"]),
            _ => (0, L["Widget.Off"]),
        };
        Ping = pingMs is { } p ? $"{p:0} ms" : "—";
        PingLevel = pingMs switch { null => 0, < 120 => 1, < 250 => 2, _ => 3 };
        PingCaption = tunnelPing ? L["Widget.Ping"] : L["Widget.PingDirect"];
        Down = DashboardViewModel.Fmt(downBps) + "/s";
        Up = DashboardViewModel.Fmt(upBps) + "/s";
    }

    public void SetHardware(HardwareSnapshot s)
    {
        CpuLoad = Pct(s.CpuUsage); CpuLoadValue = s.CpuUsage ?? 0;
        CpuTemp = Temp(s.CpuTemp); CpuTempLevel = TempLevel(s.CpuTemp, 70, 88);
        CpuClock = s.CpuClockMhz is { } mhz ? $"{mhz / 1000:0.00} GHz" : "";
        GpuLoad = Pct(s.GpuUsage); GpuLoadValue = s.GpuUsage ?? 0;
        GpuTemp = Temp(s.GpuTemp); GpuTempLevel = TempLevel(s.GpuTemp, 70, 83);
        GpuHot = Temp(s.GpuHotspot); GpuHotLevel = TempLevel(s.GpuHotspot, 85, 100);
        GpuMem = s.GpuMemTemp is { } m ? $"MEM {m:0}°" : "";
        GpuName = ShortGpu(s.GpuName);
        Ram = s.RamTotalGb > 0 ? $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB" : "—"; RamValue = s.RamUsage ?? 0;
        Vram = s.VramUsedGb is { } v ? $"VRAM {v:0.0} GB" : "";
        HasFps = s.Fps is > 0;
        Fps = s.Fps is { } f ? $"{f:0}" : "";
        Footer = s.CpuTemp is null ? L["Widget.TempHint"] : s.Source == "Windows" ? "" : L.Format("Widget.Via", s.Source);
    }

    public static string Pct(double? v) => v is { } x ? $"{x:0}%" : "—";
    public static string Temp(double? v) => v is { } x ? $"{x:0}°C" : "—";
    public static int TempLevel(double? v, double warm, double hot) => v switch { null => 0, _ when v < warm => 1, _ when v < hot => 2, _ => 3 };

    /// <summary>"NVIDIA GeForce RTX 3070" → "RTX 3070" — the widget is small.</summary>
    public static string ShortGpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "GPU";
        var n = name.Replace("NVIDIA", "").Replace("GeForce", "").Replace("AMD", "").Replace("Radeon(TM)", "Radeon").Replace("(R)", "").Replace("(TM)", "").Trim();
        while (n.Contains("  ")) n = n.Replace("  ", " ");
        return n.Length > 18 ? n[..18] : n.Length == 0 ? "GPU" : n;
    }
}
