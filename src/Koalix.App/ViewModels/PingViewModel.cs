using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.Dns;

namespace Koalix.App.ViewModels;

public sealed partial class PingViewModel(IIcmpProber prober) : PageViewModel
{
    private PingMonitor? _monitor;
    public ObservableCollection<double?> Samples { get; } = [];
    public IReadOnlyList<string> Presets { get; } = ["1.1.1.1", "8.8.8.8", "9.9.9.9", "4.2.2.4"];
    [ObservableProperty] private string _target = "1.1.1.1";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _current = "—";
    [ObservableProperty] private string _min = "—";
    [ObservableProperty] private string _avg = "—";
    [ObservableProperty] private string _max = "—";
    [ObservableProperty] private string _jitter = "—";
    [ObservableProperty] private string _loss = "—";
    [ObservableProperty] private string _quality = "";

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_monitor is not null) { await _monitor.DisposeAsync(); _monitor = null; Running = false; return; }
        IPAddress ip;
        if (!IPAddress.TryParse(Target.Trim(), out ip!))
        {
            try { ip = (await System.Net.Dns.GetHostAddressesAsync(Target.Trim())).First(); }
            catch { StatusMessage = L["Ping.BadTarget"]; return; }
        }
        Samples.Clear();
        _monitor = new PingMonitor(prober, ip);
        _monitor.Sampled += (s, st) => Ui(() =>
        {
            Samples.Add(s.Ms); while (Samples.Count > 120) Samples.RemoveAt(0);
            Current = s.Ms is { } m ? $"{m:0} ms" : L["Ping.Timeout"];
            Min = F(st.MinMs); Avg = F(st.AvgMs); Max = F(st.MaxMs); Jitter = F(st.JitterMs); Loss = $"{st.LossPercent:0.0}%";
            Quality = st.AvgMs is null ? "" : L[st.AvgMs < 50 && st.JitterMs < 10 && st.LossPercent < 1 ? "Ping.Excellent" : st.AvgMs < 100 && st.LossPercent < 3 ? "Ping.Good" : "Ping.Poor"];
        });
        _monitor.Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Running = true; StatusMessage = null;
    }

    private static string F(double? v) => v is { } x ? $"{x:0.0} ms" : "—";
    public override void OnDeactivated() { } // keep monitoring in background when user navigates away
}
