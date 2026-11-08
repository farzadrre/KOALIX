using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Koalix.App.Localization;

namespace Koalix.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    protected static Loc L => Loc.I;
    /// <summary>Called when the page becomes visible / hidden (pages stop polling while hidden to save CPU).</summary>
    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }
    protected static void Ui(Action a) { var d = Application.Current?.Dispatcher; if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a); }
}

public sealed record NavItem(string Key, string Glyph, PageViewModel Page);
