using System.Security.Cryptography;

namespace Updater.Core;

/// <summary>Result of reading a file once: its hash and, for large files, its content-defined chunks.</summary>
public sealed record ScanResult(long Size, string Sha256, List<ChunkEntry>? Chunks);

/// <summary>
/// FastCDC-style content-defined chunking with a 64-bit gear hash and normalized chunking.
/// Chunk boundaries depend only on nearby content, so inserting or removing bytes in a large file
/// changes only the chunks around the edit; the rest keep their hashes and are not downloaded again.
/// </summary>
public static class GearChunker
{
    public const string AlgorithmName = "gear-cdc-v1";

    private static readonly ulong[] Gear = BuildGear();

    // Deterministic table (splitmix64, fixed seed). Changing it changes every chunk boundary: bump AlgorithmName if you do.
    private static ulong[] BuildGear()
    {
        var t = new ulong[256];
        ulong s = 0x5555_AAAA_1234_5678UL;
        for (int i = 0; i < 256; i++)
        {
            s += 0x9E3779B97F4A7C15UL;
            ulong z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            t[i] = z ^ (z >> 31);
        }
        return t;
    }

    private static ulong Mask(int bits)
    {
        // Spread the mask bits over the top of the word (the gear hash mixes best in the high bits).
        ulong m = 0;
        for (int i = 0; i < bits; i++) m |= 1UL << (63 - i * 2 % 64);
        return m;
    }

    /// <summary>Hash a file and, if <paramref name="chunk"/> is set, split it into chunks.</summary>
    public static ScanResult Scan(string path, ChunkingParams p, bool? chunk = null, IProgress<long>? bytesRead = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        bool doChunk = chunk ?? fs.Length >= p.Threshold;
        return Scan(fs, p, doChunk, bytesRead, ct);
    }

    public static ScanResult Scan(Stream s, ChunkingParams p, bool doChunk, IProgress<long>? bytesRead = null, CancellationToken ct = default)
    {
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = doChunk ? new List<ChunkEntry>() : null;

        int avgBits = System.Numerics.BitOperations.Log2((uint)p.Avg);
        ulong maskS = Mask(avgBits + 2); // harder to hit before the average size
        ulong maskL = Mask(avgBits - 2); // easier after it
        int min = p.Min, avg = p.Avg, max = p.Max;

        var buf = new byte[1 << 20];
        long total = 0, chunkStart = 0;
        int chunkLen = 0;
        ulong h = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            fileHash.AppendData(buf, 0, n);
            total += n;
            bytesRead?.Report(n);
            if (chunks == null) continue;

            int segStart = 0;
            for (int i = 0; i < n; i++)
            {
                chunkLen++;
                if (chunkLen < min) continue;
                h = (h << 1) + Gear[buf[i]];
                ulong mask = chunkLen < avg ? maskS : maskL;
                if ((h & mask) == 0 || chunkLen >= max)
                {
                    chunkHash.AppendData(buf, segStart, i + 1 - segStart);
                    chunks.Add(new ChunkEntry { Sha256 = Hex(chunkHash.GetHashAndReset()), Size = chunkLen, Offset = chunkStart });
                    chunkStart += chunkLen;
                    segStart = i + 1;
                    chunkLen = 0;
                    h = 0;
                }
            }
            if (segStart < n) chunkHash.AppendData(buf, segStart, n - segStart);
        }
        if (chunks != null && chunkLen > 0)
            chunks.Add(new ChunkEntry { Sha256 = Hex(chunkHash.GetHashAndReset()), Size = chunkLen, Offset = chunkStart });

        return new ScanResult(total, Hex(fileHash.GetHashAndReset()), chunks);
    }

    public static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Hex(SHA256.HashData(data));
}
