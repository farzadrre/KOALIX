using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Koalix.App.ViewModels;

namespace Koalix.App.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    private bool _entered;
    public DashboardView()
    {
        InitializeComponent();
        // v0.4.6: the dashboard cards fade/slide in one after another on first open — same motion language as the Gaming page.
        Loaded += (_, _) =>
        {
            if (_entered) return;
            _entered = true;
            Controls.Fx.Stagger(Cards, 0.05);
        };
        // v0.4.23: WASD walks the server cards. Preview tunnels from the UserControl, so it fires even while a card Button holds focus.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnCountryChipMouseEnter(object sender, MouseEventArgs e)
    {
        if (DataContext is DashboardViewModel vm && sender is FrameworkElement { DataContext: CountryRow row } && row.Code.Length == 2)
            vm.HoveredCountryCode = row.Code;
    }

    private void OnCountryChipMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            vm.HoveredCountryCode = null;
    }

    /* ═══════════ WASD server navigation (v0.4.23 — «تو داشبورد کاش کاری کنی که wasd کار کنه و روی سرورا بشه جا به جا شد») ═══════════ */

    /// <summary>v0.4.23: کلیدهای W/A/S/D (و کلیدهای جهت‌نما) انتخاب سرور را بین کاشی‌های داشبورد جابه‌جا می‌کنند —
    /// A/D (چپ/راست) یک کارت، W/S (بالا/پایین) یک ردیف کامل؛ درخواست کاربر: «wasd کار کنه و روی سرورا بشه جا به جا شد».</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm || vm.Profiles.Count == 0) return;
        // typing stays typing: if a TextBox/PasswordBox (or anything inside one) holds the keyboard, get out of the way.
        if (IsTextEntry(Keyboard.FocusedElement as DependencyObject)) return;

        var delta = e.Key switch
        {
            Key.D or Key.Right => +1,
            Key.A or Key.Left => -1,
            Key.S or Key.Down => +Columns(this, vm.Profiles),
            Key.W or Key.Up => -Columns(this, vm.Profiles),
            _ => 0,
        };
        if (delta == 0) return;

        var profiles = vm.Profiles;
        var current = vm.SelectedProfile is { } sel ? profiles.IndexOf(sel) : -1;
        var next = Math.Clamp(current + delta, 0, profiles.Count - 1);
        if (next == current) return;

        e.Handled = true;                                 // keep the default arrow-key focus moves from fighting us
        vm.SelectProfileCommand.Execute(profiles[next]);  // via the command — it does the extra selection work
        if (ProfilesList(this, profiles)?.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement card)
            card.BringIntoView();                         // null when virtualized — then just skip the scroll

        static bool IsTextEntry(DependencyObject? d)
        {
            while (d is not null)
            {
                if (d is TextBox or PasswordBox) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        // how many cards fit one visual row — card 235 + 10 margin (v0.4.3 ServerCard style) against the live viewport.
        static int Columns(DependencyObject root, object profiles)
        {
            var list = ProfilesList(root, profiles);
            var viewport = list is null ? 0.0 : Ancestor<ScrollViewer>(list)?.ViewportWidth ?? list.ActualWidth;
            return Math.Max(1, (int)Math.Floor((viewport + 10.0) / 245.0));
        }

        // the ItemsControl bound to Profiles — the dashboard hosts other ItemsControls (groups, usage, history), so match by ItemsSource identity.
        static ItemsControl? ProfilesList(DependencyObject root, object profiles)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(root, i) is not DependencyObject child) continue;
                if (child is ItemsControl ic && ReferenceEquals(ic.ItemsSource, profiles)) return ic;
                if (ProfilesList(child, profiles) is { } deeper) return deeper;
            }
            return null;
        }

        static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d is not null)
            {
                d = VisualTreeHelper.GetParent(d);
                if (d is T t) return t;
            }
            return null;
        }
    }
}
