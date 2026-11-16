using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.47.1 — EnginePaths.FindSingBoxExe must see MORE than KOALIX's own engines folder: a system-wide sing-box
/// (PATH, winget, scoop, a hand-copied exe) used to be invisible, so the WARP identity button claimed "not installed"
/// while sing-box was right there. Guards the resolution order: manual override → own engines folder → PATH → null.
/// </summary>
public class EnginePathResolverTests
{
    private static string ExeName => OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "koalix-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateBinary(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, name);
        File.WriteAllText(f, "stub");
        return f;
    }

    [Fact]
    public void OwnEngineFolder_IsFound()
    {
        var root = TempRoot();
        try
        {
            var ep = new EnginePaths(root);
            var exe = CreateBinary(Path.Combine(root, "engines", "sing-box"), "sing-box.exe");
            Assert.Equal(exe, ep.FindSingBoxExe());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ManualOverride_WinsOverOwnFolder()
    {
        var root = TempRoot();
        try
        {
            var ep = new EnginePaths(root) { SingBoxPathOverride = Path.Combine(root, "custom", ExeName) };
            CreateBinary(Path.Combine(root, "engines", "sing-box"), "sing-box.exe");
            var exe = CreateBinary(Path.Combine(root, "custom"), ExeName);
            Assert.Equal(exe, ep.FindSingBoxExe());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OverridePointingToMissingFile_IsIgnored()
    {
        var root = TempRoot();
        try
        {
            var ep = new EnginePaths(root) { SingBoxPathOverride = Path.Combine(root, "nowhere", "gone.exe") };
            var exe = CreateBinary(Path.Combine(root, "engines", "sing-box"), "sing-box.exe");
            Assert.Equal(exe, ep.FindSingBoxExe());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SystemPath_IsUsedWhenOwnFolderEmpty()
    {
        var root = TempRoot();
        var pathDir = Path.Combine(root, "onpath");
        CreateBinary(pathDir, ExeName);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDir);
            var ep = new EnginePaths(root);
            Assert.Equal(Path.Combine(pathDir, ExeName), ep.FindSingBoxExe());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OwnFolder_StillBeatsSystemPath()
    {
        var root = TempRoot();
        var pathDir = Path.Combine(root, "onpath");
        CreateBinary(pathDir, ExeName);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDir);
            var ep = new EnginePaths(root);
            var exe = CreateBinary(Path.Combine(root, "engines", "sing-box"), "sing-box.exe");
            Assert.Equal(exe, ep.FindSingBoxExe());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReturnsNull_WhenNothingExists()
    {
        var root = TempRoot();
        var emptyPathDir = Path.Combine(root, "emptypath");
        Directory.CreateDirectory(emptyPathDir);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyPathDir);
            var ep = new EnginePaths(root) { SingBoxPathOverride = "" };
            Assert.Null(ep.FindSingBoxExe());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NullOrWhitespaceOverride_BehavesLikeEmpty()
    {
        var root = TempRoot();
        try
        {
            var ep = new EnginePaths(root) { SingBoxPathOverride = "   " };
            Assert.Null(ep.FindSingBoxExe());
        }
        finally { Directory.Delete(root, true); }
    }
}
