using System.Text.RegularExpressions;

namespace Koalix.Tests;

/// <summary>
/// v0.4.27 regression guards — the NullReferenceException toast class + the probe-plan wiring.
///
/// The field report («این ارور نمیدونم چیه میاد بعضی وقتا» — a raw English
/// "NullReferenceException" toast on the dashboard, only sometimes): the dashboard rebuilds
/// the virtual "Auto — best server" row (<c>new ProfileRow(null)</c>, no title yet) and selects
/// it in the same breath; <c>UpdateHero</c> then read <c>SelectedProfile?.Name</c> — the <c>?.</c>
/// guards the ROW, but the getter itself was <c>_titleOverride ?? Profile!.Name</c> and threw
/// while both were null. It only bit while disconnected with the auto row involved — hence
/// "sometimes". These guards pin every layer of the fix.
/// </summary>
public class AutoRowGuardTests
{
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"repo root not found above {AppContext.BaseDirectory}");
        var src = Path.Combine(dir.FullName, "src");
        Assert.True(Directory.Exists(src), $"src not found at {src}");
        return src;
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(SrcDir, relative);
        Assert.True(File.Exists(path), $"missing source file: {relative}");
        return File.ReadAllText(path);
    }

    private static List<(string File, int Line, string Text)> Lines(string relative)
    {
        var rows = new List<(string, int, string)>();
        var lines = File.ReadAllLines(Path.Combine(SrcDir, relative));
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i];
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("//")) continue; // comment-aware, like RuntimeCastGuardTests
            var idx = text.IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0) text = text[..idx];
            rows.Add((relative, i + 1, text));
        }
        return rows;
    }

    /// <summary>The root fix: ProfileRow.Name must be null-safe — <c>Profile?.Name ?? ""</c>, never <c>Profile!.Name</c>.</summary>
    [Fact]
    public void ProfileRow_Name_getter_is_null_safe()
    {
        var code = Lines("Koalix.App/ViewModels/ProfilesViewModel.cs");
        Assert.DoesNotContain(code, l => l.Text.Contains("_titleOverride ?? Profile!.Name")); // the crash line, never again
        Assert.Contains(code, l => l.Text.Contains("_titleOverride ?? Profile?.Name ?? \"\""));
    }

    /// <summary>Defense in depth: the dashboard must give the fresh auto row its localized title BEFORE any
    /// selection can fire UpdateHero — the seed line has to sit between the row's construction and the
    /// auto-selection block, not just somewhere at the end (RefreshAutoTexts used to run one gap too late).</summary>
    [Fact]
    public void Dashboard_seeds_the_auto_row_title_before_selecting_it()
    {
        var code = Lines("Koalix.App/ViewModels/DashboardViewModel.cs");
        var build = code.FirstOrDefault(l => l.Text.Contains("_autoRow = new ProfileRow(null)"));
        var seed = code.FirstOrDefault(l => l.Text.Contains("_autoRow.SetAutoTexts(L[\"Auto.Title\"], L[\"Auto.Sub\"], \"\")"));
        var select = code.FirstOrDefault(l => l.Text.Contains("if (autoWanted && _autoRow is not null) SelectedProfile = _autoRow"));
        Assert.True(build.Line > 0, "the dashboard must construct the auto row");
        Assert.True(seed.Line > 0, "the auto row must be seeded with its localized title immediately");
        Assert.True(select.Line > 0, "the auto-selection block is where UpdateHero fires mid-rebuild");
        Assert.True(build.Line < seed.Line && seed.Line < select.Line,
            $"order must be construct → seed → select, got build:{build.Line} seed:{seed.Line} select:{select.Line}");
    }

    /// <summary>UX layer: the activation-failure toast must be LOCALIZED — the raw exception type name
    /// ("NullReferenceException" as a title, English tech-speak in a Persian UI) is gone.</summary>
    [Fact]
    public void Page_activation_toast_is_localized()
    {
        var code = Lines("Koalix.App/ViewModels/MainViewModel.cs");
        Assert.DoesNotContain(code, l => l.Text.Contains("Toasts.Show(ex.GetType().Name")); // raw type as TITLE — gone
        Assert.Contains(code, l => l.Text.Contains("Toast.PageError.Title"));                // localized title instead
        Assert.Contains(code, l => l.Text.Contains("StartupLog.Trace(\"page activation: ")); // the trace stays
    }

    /// <summary>The localization keys must exist in BOTH languages (XamlIntegrityTests-style parity for the new key).</summary>
    [Theory]
    [InlineData("fa")]
    [InlineData("en")]
    public void PageError_toast_key_exists_in_both_languages(string lang)
    {
        var json = Read($"Koalix.App/Localization/{lang}.json");
        Assert.Contains("\"Toast.PageError.Title\"", json);
    }

    /// <summary>Wiring guard: the connection manager must actually CONSUME the probe plan — a worker-fronted
    /// profile whose health checks still probe cp.cloudflare.com would reproduce the 40-second cuts.</summary>
    [Fact]
    public void ConnectionManager_consumes_the_probe_plan()
    {
        var code = Lines("Koalix.Tunneling/ConnectionManager.cs");
        Assert.Contains(code, l => l.Text.Contains("HealthChecker.ProbePlan("));                       // the plan is computed per connection
        Assert.Contains(code, l => l.Text.Contains("CheckThroughProxyAsync(port, probeTimeout, ct, _probeUrls)")); // …and reaches the connect probes
        Assert.Contains(code, l => Regex.IsMatch(l.Text, @"CheckThroughProxyAsync\(port, TimeSpan\.FromSeconds\(6\), ct, _probeUrls\)")); // …and the monitor probes
        Assert.Contains(code, l => l.Text.Contains("_degradedLatencyMs ? ConnectionState.Degraded"));   // …and the degraded verdict
    }
}
