using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Koalix.App.Controls;
using Koalix.App.Localization;

namespace Koalix.App.Views;

/// <summary>
/// v0.4.22 — the first-run starter tour (user: «برای کسایی که اولین بار وارد میشن یک بخش بزار که اموزشی طور باشه
/// و کامل بهشون توضیح بده … با طراحی زیبا و قشنگ و موشن و انیمیشن»). Six steps — welcome, install engines,
/// add configs, selective tunneling, connect, the toolbox — each with a LIVE animated visual (the same motion
/// language as the app: ECG, shuttling arrows, rising bars). Auto-opens on the very first launch; re-openable
/// any time from Settings («دیدن دوبارهٔ راهنمای شروع»).
/// </summary>
public partial class OnboardingView : UserControl
{
    /// <summary>Raised when the tour is closed (finished or skipped) — the host marks ui.onboarded.</summary>
    public event Action? Closed;
    /// <summary>Raised when the user takes the «برو به نصب موتورها» shortcut — the host navigates to Settings.</summary>
    public event Action? EnginesRequested;

    private int _index = -1;
    private readonly Action[] _builders;

    public OnboardingView()
    {
        InitializeComponent();
        _builders = [Welcome, Engines, AddConfig, Split, Connect, WidgetStep, GamingStep, LooksStep, ShortcutsStep, Toolbox]; // v0.4.38: +4 steps
        // build the step only when the overlay actually becomes visible — a hidden tour must not animate
        // (Loaded fires for collapsed elements too, and the emblems would run behind the overlay forever).
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Show(0);
            else VisualHost.Children.Clear(); // unloads the emblems → their storyboards stop
        };
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _builders.Length - 1);
        _builders[_index]();

        BackBtn.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = _index == _builders.Length - 1 ? Loc.I["Tour.Finish"] : Loc.I["Tour.Next"];
        GotoEngines.Visibility = _index == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepBadgeText.Text = Loc.I.Format("Tour.StepOf", _index + 1, _builders.Length);

        // progress dots — passed ones filled, the current one pulsing
        Dots.Children.Clear();
        for (var i = 0; i < _builders.Length; i++)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 0, 7, 0) };
            if (i < _index) { dot.Background = FindResource("AccentBrush") as Brush; dot.Opacity = 0.55; }
            else if (i == _index)
            {
                dot.Background = FindResource("AccentBrush") as Brush;
                if (Fx.Enabled)
                {
                    var pulse = new DoubleAnimation(0.45, 1, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                    dot.BeginAnimation(OpacityProperty, pulse);
                }
            }
            else dot.Background = FindResource("BorderBrush") as Brush;
            Dots.Children.Add(dot);
        }
    }

    private void SetText(string titleKey, string bodyKey)
    {
        Title.Text = Loc.I[titleKey];
        Body.Text = Loc.I[bodyKey];
    }

    /// <summary>Swaps the step content with a soft rise+fade (the app's slide-in motion language).</summary>
    private void SwapVisual(UIElement visual)
    {
        VisualHost.Children.Clear();
        VisualHost.Children.Add(visual);
        if (Fx.Enabled && visual is FrameworkElement fe) Fx.SlideIn(fe, 14, 0.32);
    }

    private TextBlock Icon(char glyph, double size, Brush? brush = null)
    {
        var t = new TextBlock
        {
            Text = glyph.ToString(),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (brush is not null) t.Foreground = brush;
        return t;
    }

    private Brush Res(string key) => FindResource(key) as Brush ?? Brushes.Gray;

    /* ─── step visuals — every one of them MOVES (the user loves motion) ─── */

    // 1 — خوش‌آمدی: the koala in a breathing accent halo
    private void Welcome()
    {
        SetText("Tour.Step1Title", "Tour.Step1Text");
        var grid = new Grid();
        var halo = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(26), Background = Res("AccentBrush"), Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        halo.Effect = new BlurEffect { Radius = 22 };
        var img = new Image { Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var src = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/KOALIX;component/Assets/koalix.png"));
        img.Source = src;
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var scale = new ScaleTransform(1, 1);
        img.RenderTransform = scale;
        img.RenderTransformOrigin = new Point(0.5, 0.5);
        grid.Children.Add(halo);
        grid.Children.Add(img);
        if (Fx.Enabled)
        {
            var breathe = new DoubleAnimation(0.28, 0.5, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            halo.BeginAnimation(OpacityProperty, breathe);
            var beat = new DoubleAnimation(1, 1.045, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
        }
        SwapVisual(grid);
    }

    // 2 — نصب موتورها: a rotating gear + the four engine chips
    private void Engines()
    {
        SetText("Tour.Step2Title", "Tour.Step2Text");
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var gearHost = new Grid { Width = 54, Height = 54, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        var gear = Icon('\uE713', 46, Res("AccentBrush"));
        var rt = new RotateTransform(0);
        gear.RenderTransform = rt;
        gear.RenderTransformOrigin = new Point(0.5, 0.5);
        gearHost.Children.Add(gear);
        if (Fx.Enabled)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever };
            rt.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var name in new[] { "sing-box", "Xray", "WireGuard", "OpenVPN" })
        {
            var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(4, 0, 4, 0), Background = Res("ControlBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1) };
            chip.Child = new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("LatinFont") };
            chips.Children.Add(chip);
        }
        sp.Children.Add(gearHost);
        sp.Children.Add(chips);
        SwapVisual(sp);
    }

    // 3 — افزودن کانفیگ: a breathing plus tile + the shuttling import arrows
    private void AddConfig()
    {
        SetText("Tour.Step3Title", "Tour.Step3Text");
        var grid = new Grid();
        var tile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(16), Background = Res("AccentSoftBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Icon('\uE710', 26, Res("AccentBrush")) };
        var st = new ScaleTransform(1, 1);
        tile.RenderTransform = st;
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        grid.Children.Add(tile);
        var arrows = new SectionEmblem { Kind = "Arrows", Width = 150, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
        grid.Children.Add(arrows);
        if (Fx.Enabled)
        {
            var pop = new DoubleAnimation(0.92, 1.03, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        SwapVisual(grid);
    }

    // 4 — تونل‌زنی انتخابی: app chips + a packet gliding into the tunnel mouth
    private void Split()
    {
        SetText("Tour.Step4Title", "Tour.Step4Text");
        var grid = new Grid();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var apps = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var g in new[] { '\uE7FC', '\uE8B8', '\uE77B' }) // controller, browser, chat
            apps.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Res("ControlBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 3, 0, 3), Child = Icon(g, 16, Res("SubtleTextBrush")) });
        var tunnel = new Border { Width = 74, Height = 58, CornerRadius = new CornerRadius(16), Background = Res("AccentSoftBrush"), BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(1.4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0), Child = Icon('\uE8AB', 28, Res("AccentBrush")) };
        row.Children.Add(apps);
        row.Children.Add(tunnel);
        grid.Children.Add(row);
        if (Fx.Enabled)
        {
            // a packet leaves the app stack and glides into the tunnel — the whole point of selective tunneling
            var packet = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2.5), Background = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0 };
            var shift = new TranslateTransform(16, 0);
            packet.RenderTransform = shift;
            grid.Children.Add(packet);
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var glide = new DoubleAnimation(0, 150, TimeSpan.FromSeconds(1.5)) { BeginTime = TimeSpan.FromSeconds(0.25), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)) { BeginTime = TimeSpan.FromSeconds(0.25) };
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3)) { BeginTime = TimeSpan.FromSeconds(1.45) };
            Storyboard.SetTarget(glide, shift); Storyboard.SetTargetProperty(glide, new PropertyPath(TranslateTransform.XProperty));
            Storyboard.SetTarget(fade, packet); Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            Storyboard.SetTarget(fadeOut, packet); Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
            sb.Children.Add(glide); sb.Children.Add(fade); sb.Children.Add(fadeOut);
            sb.Begin(this, true);
        }
        SwapVisual(grid);
    }

    // 5 — اتصال: the live ECG strip — the connection as a heartbeat
    private void Connect()
    {
        SetText("Tour.Step5Title", "Tour.Step5Text");
        var grid = new Grid();
        grid.Children.Add(new SectionEmblem { Kind = "Ecg", Width = 220, Height = 46, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        SwapVisual(grid);
    }

    /* ─── v0.4.38 — four more steps (user: «قسمت اموزش هم بهش چن تا چیز بیشتر میتونی اضافه کنی») ─── */

    private Border Chip(string text, Brush? fg = null) => new()
    {
        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 4, 0),
        Background = Res("ControlBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("LatinFont"), Foreground = fg ?? Res("TextBrush") },
    };

    private static void Pulse(UIElement el, double from, double to, double seconds)
    {
        if (!Fx.Enabled) return;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    // 6 — ویجت شناور: a tiny Afterburner-style OSD plate with live-looking numbers
    private void WidgetStep()
    {
        SetText("Tour.Step7Title", "Tour.Step7Text");
        var plate = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 14, 9), Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x07, 0x08, 0x0E)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plate.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.4 };
        var lines = new StackPanel();
        var mono = (FontFamily)FindResource("MonoFont");
        TextBlock Line(string label, string value, string brush)
        {
            var t = new TextBlock { FontFamily = mono, FontSize = 13, Margin = new Thickness(0, 1, 0, 1) };
            t.Inlines.Add(new System.Windows.Documents.Run(label.PadRight(6)) { Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB8)) });
            t.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = Res(brush), FontWeight = FontWeights.SemiBold });
            return t;
        }
        lines.Children.Add(Line("GPU", "64°C  HOT 78°", "WarningBrush"));
        lines.Children.Add(Line("CPU", "52°C  31%", "SuccessBrush"));
        lines.Children.Add(Line("PING", "48 ms", "SuccessBrush"));
        var net = Line("NET", "↓ 4.2 MB/s", "AccentBrush");
        lines.Children.Add(net);
        plate.Child = lines;
        Pulse(net, 0.55, 1, 0.9);
        if (Fx.Enabled)
        {
            var bob = new TranslateTransform();
            plate.RenderTransform = bob;
            bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, 3, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        SwapVisual(plate);
    }

    // 7 — گیمینگ خودکار: a controller that lights the boost chips when a game starts
    private void GamingStep()
    {
        SetText("Tour.Step8Title", "Tour.Step8Text");
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var pad = Icon('\uE7FC', 44, Res("AccentBrush"));
        pad.HorizontalAlignment = HorizontalAlignment.Center;
        pad.Margin = new Thickness(0, 0, 0, 12);
        Pulse(pad, 0.6, 1, 0.8);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var i = 0;
        foreach (var name in new[] { "Auto", "Boost", "Low ping", "Focus" })
        {
            var c = Chip(name, Res("AccentBrush"));
            chips.Children.Add(c);
            if (Fx.Enabled)
                c.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.7)) { BeginTime = TimeSpan.FromSeconds(0.25 * i++), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        sp.Children.Add(pad);
        sp.Children.Add(chips);
        SwapVisual(sp);
    }

    // 8 — ظاهر: three mini cards in the neon / minimal / retro styles + a hue-cycling ARGB dot
    private void LooksStep()
    {
        SetText("Tour.Step9Title", "Tour.Step9Text");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var accent = FindResource("AccentColor") is Color ac ? ac : Color.FromRgb(0x7C, 0x5C, 0xFF);
        Border Mini(double radius, Brush border, double thick, string label, Effect? fx)
        {
            var b = new Border { Width = 74, Height = 54, CornerRadius = new CornerRadius(radius), Margin = new Thickness(6, 0, 6, 0), Background = Res("ControlBrush"), BorderBrush = border, BorderThickness = new Thickness(thick), Effect = fx };
            b.Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("LatinFont") };
            return b;
        }
        var neon = Mini(16, Res("AccentBrush"), 1.4, "Neon", Fx.Enabled ? new DropShadowEffect { Color = accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.6 } : null);
        row.Children.Add(neon);
        row.Children.Add(Mini(6, Res("BorderBrush"), 1, "Minimal", null));
        row.Children.Add(Mini(2, Res("AccentBrush"), 2, "Retro", null));
        Pulse(neon, 0.7, 1, 1.1);
        var grid = new Grid();
        grid.Children.Add(row);
        if (Fx.Enabled)
        {
            // the ARGB idea in one dot: a ring sweeping the hue wheel
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -4) };
            var brush = new SolidColorBrush(Colors.Red);
            dot.Background = brush;
            var hue = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(3) };
            var stops = new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red };
            for (var k = 0; k < stops.Length; k++)
                hue.KeyFrames.Add(new LinearColorKeyFrame(stops[k], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.0 * k / (stops.Length - 1)))));
            brush.BeginAnimation(SolidColorBrush.ColorProperty, hue);
            grid.Children.Add(dot);
        }
        SwapVisual(grid);
    }

    // 9 — میانبرها: the Ctrl + Alt + K keycaps pressing in sequence, plus the tray icon
    private void ShortcutsStep()
    {
        SetText("Tour.Step10Title", "Tour.Step10Text");
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        var n = 0;
        foreach (var k in new[] { "Ctrl", "+", "Alt", "+", "K" })
        {
            if (k == "+") { keys.Children.Add(new TextBlock { Text = "+", FontSize = 16, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("SubtleTextBrush") }); continue; }
            var cap = new Border
            {
                MinWidth = 46, Height = 40, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 10, 0),
                Background = Res("ControlBrush"), BorderBrush = Res("StrongBorderBrush"), BorderThickness = new Thickness(1, 1, 1, 3),
                Child = new TextBlock { Text = k, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = (FontFamily)FindResource("LatinFont") },
            };
            var press = new TranslateTransform();
            cap.RenderTransform = press;
            keys.Children.Add(cap);
            if (Fx.Enabled)
            {
                var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.4) };
                var t0 = 0.3 * n++;
                a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t0))));
                a.KeyFrames.Add(new LinearDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t0 + 0.08))));
                a.KeyFrames.Add(new DiscreteDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));
                a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
                press.BeginAnimation(TranslateTransform.YProperty, a);
            }
        }
        sp.Children.Add(keys);
        var tray = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var g in new[] { '\uE7E7', '\uE9D9', '\uE713' }) // tray / widget / settings
            tray.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Res("ControlBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(3, 0, 3, 0), Child = Icon(g, 14, Res("SubtleTextBrush")) });
        sp.Children.Add(tray);
        SwapVisual(sp);
    }

    // 10 — جعبه‌ابزار: the rising/falling report bars + the section tool chips
    private void Toolbox()
    {
        SetText("Tour.Step6Title", "Tour.Step6Text");
        var grid = new Grid();
        grid.Children.Add(new SectionEmblem { Kind = "Bars", Width = 220, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var g in new[] { '\uE968', '\uE9D9', '\uEC05', '\uE9F9', '\uE7FC', '\uE90F' }) // dns, inspector, ping, reports, gaming, repair
            chips.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Res("ControlBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(3, 0, 3, 0), Child = Icon(g, 14, Res("SubtleTextBrush")) });
        grid.Children.Add(chips);
        SwapVisual(grid);
    }

    /* ─── navigation ─── */

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index == _builders.Length - 1) { Closed?.Invoke(); return; }
        Show(_index + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(_index - 1);
    private void Skip_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();
    private void GotoEngines_Click(object sender, RoutedEventArgs e) => EnginesRequested?.Invoke();

    /// <summary>Esc = skip, ←/→ navigate (RTL-aware: in fa right goes back, left goes forward).</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Closed?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Right) { if (_index > 0) Show(_index - 1); e.Handled = true; }
        else if (e.Key == Key.Left) { if (_index < _builders.Length - 1) Show(_index + 1); e.Handled = true; }
    }
}
