using System.Windows;
using System.Windows.Media;
using Koalix.App.Services;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.38 — the animated dashboard background (user: «پس‌زمینهٔ متحرک داشبورد: ذرات یا موج ملایمی که با وصل شدن
/// جان می‌گیرد»). Slow drifting accent particles plus two soft sine waves along the bottom. Disconnected it barely
/// breathes; connected it wakes up — the particles speed up, brighten and link with faint lines, and the waves rise
/// and quicken with the live download speed (<see cref="ThemeService.LiveActivity"/>). Renders at ~30 fps only while
/// visible on screen, honours the Effects switch and its own Settings toggle, and never takes input.
/// </summary>
public sealed class LiveBackdrop : FrameworkElement
{
    public const string SettingKey = "ui.liveBg";
    /// <summary>The Settings toggle (default on). Effects off also stops it.</summary>
    public static bool Allowed { get; set; } = true;

    public static readonly DependencyProperty IsLiveProperty =
        DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(LiveBackdrop), new PropertyMetadata(false));
    /// <summary>Bound to the dashboard's IsConnected.</summary>
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    private struct Dot { public double X, Y, Vx, Vy, R, Phase; }
    private readonly Dot[] _dots = new Dot[34];
    private readonly Random _rnd = new(7);
    private bool _hooked;
    private TimeSpan _last;
    private double _t, _energy, _wave;

    public LiveBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        for (var i = 0; i < _dots.Length; i++)
            _dots[i] = new Dot
            {
                X = _rnd.NextDouble(), Y = _rnd.NextDouble(),
                Vx = (_rnd.NextDouble() - 0.5) * 0.012, Vy = (_rnd.NextDouble() - 0.5) * 0.010,
                R = 1.2 + _rnd.NextDouble() * 2.2, Phase = _rnd.NextDouble() * Math.PI * 2,
            };
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Loaded += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) { _last = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; }
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        var now = re.RenderingTime;
        if (_last != TimeSpan.Zero && (now - _last).TotalMilliseconds < 32) return; // ~30 fps is plenty for a backdrop
        var dt = _last == TimeSpan.Zero ? 0.033 : Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        var window = Window.GetWindow(this);
        if (window is { WindowState: WindowState.Minimized } || !Fx.Enabled || !Allowed)
        {
            if (Opacity > 0) InvalidateVisual();
            _energy = 0;
            return;
        }
        // energy glides toward the target: ~0.15 idle, 0.55-1 connected (more with traffic)
        var target = IsLive ? 0.55 + 0.45 * ThemeService.LiveActivity : 0.15;
        _energy += (target - _energy) * Math.Min(1, dt * 1.6);
        _wave += dt * (0.5 + 2.2 * _energy);
        _t += dt;
        var speed = 0.35 + 2.4 * _energy;
        for (var i = 0; i < _dots.Length; i++)
        {
            ref var d = ref _dots[i];
            d.X += d.Vx * speed * dt * 4; d.Y += d.Vy * speed * dt * 4;
            if (d.X < -0.05) d.X = 1.05; else if (d.X > 1.05) d.X = -0.05;
            if (d.Y < -0.05) d.Y = 1.05; else if (d.Y > 1.05) d.Y = -0.05;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 10 || h < 10 || !Fx.Enabled || !Allowed) return;
        var accent = TryFindResource("AccentColor") is Color a ? a : Color.FromRgb(0x7C, 0x5C, 0xFF);
        var accent2 = TryFindResource("Accent2Color") is Color a2 ? a2 : accent;
        var e = _energy;

        // particles + constellation links (links only once awake)
        var linkDist = Math.Min(w, h) * 0.18;
        if (e > 0.3)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(40 * (e - 0.3) / 0.7), accent.R, accent.G, accent.B)), 0.8);
            pen.Freeze();
            for (var i = 0; i < _dots.Length; i++)
                for (var j = i + 1; j < _dots.Length; j++)
                {
                    var dx = (_dots[i].X - _dots[j].X) * w; var dy = (_dots[i].Y - _dots[j].Y) * h;
                    if (dx * dx + dy * dy < linkDist * linkDist)
                        dc.DrawLine(pen, new Point(_dots[i].X * w, _dots[i].Y * h), new Point(_dots[j].X * w, _dots[j].Y * h));
                }
        }
        for (var i = 0; i < _dots.Length; i++)
        {
            var d = _dots[i];
            var tw = 0.6 + 0.4 * Math.Sin(_t * (0.8 + e * 2) + d.Phase);
            var alpha = (byte)Math.Clamp((22 + 90 * e) * tw, 0, 255);
            var c = i % 3 == 0 ? accent2 : accent;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze();
            var r = d.R * (0.8 + 0.5 * e);
            dc.DrawEllipse(b, null, new Point(d.X * w, d.Y * h), r, r);
        }

        // two soft waves along the bottom
        for (var k = 0; k < 2; k++)
        {
            var amp = h * (0.018 + 0.045 * e) * (k == 0 ? 1 : 0.7);
            var baseY = h * (0.86 - 0.05 * e - k * 0.035);
            var c = k == 0 ? accent : accent2;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                const int steps = 48;
                for (var s = 0; s <= steps; s++)
                {
                    var x = w * s / steps;
                    var y = baseY + amp * Math.Sin(x / w * Math.PI * (2.2 + k) + _wave * (k == 0 ? 1 : -1.3) + k);
                    ctx.LineTo(new Point(x, y), true, true);
                }
                ctx.LineTo(new Point(w, h), true, true);
            }
            g.Freeze();
            var fill = new LinearGradientBrush(Color.FromArgb((byte)(18 + 34 * e), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, g);
        }
    }
}
