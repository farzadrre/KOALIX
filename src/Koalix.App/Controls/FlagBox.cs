using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Koalix.App.Services;
using Koalix.Infrastructure;

namespace Koalix.App.Controls;

/// <summary>
/// A small rounded flag badge (v0.4.12): the real country flag is loaded from the <b>embedded</b> flag pack that ships
/// inside the exe, so it renders instantly and offline. v0.4.6–v0.4.11 downloaded from flagcdn.com with an emoji
/// fallback — but flagcdn is regularly unreachable on Iranian ISPs and Windows never renders flag emoji (it draws
/// plain letter pairs), so users saw no flag at all. Fallback chain now: embedded resource → disk cache → network →
/// a styled 2-letter code badge (never empty, never a broken image).
/// </summary>
public sealed class FlagBox : Grid
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(string), typeof(FlagBox),
        new PropertyMetadata("", (d, e) => ((FlagBox)d).Reload()));
    public static readonly DependencyProperty CountryProperty = DependencyProperty.Register(nameof(Country), typeof(string), typeof(FlagBox),
        new PropertyMetadata("", (d, e) => ((FlagBox)d).UpdateToolTip()));

    public string Code { get => (string)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public string Country { get => (string)GetValue(CountryProperty); set => SetValue(CountryProperty, value); }

    private static readonly ConcurrentDictionary<string, byte> InFlight = new();
    private static readonly ConcurrentDictionary<string, BitmapSource> Embedded = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = new Grid() };
    private readonly Image _img = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    private readonly TextBlock _fallback = new()
    {
        FontFamily = Latin(),
        FontSize = 10, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static FontFamily Latin() => Application.Current?.TryFindResource("LatinFont") as FontFamily ?? new FontFamily("Segoe UI, Tahoma");

    public FlagBox()
    {
        Width = 34; Height = 24;
        SetResourceReference(BackgroundProperty, "InputBrush");
        _frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var inner = (Grid)_frame.Child!;
        inner.Children.Add(_fallback);
        inner.Children.Add(_img);
        Children.Add(_frame);
        ClipToBounds = true;
        SizeChanged += (_, _) => ApplyClip();
        UpdateToolTip();
        Loaded += (_, _) => Reload();
    }

    /// <summary>Rounded clip for the image (Border.ClipToBounds ignores the corner radius).</summary>
    /// <remarks>Named <c>ApplyClip</c> — a <c>Clip</c> method would hide <see cref="UIElement.Clip"/> (CS0108).</remarks>
    private void ApplyClip() => _frame.Clip = new RectangleGeometry(new Rect(0, 0, _frame.ActualWidth, _frame.ActualHeight), 5, 5);

    private void UpdateToolTip() => ToolTip = string.IsNullOrWhiteSpace(Country) ? null : $"{Country}{(Code.Length == 2 ? $" ({Code.ToUpperInvariant()})" : "")}";

    /// <summary>v0.4.41 — returns the embedded (or disk-cached) flag bitmap for direct DrawingContext rendering (e.g. WorldMap).</summary>
    public static BitmapSource? TryGetBitmap(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (c == "UK") c = "GB";
        if (c.Length != 2) return null;
        var embedded = Embedded.GetOrAdd(c, static k =>
        {
            try
            {
                var src = Application.GetResourceStream(new Uri($"pack://application:,,,/KOALIX;component/Assets/Flags/{k}.png"));
                if (src is null) return null!;
                var bmp = new BitmapImage();
                using (var s = src.Stream) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = s; bmp.EndInit(); }
                bmp.Freeze();
                return bmp;
            }
            catch { return null!; }
        });
        if (embedded is not null) return embedded;
        var path = FlagPath(c);
        return File.Exists(path) ? Load(path) : null;
    }

    private void Reload()
    {
        var code = (Code ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        _img.Visibility = Visibility.Collapsed;
        _fallback.Visibility = Visibility.Visible;
        // Windows has no flag emoji glyphs — a bold ISO code badge is the honest, always-readable fallback.
        _fallback.Text = code.Length == 2 ? code : "";
        if (code.Length != 2 || ActualWidth < 4) return; // Reload() runs again from Loaded once laid out

        // 1) Embedded pack — instant, offline, always ships with the app (v0.4.12).
        if (TryGetBitmap(code) is { } embedded) { Show(embedded); return; }

        // 2) Disk cache from a previous run, 3) network download for codes missing from the pack.
        var path = FlagPath(code);
        if (File.Exists(path)) { Show(Load(path)); return; }
        if (!InFlight.TryAdd(code, 0)) return;
        var cc = code.ToLowerInvariant();
        Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(new Uri($"https://flagcdn.com/w80/{cc}.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch { return; }
            finally { InFlight.TryRemove(code, out _); }
        }).ContinueWith(_ =>
        {
            if (!File.Exists(path)) return;
            // Dependency properties must only be read on the UI thread — the Code check runs inside the dispatcher.
            Dispatcher.Invoke(() => { if (string.Equals(Code, code, StringComparison.OrdinalIgnoreCase)) Show(Load(path)); });
        });
    }

    private static BitmapSource? Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var fs = File.OpenRead(path))
            {
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = fs; bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch { return null; /* corrupt cache file → code badge stays */ }
    }

    private void Show(BitmapSource? bmp)
    {
        if (bmp is null) return;
        _img.Source = bmp;
        _fallback.Visibility = Visibility.Collapsed;
        _img.Visibility = Visibility.Visible;
    }

    internal static string FlagPath(string code)
    {
        string root;
        try { root = AppHost.Get<AppPaths>().Root; }
        catch { root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KOALIX"); }
        return Path.Combine(root, "flags", $"{code.ToUpperInvariant()}.png");
    }
}
