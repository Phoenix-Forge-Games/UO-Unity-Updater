using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Updater.Core;

/// <summary>
/// The launcher skin pack (LauncherSkin.pak): a zip of skin.json plus images, AES-encrypted so the
/// paid UI art it carries never sits on disk as loose image files.
/// </summary>
/// <remarks>
/// This is obfuscation, not protection: the key is in the (public) launcher. What keeps the art private
/// is where the file is distributed: only inside the friends-only game patch, never in this repository
/// or the public launcher release.
/// Layout: "BRSKIN01" (8 bytes) | IV (16 bytes) | AES-256-CBC(PKCS7) of the zip.
/// </remarks>
public static class SkinPack
{
    public const string FileName = "LauncherSkin.pak";
    public const string ManifestName = "skin.json";
    private static readonly byte[] Magic = "BRSKIN01"u8.ToArray();
    private const long MaxPackBytes = 64L * 1024 * 1024;
    private const long MaxEntryBytes = 32L * 1024 * 1024;

    private static byte[] Key() => SHA256.HashData(Encoding.UTF8.GetBytes("Britannia Reborn launcher skin v1"));

    /// <summary>Packs every file under <paramref name="sourceDir"/> (which must contain skin.json).</summary>
    public static void Pack(string sourceDir, string outFile)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (!File.Exists(Path.Combine(sourceDir, ManifestName)))
            throw new UpdaterException($"{sourceDir} has no {ManifestName}.");

        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                // PNG/JPEG are already compressed; deflating them again only costs time.
                var level = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // reproducible output
                using var src = File.OpenRead(file);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }

        using var aes = Aes.Create();
        aes.Key = Key();
        // Deterministic IV: the same skin always packs to the same bytes, so republishing it costs no download.
        var iv = SHA256.HashData(zipBytes.ToArray())[..16];
        var cipher = aes.EncryptCbc(zipBytes.ToArray(), iv);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        using var outStream = File.Create(outFile);
        outStream.Write(Magic);
        outStream.Write(iv);
        outStream.Write(cipher);
    }

    /// <summary>Reads a pack into memory: entry name (forward slashes) to bytes. Nothing is written to disk.</summary>
    public static Dictionary<string, byte[]> Open(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new UpdaterException($"{file} does not exist.");
        if (info.Length > MaxPackBytes) throw new UpdaterException($"{file} is too large to be a launcher skin.");
        var data = File.ReadAllBytes(file);
        if (data.Length < Magic.Length + 16 + 16 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new UpdaterException($"{file} is not a launcher skin pack.");

        byte[] plain;
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key();
            plain = aes.DecryptCbc(data.AsSpan(Magic.Length + 16), data.AsSpan(Magic.Length, 16));
        }
        catch (CryptographicException ex) { throw new UpdaterException($"{file} is damaged: {ex.Message}"); }

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (entry.Length > MaxEntryBytes) throw new UpdaterException($"{file}: {entry.FullName} is too large.");
                using var s = entry.Open();
                using var ms = new MemoryStream((int)entry.Length);
                s.CopyTo(ms);
                result[entry.FullName] = ms.ToArray();
            }
        }
        catch (InvalidDataException ex) { throw new UpdaterException($"{file} is damaged: {ex.Message}"); }

        if (!result.ContainsKey(ManifestName)) throw new UpdaterException($"{file} has no {ManifestName}.");
        return result;
    }
}
