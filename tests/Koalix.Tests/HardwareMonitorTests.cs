using System.Text;
using Koalix.SystemIntegration;
using Xunit.Abstractions;

namespace Koalix.Tests;

/// <summary>v0.4.38 — hardware telemetry: shared-memory parsers (Afterburner / HWiNFO), merge rules, and a live smoke sample.</summary>
public class HardwareMonitorTests(ITestOutputHelper output)
{
    private static void Put(byte[] b, int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, at);

    private static byte[] Afterburner(params (string Name, float Value)[] entries)
    {
        const int header = 32, entry = 260 * 5 + 4 * 6;
        var b = new byte[header + entries.Length * entry];
        BitConverter.GetBytes(AfterburnerParser.Signature).CopyTo(b, 0);
        BitConverter.GetBytes(0x00020000u).CopyTo(b, 4);
        BitConverter.GetBytes((uint)header).CopyTo(b, 8);
        BitConverter.GetBytes((uint)entries.Length).CopyTo(b, 12);
        BitConverter.GetBytes((uint)entry).CopyTo(b, 16);
        for (var i = 0; i < entries.Length; i++)
        {
            Put(b, header + i * entry, entries[i].Name);
            BitConverter.GetBytes(entries[i].Value).CopyTo(b, header + i * entry + 1300);
        }
        return b;
    }

    [Fact]
    public void Afterburner_maps_temperatures_hotspot_and_framerate()
    {
        var r = AfterburnerParser.Parse(Afterburner(("GPU temperature", 61), ("GPU hot spot temperature", 74.4f), ("Memory temperature", 70),
            ("CPU temperature", 55), ("Framerate", 144), ("GPU usage", 97), ("CPU usage", 23), ("Fan speed", 1500)));
        Assert.NotNull(r);
        Assert.Equal(61, r!.GpuTemp);
        Assert.Equal(74.4, r.GpuHotspot);
        Assert.Equal(70, r.GpuMemTemp);
        Assert.Equal(55, r.CpuTemp);
        Assert.Equal(144, r.Fps);
        Assert.Equal(97, r.GpuUsage);
        Assert.Equal(23, r.CpuUsage);
    }

    [Fact]
    public void Afterburner_rejects_bad_signature_and_skips_unavailable_values()
    {
        var b = Afterburner(("GPU temperature", float.MaxValue), ("Framerate", 0));
        var r = AfterburnerParser.Parse(b);
        Assert.NotNull(r);
        Assert.True(r!.IsEmpty);
        b[0] = 0xAD; b[1] = 0xDE;
        Assert.Null(AfterburnerParser.Parse(b));
        Assert.Null(AfterburnerParser.Parse(new byte[8]));
    }

    private static byte[] HwInfo(params (int Type, string Label, double Value)[] readings)
    {
        const int header = 48, size = 12 + 128 + 128 + 16 + 8 * 4;
        var b = new byte[header + readings.Length * size];
        BitConverter.GetBytes(HwInfoParser.Signature).CopyTo(b, 0);
        BitConverter.GetBytes((uint)header).CopyTo(b, 32);
        BitConverter.GetBytes((uint)size).CopyTo(b, 36);
        BitConverter.GetBytes((uint)readings.Length).CopyTo(b, 40);
        for (var i = 0; i < readings.Length; i++)
        {
            var at = header + i * size;
            BitConverter.GetBytes(readings[i].Type).CopyTo(b, at);
            Put(b, at + 12, readings[i].Label);
            BitConverter.GetBytes(readings[i].Value).CopyTo(b, at + 12 + 128 + 128 + 16);
        }
        return b;
    }

    [Fact]
    public void HwInfo_prefers_cpu_package_and_reads_gpu_hotspot()
    {
        var r = HwInfoParser.Parse(HwInfo((1, "Core Max", 80), (1, "CPU Package", 67.25), (1, "GPU Temperature", 58),
            (1, "GPU Hot Spot Temperature", 71), (1, "GPU Memory Junction Temperature", 76), (7, "Total CPU Usage", 12.5), (7, "GPU Core Load", 88)));
        Assert.NotNull(r);
        Assert.Equal(67.2, r!.CpuTemp!.Value, 1);
        Assert.Equal(58, r.GpuTemp);
        Assert.Equal(71, r.GpuHotspot);
        Assert.Equal(76, r.GpuMemTemp);
        Assert.Equal(12.5, r.CpuUsage);
        Assert.Equal(88, r.GpuUsage);
    }

