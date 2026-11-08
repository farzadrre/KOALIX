using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Koalix.Core;

namespace Koalix.Infrastructure;

/// <summary>DPAPI (CurrentUser scope) with app-specific entropy. Stored as "dpapi:" + base64.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KOALIX-Gaming-Suite/v1");
    /// <summary>Secrets written before the rename (NEXUS 0.1.x) used this entropy; still accepted on read.</summary>
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("NEXUS-Gaming-Suite/v1");
    public string Protect(string plaintext) =>
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    /// <summary>True when the value is only readable with the legacy (pre-rename) entropy and should be re-encrypted.</summary>
    public bool IsLegacy(string protectedText)
    {
        if (!protectedText.StartsWith("dpapi:", StringComparison.Ordinal)) return false;
        try { ProtectedData.Unprotect(Convert.FromBase64String(protectedText[6..]), Entropy, DataProtectionScope.CurrentUser); return false; }
        catch (CryptographicException) { return true; }
    }
    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith("dpapi:", StringComparison.Ordinal)) throw new CryptographicException("Unknown secret format");
        var data = Convert.FromBase64String(protectedText[6..]);
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, LegacyEntropy, DataProtectionScope.CurrentUser)); }
    }
}

/// <summary>Non-Windows/test fallback: AES-GCM with a key file (only for tests and development — not used on Windows).</summary>
public sealed class AesFileSecretProtector : ISecretProtector
{
    private readonly byte[] _key;
    public AesFileSecretProtector(string keyFile)
    {
        if (!File.Exists(keyFile)) File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
        _key = File.ReadAllBytes(keyFile);
    }
    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length]; var tag = new byte[16];
        using var aes = new AesGcm(_key, 16); aes.Encrypt(nonce, pt, ct, tag);
        return "aes:" + Convert.ToBase64String([.. nonce, .. tag, .. ct]);
    }
    public string Unprotect(string s)
    {
        var b = Convert.FromBase64String(s[4..]); var pt = new byte[b.Length - 28];
        using var aes = new AesGcm(_key, 16); aes.Decrypt(b[..12], b[28..], b[12..28], pt);
        return Encoding.UTF8.GetString(pt);
    }
}
