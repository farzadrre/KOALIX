using System.Text.RegularExpressions;

namespace Koalix.Tests;

/// <summary>
/// v0.4.25 postmortem regression guard — the runtime-cast crash class.
///
/// KOALIX 0.4.24 never started on the user's machine («KOALIX could not start —
/// InvalidCastException: Specified cast is not valid»). Root cause: inside
/// <c>MotionGlyph.InspectorIcon</c> the new animated routing-inspector logo did
/// <c>(PathGeometry)Geometry.Parse(route)</c>. Geometry.Parse ALWAYS returns a
/// StreamGeometry — the C# compiler happily compiles the cast (the static type is
/// Geometry, so a cast is even required-looking) but the CLR throws
/// InvalidCastException the moment the glyph is loaded with real pixels, i.e.
/// during MainWindow's first render pass → the global handler reported a fatal
/// startup failure. Builds stayed green; XamlIntegrityTests only scan XAML, not C#.
///
/// These tests statically scan every C# source file under src/ for the whole bug
/// family: hard casts of Geometry.Parse (or of a plain Geometry value) into a
/// concrete geometry type. The only sanctioned conversion is
/// <c>PathGeometry.CreateFromGeometry(...)</c>.
/// </summary>
public class RuntimeCastGuardTests
{
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir()
    {
        // walk up from the test bin directory until the solution root (KoalixSuite.slnx)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"repo root not found above {AppContext.BaseDirectory} — run tests from the source tree");
        var src = Path.Combine(dir.FullName, "src");
        Assert.True(Directory.Exists(src), $"src not found at {src}");
        return src;
    }

    private static List<(string File, int Line, string Text)> SourceLines()
    {
        var files = Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p)
            .ToList();
        Assert.NotEmpty(files);
        var rows = new List<(string, int, string)>();
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i];
                // comment-aware: skip pure comment lines and strip trailing // comments, so this very
                // incident can be DOCUMENTED in the code (as the postmortem comments are) without tripping the guard
                var trimmed = text.TrimStart();
                if (trimmed.StartsWith("//")) continue;
                var idx = text.IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0) text = text[..idx];
                rows.Add((Path.GetRelativePath(SrcDir, f), i + 1, text));
            }
        }
        return rows;
    }

    /// <summary>(PathGeometry)Geometry.Parse / (StreamGeometry)Geometry.Parse / … — compiles, dies at runtime.</summary>
    [Fact]
    public void No_hard_cast_of_GeometryParse_to_a_concrete_geometry_type()
    {
        // Geometry.Parse returns StreamGeometry; casting its RESULT to any other concrete geometry type is always invalid.
        var castOfParse = new Regex(
            @"\(\s*(PathGeometry|StreamGeometry|EllipseGeometry|LineGeometry|RectangleGeometry|CombinedGeometry|GeometryGroup)\s*\)\s*(Geometry|PathGeometry|StreamGeometry)\s*\.\s*Parse\s*\(",
            RegexOptions.Compiled);
        var offenders = SourceLines()
            .Where(l => castOfParse.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}  {l.Text.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "Hard casts of Geometry.Parse(...) to a concrete geometry type throw InvalidCastException at runtime " +
            "(Geometry.Parse always returns StreamGeometry). Use PathGeometry.CreateFromGeometry(...) instead. Offenders:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>v0.4.26 — the hopping-packet redesign dropped MatrixAnimationUsingPath entirely (it animates
    /// Canvas.Left/Top, no geometry parsing at all). This check now asserts the inspector icon is still present
    /// and cast-free; the global anti-cast scan above covers the crash class for the whole tree.</summary>
    [Fact]
    public void MotionGlyph_inspector_icon_present_and_cast_free()
    {
        var code = SourceLines().Where(l => l.File.Replace('\\', '/').EndsWith("Koalix.App/Controls/MotionGlyph.cs")).ToList();
        Assert.NotEmpty(code);
        Assert.Contains(code, l => l.Text.Contains("InspectorIcon"));                       // the inspector logo is still there
        Assert.Contains(code, l => l.Text.Contains("StrokeDashOffset"));                    // …and is the v0.4.26 marching-dash design
        Assert.DoesNotContain(code, l => l.Text.Contains("(PathGeometry)Geometry.Parse")); // the crash, never again (comments allowed)
    }
}
