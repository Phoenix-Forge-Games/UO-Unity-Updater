using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Updater.Core;

/// <summary>
/// ECDSA P-256 / SHA-256 signatures over the exact bytes of channel.json and updater.json.
/// A signature file (".sig") holds "&lt;keyId&gt; &lt;base64 signature&gt;". Clients only accept files
/// signed by a key they trust, so a compromised or spoofed host can't push a bad patch or updater.
/// Blobs and manifests are covered transitively by the SHA-256 hashes inside the signed files.
/// </summary>
public static class Signing
{
    public static (string privatePem, string publicPem) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportPkcs8PrivateKeyPem(), ec.ExportSubjectPublicKeyInfoPem());
    }

    public static string KeyId(string publicPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(publicPem);
        return Convert.ToHexStringLower(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()))[..16];
    }

    public static string Sign(byte[] data, string privatePem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);
        var sig = ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var pub = ec.ExportSubjectPublicKeyInfoPem();
        return $"{KeyId(pub)} {Convert.ToBase64String(sig)}\n";
    }

    /// <summary>Throws unless <paramref name="sigFile"/> is a valid signature of <paramref name="data"/> by one of <paramref name="trustedPems"/>.</summary>
    public static void Verify(byte[] data, string sigFile, IReadOnlyCollection<string> trustedPems, string what)
    {
        if (trustedPems.Count == 0)
            throw new UpdaterException($"No trusted keys configured to verify {what}.");
        var parts = sigFile.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            throw new UpdaterException($"Signature for {what} is malformed.");
        byte[] sig;
        try { sig = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new UpdaterException($"Signature for {what} is malformed."); }

        foreach (var pem in trustedPems)
        {
            if (!string.Equals(KeyId(pem), parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            if (ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return;
            throw new UpdaterException($"Signature check FAILED for {what}: the file was modified or signed with the wrong key.");
        }
        throw new UpdaterException($"{what} is signed by an untrusted key ({parts[0]}).");
    }

    /// <summary>Public keys compiled into the updater (repo folder keys/*.pub), by file name prefix.</summary>
    public static List<string> EmbeddedKeys(string prefix)
    {
        var asm = typeof(Signing).Assembly;
        var list = new List<string>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("keys/" + prefix, StringComparison.OrdinalIgnoreCase)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            list.Add(r.ReadToEnd());
        }
        return list;
    }

    public static string CurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(Signing).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
