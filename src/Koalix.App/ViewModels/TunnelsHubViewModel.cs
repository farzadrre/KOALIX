using Koalix.App.Services;

namespace Koalix.App.ViewModels;

/// <summary>v0.4.50 — the advanced tunnels (WARP chain + Aether) got their own sidebar page («کلا از تو داشبورد
/// بیاری بیرون … تو همون یک ردیف هم warp قابل انتخاب باشه هم aether»). Deliberately a thin shell: both panels keep
/// binding to the DashboardViewModel singletons (same commands, same settings, zero rewiring); the hub only makes
/// sure the chosen panel is open when the page activates — nobody should land on an empty selector.</summary>
public sealed class TunnelsHubViewModel(DashboardViewModel dash) : PageViewModel
{
    public DashboardViewModel Dash => dash;

    public override void OnActivated() => dash.OpenTunnelsPanel();
}
