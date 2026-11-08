using System.IO;
using System.Media;
using Koalix.Core;
using Koalix.Infrastructure;

namespace Koalix.App.Services;

/// <summary>
/// v0.4.24 — the connection-sound engine, rebuilt per the external audit + user's approved list:
/// «صدا فقط هنگام وصل شدن، قطع شدن و خطا» — sounds fire on EXACTLY three events now (connect / disconnect /
/// error). Two v0.4.23 bugs are fixed: the "unstable" (Degraded) state no longer plays the CONNECT tone, and a
/// manual disconnect no longer plays the disconnect tone TWICE (it fired once on "disconnecting" and again on
/// "disconnected" — now only the final Disconnected state is audible; Disconnecting/Connecting/Reconnecting/
/// Degraded are all silent).
/// Per event the user picks one of four built-in synthesized presets (or silence, or a custom .wav/.mp3 file),
/// each with a test-play button, and one master volume slider (settings: ui.sounds.volume, 0–100).
/// Presets stay asset-free: they are rendered into memory as 16-bit PCM WAV (soft sines, fade envelopes),
/// with the volume baked in; custom files play through WPF MediaPlayer (wav/mp3, volume-aware).
/// </summary>
public static class SoundService
{
    public const string KeyEnabled = "ui.sounds";
    public const string KeyVolume = "ui.sounds.volume";
    public const string KeyConnect = "ui.sounds.connect";
    public const string KeyDisconnect = "ui.sounds.disconnect";
    public const string KeyError = "ui.sounds.error";

    /// <summary>v0.4.36 — four more events (the user's «چن تا صدای دیگه هم اضافه کن و قابل تنظیم باشن»):
    /// Reconnecting (the tunnel dropped and is coming back), PingSpike (sudden latency jump while connected),
    /// AutoSwitch (Auto mode moved to another server), FocusOn (focus mode opened). The three originals keep
    /// their keys; every event persists under ui.sounds.&lt;event lower-case&gt;.</summary>
    public enum SoundEvent { Connect, Disconnect, Error, Reconnecting, PingSpike, AutoSwitch, FocusOn }

    /// <summary>Built-in presets per event (the pickers list them in this order).</summary>
    public static IReadOnlyList<string> Presets(SoundEvent e) => e switch
    {
        SoundEvent.Connect => ["rise", "ding", "chord", "blip", "sparkle", "warm"],
        SoundEvent.Disconnect => ["fall", "low", "soft", "bye", "drop", "hush"],
        SoundEvent.Error => ["tap", "buzz", "alert", "thud", "glitch", "knock"],
        SoundEvent.Reconnecting => ["pulse", "tick", "wave"],
        SoundEvent.PingSpike => ["ping", "siren", "pop"],
        SoundEvent.AutoSwitch => ["hop", "swoosh", "twin"],
        SoundEvent.FocusOn => ["zen", "glass", "breath"],
        _ => [],
    };

    public static string KeyFor(SoundEvent e) => "ui.sounds." + e.ToString().ToLowerInvariant();

    /// <summary>Out-of-the-box choice per event. The noisy-by-nature ones (reconnect flapping, focus) start silent.</summary>
    public static string DefaultChoice(SoundEvent e) => e switch
    {
        SoundEvent.Connect => "rise",
        SoundEvent.Disconnect => "fall",
        SoundEvent.Error => "tap",
        SoundEvent.PingSpike => "ping",
        SoundEvent.AutoSwitch => "hop",
        _ => "none",
    };

    private static volatile bool _enabled = true;
    public static bool Enabled { get => _enabled; set => _enabled = value; }

    private static int _volume = 70;
    /// <summary>Master volume 0–100. Synthesized presets bake it into the render; files scale MediaPlayer.Volume.</summary>
    public static int Volume { get => _volume; set => _volume = Math.Clamp(value, 0, 100); }

    private static readonly Dictionary<SoundEvent, string> _choice = Enum.GetValues<SoundEvent>().ToDictionary(e => e, DefaultChoice);
    private static readonly Dictionary<SoundEvent, DateTime> _lastPlayed = [];
    /// <summary>Repeat guard for the events that can fire in bursts (flapping link, jittery ping).</summary>
    private static TimeSpan MinGap(SoundEvent e) => e is SoundEvent.Reconnecting or SoundEvent.PingSpike or SoundEvent.AutoSwitch ? TimeSpan.FromSeconds(12) : TimeSpan.Zero;

    /// <summary>Per-event choice: a preset id, "none", or "file:&lt;absolute path&gt;".</summary>
    public static string Choice(SoundEvent e) => _choice.TryGetValue(e, out var c) ? c : "none";

