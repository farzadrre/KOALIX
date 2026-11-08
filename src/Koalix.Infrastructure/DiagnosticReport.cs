using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Koalix.Core;

namespace Koalix.Infrastructure;

/// <summary>Builds a redacted diagnostic zip (system info, adapters, recent logs). Secrets and public IPs are redacted.</summary>
public static class DiagnosticReport
{
    public static string Create(AppPaths paths, IEnumerable<string> recentLogLines, string outputDir)
    {
        var file = Path.Combine(outputDir, $"koalix-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        var sb = new StringBuilder()
            .AppendLine($"KOALIX {typeof(DiagnosticReport).Assembly.GetName().Version}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}")
            .AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            sb.AppendLine($"- {n.Name} | {n.Description} | {n.NetworkInterfaceType} | {n.OperationalStatus}");
            try { foreach (var d in n.GetIPProperties().DnsAddresses) sb.AppendLine($"    dns {d}"); } catch { }
        }
        Write(zip, "system.txt", Redactor.Redact(sb.ToString(), redactIpAddresses: true));
        Write(zip, "logs.txt", Redactor.Redact(string.Join('\n', recentLogLines), redactIpAddresses: true));
        return file;
    }
    private static void Write(ZipArchive z, string name, string text)
    {
        using var s = z.CreateEntry(name).Open(); var b = Encoding.UTF8.GetBytes(text); s.Write(b);
    }
}