    [Fact]
    public void HwInfo_amd_tctl_is_the_cpu_temperature_fallback()
    {
        var r = HwInfoParser.Parse(HwInfo((1, "CPU (Tctl/Tdie)", 72), (2, "CPU Package", 1.2)));
        Assert.Equal(72, r!.CpuTemp);
    }

    [Fact]
    public void Merge_lets_external_temps_win_but_keeps_native_loads()
    {
        var native = new HardwareSnapshot { CpuUsage = 10, GpuUsage = 50, GpuTemp = 40, CpuTemp = 27.8 };
        var m = HardwareMonitor.Merge(native, new ExternalSensors("HWiNFO") { CpuTemp = 65, GpuHotspot = 70, CpuUsage = 99 });
        Assert.Equal(65, m.CpuTemp);
        Assert.Equal(40, m.GpuTemp);
        Assert.Equal(70, m.GpuHotspot);
        Assert.Equal(10, m.CpuUsage);
        Assert.Equal("HWiNFO", m.Source);
    }

    [Fact]
    public void Gpu_luid_is_extracted_from_counter_instances()
    {
        Assert.Equal("luid_0x00000000_0x0000c2f3", HardwareMonitor.GpuLuid("pid_1234_luid_0x00000000_0x0000C2F3_phys_0_eng_0_engtype_3D"));
        Assert.Equal("luid_0x00000000_0x0000c2f3", HardwareMonitor.FormatLuid(0xC2F3, 0));
        Assert.Null(HardwareMonitor.GpuLuid("_Total"));
    }

    [Fact]
    public void Gpu_load_is_busiest_engine_not_sum_of_all_engines()
    {
        const string L = "luid_0x00000000_0x0000C2F3";
        var r = HardwareMonitor.AggregateGpuLoad(
        [
            ($"pid_10_{L}_phys_0_eng_0_engtype_3D", 30), // game
            ($"pid_20_{L}_phys_0_eng_0_engtype_3D", 15), // dwm on the same engine → adds
            ($"pid_10_{L}_phys_0_eng_5_engtype_3D", 40), // a second 3D engine → max, not sum
            ("pid_30_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 80),
            ($"pid_40_{L}_phys_0_eng_9_engtype_3D", 250), // driver glitch → clamped
        ]);
        Assert.Equal(100, r["luid_0x00000000_0x0000c2f3"]);
        Assert.Equal(80, r["luid_0x00000000_0x0000aaaa"]);
        var r2 = HardwareMonitor.AggregateGpuLoad([($"pid_10_{L}_phys_0_eng_0_engtype_3D", 30), ($"pid_20_{L}_phys_0_eng_0_engtype_3D", 15), ($"pid_10_{L}_phys_0_eng_5_engtype_3D", 40)]);
        Assert.Equal(45, r2["luid_0x00000000_0x0000c2f3"]);
    }

    [Fact]
    public void Live_sample_never_throws()
    {
        using var hw = new HardwareMonitor();
        hw.Sample();
        Thread.Sleep(600);
        var s = hw.Sample();
        output.WriteLine($"CPU {s.CpuUsage:0.0}% {s.CpuTemp}°C {s.CpuClockMhz:0} MHz | RAM {s.RamUsage}% {s.RamUsedGb:0.0}/{s.RamTotalGb:0.0} GB");
        output.WriteLine($"GPU '{s.GpuName}' {s.GpuUsage:0.0}% {s.GpuTemp}°C hot {s.GpuHotspot} mem {s.GpuMemTemp} VRAM {s.VramUsedGb:0.00} GB fan {s.GpuFanRpm} | FPS {s.Fps} | src {s.Source}");
        Assert.True(s.RamTotalGb > 0);
        Assert.NotNull(s.CpuUsage);
    }
}