    public static void SetChoice(SoundEvent e, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) value = "none";
        _choice[e] = value.Trim();
    }

    /// <summary>Reads every persisted sound setting at startup.</summary>
    public static void Load(SettingsRepository settings)
    {
        _enabled = settings.Get(KeyEnabled) != "0";
        _volume = int.TryParse(settings.Get(KeyVolume), out var v) ? Math.Clamp(v, 0, 100) : 70;
        foreach (var e in Enum.GetValues<SoundEvent>())
            _choice[e] = settings.Get(KeyFor(e)) ?? DefaultChoice(e);
    }

    /// <summary>The three audible states — everything else (connecting, reconnecting, disconnecting, unstable) is silent.</summary>
    public static void Play(ConnectionState state)
    {
        if (!_enabled) return;
        var ev = state switch
        {
            ConnectionState.Connected => SoundEvent.Connect,
            ConnectionState.Disconnected => SoundEvent.Disconnect,
            ConnectionState.Error => SoundEvent.Error,
            ConnectionState.Reconnecting => SoundEvent.Reconnecting, // v0.4.36 — own event, silent by default
            _ => (SoundEvent?)null, // Connecting/Disconnecting/Degraded: no sound (the v0.4.23 fixes)
        };
        if (ev is { } e) Play(e);
    }

    /// <summary>Plays the currently chosen sound for one event — used by real state changes and by the test button alike.</summary>
    public static void Play(SoundEvent e) => Play(e, throttle: true);

    private static void Play(SoundEvent e, bool throttle)
    {
        if (!_enabled) return;
        var choice = Choice(e);
        if (choice is "" or "none") return;
        if (throttle && MinGap(e) > TimeSpan.Zero)
        {
            lock (_lastPlayed)
            {
                var now = DateTime.UtcNow;
                if (_lastPlayed.TryGetValue(e, out var at) && now - at < MinGap(e)) return;
                _lastPlayed[e] = now;
            }
        }
        if (choice.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            PlayFile(choice[5..]);
            return;
        }
        var preset = choice.Trim();
        // PlaySync on a worker thread = one clip at a time, never overlapping, never blocking the UI.
        Task.Run(() =>
        {
            try
            {
                using var player = new SoundPlayer(GetStream(preset));
                player.PlaySync();
            }
            catch { /* a missing audio device must never surface as an error dialog */ }
        });
    }

    /// <summary>The Settings test button: always audible, even while the master switch itself is off.</summary>
    public static void Test(SoundEvent e)
    {
        var wasEnabled = _enabled;
        _enabled = true;
        try { Play(e, throttle: false); }
        finally { _enabled = wasEnabled; }
    }

    /* ─────────── custom files (wav / mp3) — WPF MediaPlayer is volume-aware and plays both ─────────── */

    private static void PlayFile(string path)
    {
        if (!File.Exists(path)) return;
        var vol = _volume / 100.0;
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var player = new System.Windows.Media.MediaPlayer();
                player.Open(new Uri(path, UriKind.Absolute));
                player.Volume = vol;
                player.MediaOpened += (_, _) => { try { player.Play(); } catch { } };
                player.MediaEnded += (_, _) => { try { player.Close(); } catch { } };
                // A timer-less watchdog: if the file never opens (corrupt/locked), drop the handle.
                var leakGuard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                leakGuard.Tick += (_, _) => { leakGuard.Stop(); try { player.Close(); } catch { } };
                leakGuard.Start();
            }
            catch { /* bad file / no device — silence, never an error dialog */ }
        });
    }

    /* ─────────── synthesized presets, rendered to WAV with the volume baked in ─────────── */

    private static readonly object _gate = new();
    private static readonly Dictionary<string, MemoryStream> _cache = [];

    private static Stream GetStream(string preset)
    {
        // Volume is baked into the render — quantize to steps of 10 so the cache stays tiny across slider moves.
        var bucket = _volume / 10 * 10;
        var key = preset + ":" + bucket;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var ms))
            {
                ms = Render(preset, bucket / 100.0);
                _cache[key] = ms;
            }
            ms.Position = 0;
            return ms;
        }
    }

    /// <summary>Renders one preset as a quiet, click-free mono WAV. freq=0 entries are rests.</summary>
    private static MemoryStream Render(string preset, double volume)
    {
        const int rate = 22050;
        var notes = preset switch
        {
            // connect — bright, opening
            "rise"  => new[] { (523.25, 0.10), (784.0, 0.16) },            // C5 → G5
            "ding"  => new[] { (1318.5, 0.22), (0.0, 0.03), (1318.5, 0.10) }, // E6 bell, soft echo
            "chord" => new[] { (523.25, 0.09), (659.25, 0.09), (784.0, 0.14) }, // C5 → E5 → G5 arpeggio
            "blip"  => new[] { (880.0, 0.12) },                             // single A5
            // disconnect — settling, closing
            "fall"  => new[] { (659.25, 0.10), (440.0, 0.16) },            // E5 → A4
            "low"   => new[] { (392.0, 0.11), (293.66, 0.17) },            // G4 → D4
            "soft"  => new[] { (440.0, 0.16) },                            // single A4
            "bye"   => new[] { (659.25, 0.09), (523.25, 0.09), (440.0, 0.13) }, // E5 → C5 → A4
            // error — low, deliberate
            "tap"   => new[] { (311.13, 0.09), (0.0, 0.05), (311.13, 0.12) },  // D#4, rest, D#4
            "buzz"  => new[] { (146.83, 0.16), (0.0, 0.04), (146.83, 0.16) },  // D3 growl twice
            "alert" => new[] { (493.88, 0.08), (0.0, 0.04), (493.88, 0.08), (0.0, 0.04), (493.88, 0.10) }, // B4 ×3
            "thud"  => new[] { (196.0, 0.20) },                            // single G3
            // v0.4.36 — more per event
            "sparkle" => new[] { (1046.5, 0.06), (1318.5, 0.06), (1568.0, 0.06), (2093.0, 0.12) }, // C6 E6 G6 C7 run
            "warm"  => new[] { (392.0, 0.10), (523.25, 0.18) },            // G4 → C5, mellow
            "drop"  => new[] { (880.0, 0.06), (587.33, 0.06), (392.0, 0.14) }, // A5 → D5 → G4
            "hush"  => new[] { (329.63, 0.22) },                           // single quiet E4
            "glitch" => new[] { (622.25, 0.04), (0.0, 0.03), (587.33, 0.04), (0.0, 0.03), (554.37, 0.08) }, // stutter down
            "knock" => new[] { (220.0, 0.07), (0.0, 0.07), (220.0, 0.07) }, // A3 knock-knock
            // reconnecting — patient, neutral
            "pulse" => new[] { (587.33, 0.08), (0.0, 0.10), (587.33, 0.08) }, // D5 heartbeat
            "tick"  => new[] { (1174.7, 0.04) },                           // D6 tick
            "wave"  => new[] { (440.0, 0.08), (523.25, 0.08), (440.0, 0.10) }, // A4 C5 A4
            // ping spike — short, alerting, never alarming
            "ping"  => new[] { (1760.0, 0.08), (0.0, 0.04), (1760.0, 0.05) }, // A6 double ping
            "siren" => new[] { (698.46, 0.09), (880.0, 0.09), (698.46, 0.09) }, // F5 A5 F5
            "pop"   => new[] { (987.77, 0.05) },                           // B5 pop
            // auto-switch — movement
            "hop"   => new[] { (659.25, 0.07), (987.77, 0.11) },           // E5 → B5 leap
            "swoosh" => new[] { (392.0, 0.05), (523.25, 0.05), (659.25, 0.05), (783.99, 0.09) }, // quick climb
            "twin"  => new[] { (783.99, 0.07), (0.0, 0.03), (1046.5, 0.07) }, // G5 · C6
            // focus — calm
            "zen"   => new[] { (261.63, 0.30) },                           // long C4
            "glass" => new[] { (2093.0, 0.18) },                           // C7 glassy
            "breath" => new[] { (329.63, 0.16), (392.0, 0.24) },           // E4 → G4 slow
            _ => new[] { (523.25, 0.10), (784.0, 0.16) },
        };
        var totalSeconds = notes.Sum(n => n.Item2);
        var samples = (int)(rate * totalSeconds);
        var data = new byte[samples * 2];
        var amp = 0.32 * Math.Max(0.05, volume); // a whisper by design; keep a floor so 10% is still audible
        var at = 0;
        foreach (var (freq, seconds) in notes)
        {
            var len = (int)(rate * seconds);
            for (var i = 0; i < len && at < samples; i++, at++)
            {
                var t = (double)i / rate;
                var env = Math.Min(1, i / (rate * 0.008)) * Math.Min(1, (len - i) / (rate * 0.05));
                var v = freq <= 0 ? 0 : Math.Sin(2 * Math.PI * freq * t) + 0.22 * Math.Sin(4 * Math.PI * freq * t);
                var s = Math.Clamp(amp * env * v, -1, 1);
                var b = (short)(s * short.MaxValue);
                data[at * 2] = (byte)(b & 0xFF);
                data[at * 2 + 1] = (byte)((b >> 8) & 0xFF);
            }
        }
        var msOut = new MemoryStream();
        using (var w = new BinaryWriter(msOut, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + data.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8);
            w.Write(data.Length);
            w.Write(data);
        }
        msOut.Position = 0;
        return msOut;
    }
}
