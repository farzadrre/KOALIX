using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Koalix.Tests;

/// <summary>
/// v0.4.20 postmortem regression guard. The Tunnels page shipped with three
/// <c>Style="StaticResource Subtle}"</c> attributes (missing the opening brace):
/// the WPF markup compiler ACCEPTS such strings and defers them to load time,
/// where <c>InitializeComponent()</c> threw a silent XamlParseException — the
/// global handler swallowed it and the user got a completely blank Tunnels page
/// («بخش تونل کلا باز نمیشه») while the build stayed green and every other test passed.
/// These tests statically re-validate every XAML file in the source tree on each
/// test run, catching the whole crash class BEFORE it reaches a build.
/// </summary>
public class XamlIntegrityTests
{
    private static readonly string AppDir = FindAppDir();

    private static string FindAppDir()
    {
        // walk up from the test bin directory until the solution root (KoalixSuite.slnx)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"repo root not found above {AppContext.BaseDirectory} — run tests from the source tree");
        var app = Path.Combine(dir.FullName, "src", "Koalix.App");
        Assert.True(Directory.Exists(app), $"Koalix.App not found at {app}");
        return app;
    }

    private static HashSet<string> KeysOf(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        var keys = new HashSet<string>();
        foreach (XmlElement el in doc.SelectNodes("//*[@x:Key]", nsmgr)!)
            keys.Add(el.GetAttribute("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));
        return keys;
    }

    private static Dictionary<string, string> Loc(string lang) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppDir, "Localization", $"{lang}.json")))!;

    private static readonly Regex BareExtension = new(
        @"^(StaticResource|DynamicResource|Binding|TemplateBinding|x:Static|x:Null|RelativeSource|l:T)\s",
        RegexOptions.Compiled);
    private static readonly Regex StaticResource = new(@"\{StaticResource\s+([^}]+?)\s*\}", RegexOptions.Compiled);
    private static readonly Regex TypedKey = new(@"^\{x:Type\b", RegexOptions.Compiled);
    private static readonly Regex LocExtension = new(@"\{\s*l:T\s+([A-Za-z0-9_.:]+?)\s*\}", RegexOptions.Compiled);
    private static readonly Regex Attribute = new(@"([A-Za-z0-9_.:\[\]]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void Every_xaml_is_well_formed_and_every_markup_reference_is_valid()
    {
        var appKeys = KeysOf(Path.Combine(AppDir, "App.xaml"));
        var controlKeys = KeysOf(Path.Combine(AppDir, "Themes", "Controls.xaml"));
        var windowKeys = KeysOf(Path.Combine(AppDir, "MainWindow.xaml"));
        var darkKeys = KeysOf(Path.Combine(AppDir, "Themes", "Dark.xaml"));
        var lightKeys = KeysOf(Path.Combine(AppDir, "Themes", "Light.xaml"));
        var fa = Loc("fa");
        var en = Loc("en");

        var xamlFiles = Directory.EnumerateFiles(Path.Combine(AppDir, ".."), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p)
            .ToList();
        Assert.NotEmpty(xamlFiles);

        var errors = new List<string>();
        foreach (var file in xamlFiles)
        {
            var rel = Path.GetRelativePath(Path.Combine(AppDir, ".."), file);
            var text = File.ReadAllText(file);

            // 1. XML well-formedness (a broken file fails the build anyway, but keep the guard self-contained)
            XmlDocument doc;
            try { doc = new XmlDocument(); doc.Load(file); }
            catch (XmlException ex) { errors.Add($"{rel}: XML parse error: {ex.Message}"); continue; }

            // keys defined in THIS file (available to StaticResource inside it)
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var localKeys = new HashSet<string>();
            foreach (XmlElement el in doc.SelectNodes("//*[@x:Key]", nsmgr)!)
                localKeys.Add(el.GetAttribute("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));
            var resolvable = new HashSet<string>(appKeys.Concat(controlKeys).Concat(windowKeys).Concat(localKeys));

            foreach (Match attr in Attribute.Matches(text))
            {
                var value = attr.Groups[2].Value;

                // 2. THE v0.4.20 crash class: a markup extension written WITHOUT the opening brace.
                //    The markup compiler accepts the string and defers it to load time, where it
                //    becomes a XamlParseException and a blank page. Build stays green — only this
                //    test catches it.
                if (BareExtension.IsMatch(value))
                    errors.Add($"{rel}: attribute {attr.Groups[1].Value}=\"{value}\" — markup extension missing '{{' (builds fine, blanks the page at runtime — the v0.4.20 bug)");

                // 3. every {StaticResource Key} must resolve statically (unresolved = XamlParseException at load)
                foreach (Match sr in StaticResource.Matches(value))
                {
                    var key = sr.Groups[1].Value;
                    if (TypedKey.IsMatch(key)) continue; // {x:Type …} typed keys resolve by type
                    if (!resolvable.Contains(key))
                        errors.Add($"{rel}: StaticResource '{key}' is not defined in App/Controls/MainWindow or locally");
                    else if (darkKeys.Contains(key) != lightKeys.Contains(key))
                        errors.Add($"{rel}: StaticResource '{key}' exists in only one theme dictionary — blank page under the other theme");
                }

                // 4. every {l:T Key} must exist in BOTH languages
                foreach (Match t in LocExtension.Matches(value))
                {
                    var key = t.Groups[1].Value.Split(':')[0];
                    if (!fa.ContainsKey(key)) errors.Add($"{rel}: l:T '{key}' missing from fa.json");
                    if (!en.ContainsKey(key)) errors.Add($"{rel}: l:T '{key}' missing from en.json");
                }
            }
        }

        // 5. language parity (a missing key renders as raw text on one side)
        var onlyFa = fa.Keys.Except(en.Keys).ToList();
        var onlyEn = en.Keys.Except(fa.Keys).ToList();
        if (onlyFa.Count > 0) errors.Add($"fa.json has keys missing from en.json: {string.Join(", ", onlyFa.Take(5))}");
        if (onlyEn.Count > 0) errors.Add($"en.json has keys missing from fa.json: {string.Join(", ", onlyEn.Take(5))}");

        Assert.True(errors.Count == 0, $"{errors.Count} XAML integrity problem(s):\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Localization_files_parse_and_cover_both_languages()
    {
        var fa = Loc("fa");
        var en = Loc("en");
        Assert.NotEmpty(fa);
        Assert.NotEmpty(en);
        // the strings the app literally cannot run without
        foreach (var key in new[] { "Nav.Dashboard", "Nav.Profiles", "Profiles.Title", "Common.Delete" })
        {
            Assert.True(fa.ContainsKey(key), $"fa.json missing core key {key}");
            Assert.True(en.ContainsKey(key), $"en.json missing core key {key}");
        }
    }
}
