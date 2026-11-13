using System.Net;
using System.Text;
using PatchTool;
using Updater.Core;

const string Usage = """
patchtool - build, publish and apply incremental game patches

  patchtool keygen  --out <dir> --name <patch|updater>
      Make a signing key pair: <name>.pem (private, keep it secret) and <name>.pub (commit to keys/).

  patchtool publish --build <dir> --host <dir> --channel <name> --version <x.y.z> --key <pem|env:VAR>
                    [--product <name>] [--launch <exe>] [--launch-args <args>] [--exclude <glob>]... [--force]
                    [--chunk-threshold <bytes>]
      Add a build to a local mirror of the patch host. Only new files/chunks are written to <host>/blobs.

  patchtool sync    --channel-url <url> --dir <install dir> [--pubkey <file>]... [--verify] [--dry-run] [--json]
      Update an install folder to the channel's current version (headless; used by dedicated servers and CI).

  patchtool updater-release --exe <file> --version <x.y.z> --url <download url> --key <pem|env:VAR> --out <dir> [--notes <text>]
      Write a signed updater.json describing a GameUpdater.exe release.

  patchtool prune   --host <dir> --keep <n> [--dry-run]
      Delete blobs not used by the newest <n> versions of any channel.

  patchtool rollback --host <dir> --channel <name> --version <x.y.z>
      Point a channel back at an earlier published version (clients then patch back to it).

  patchtool pack-skin --dir <skin folder> --out <LauncherSkin.pak>
      Pack a launcher skin (skin.json + images) into one encrypted file. Ship it only in the private game
      patch, at the install root; never commit it or attach it to a public release.

  patchtool serve   --root <dir> [--port 8080] [--bind 127.0.0.1]
      Serve a folder over HTTP for local testing.

  patchtool version
""";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var rest = args.Skip(1);
    switch (args[0])
    {
        case "version":
            Console.WriteLine(Signing.CurrentVersion());
            return 0;

        case "keygen":
        {
            var a = new Args(rest);
            var outDir = a.Req("out"); var name = a.Req("name"); a.EnsureAllUsed();
            Directory.CreateDirectory(outDir);
            var pem = Path.Combine(outDir, name + ".pem");
            if (File.Exists(pem)) throw new UpdaterException($"{pem} already exists; refusing to overwrite a key.");
            var (priv, pub) = Signing.GenerateKeyPair();
            File.WriteAllText(pem, priv);
            File.WriteAllText(Path.Combine(outDir, name + ".pub"), pub);
            Console.WriteLine($"Wrote {pem} (PRIVATE) and {name}.pub. Key id {Signing.KeyId(pub)}.");
            return 0;
        }

        case "publish":
        {
            var a = new Args(rest, "force");
            var chunking = new ChunkingParams();
            if (a.Opt("chunk-threshold") is { } th) chunking.Threshold = long.Parse(th);
            var o = new PublishOptions
            {
                BuildDir = a.Req("build"), HostRoot = a.Req("host"), Channel = a.Req("channel"), Version = a.Req("version"),
                Product = a.Opt("product") ?? "game", PrivateKeyPem = ReadKey(a.Req("key")),
                Launch = a.Opt("launch"), LaunchArgs = a.Opt("launch-args"), Exclude = a.Many("exclude"),
                Force = a.Flag("force"), Chunking = chunking,
            };
            a.EnsureAllUsed();
            PatchPublisher.Publish(o, Console.WriteLine);
            return 0;
        }

        case "sync":
        {
            var a = new Args(rest, "verify", "dry-run", "json");
            var url = new Uri(a.Req("channel-url"), UriKind.Absolute);
            var dir = a.Req("dir");
            var keys = a.Many("pubkey").Select(File.ReadAllText).ToList();
            if (keys.Count == 0) keys = Signing.EmbeddedKeys("patch");
            bool verify = a.Flag("verify"), dry = a.Flag("dry-run"), json = a.Flag("json");
            a.EnsureAllUsed();

            using var http = Fetcher.CreateHttpClient($"patchtool/{Signing.CurrentVersion()}");
            var client = new PatchClient(new Fetcher(http), keys);
            var remote = await client.CheckAsync(url, cts.Token);
            Directory.CreateDirectory(dir);
            var plan = await client.PlanAsync(dir, remote, verify, null, cts.Token);
            Console.WriteLine($"Remote {remote.Channel.Channel} {remote.Manifest.Version}; installed {plan.InstalledVersion ?? "(none)"}.");
            Console.WriteLine($"{plan.Changed.Count} to write, {plan.Deleted.Count} to delete, {plan.Unchanged} unchanged; " +
                              $"download {PatchPublisher.Fmt(plan.DownloadBytes)} in {plan.Downloads.Count} blobs, reuse {PatchPublisher.Fmt(plan.ReusedBytes)} on disk.");
            if (dry) return 0;
            UpdatePhase last = UpdatePhase.Checking;
            var progress = new SyncProgress<UpdateProgress>(p =>
            {
                if (p.Phase != last) { last = p.Phase; Console.WriteLine($"[{p.Phase}] {p.Message}"); }
            });
            var result = await client.ApplyAsync(plan, progress, cts.Token);
            Console.WriteLine($"Done: {result.Version}. Wrote {result.FilesWritten}, deleted {result.FilesDeleted}, " +
                              $"downloaded {PatchPublisher.Fmt(result.BytesDownloaded)}, reused {PatchPublisher.Fmt(result.BytesReused)}.");
            if (json)
                Console.WriteLine("RESULT " + Encoding.UTF8.GetString(Json.Serialize(result, indented: false)));
            return 0;
        }

        case "updater-release":
        {
            var a = new Args(rest);
            var exe = a.Req("exe"); var version = a.Req("version"); var url = a.Req("url");
            var key = ReadKey(a.Req("key")); var outDir = a.Req("out"); var notes = a.Opt("notes");
            a.EnsureAllUsed();
            var bytes = File.ReadAllBytes(exe);
            var rel = new UpdaterRelease { Version = version.TrimStart('v'), Url = url, Sha256 = GearChunker.Sha256Hex(bytes), Size = bytes.Length, Notes = notes };
            var json = Json.Serialize(rel);
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "updater.json"), json);
            File.WriteAllText(Path.Combine(outDir, "updater.json.sig"), Signing.Sign(json, key));
            Console.WriteLine($"Wrote updater.json for {rel.Version} ({PatchPublisher.Fmt(rel.Size)}, sha256 {rel.Sha256[..12]}...).");
            return 0;
        }

        case "prune":
        {
            var a = new Args(rest, "dry-run");
            var host = a.Req("host"); var keep = a.Int("keep", 3); var dry = a.Flag("dry-run");
            a.EnsureAllUsed();
            if (keep < 1) throw new ArgumentException("--keep must be at least 1.");
            PatchPublisher.Prune(host, keep, dry, Console.WriteLine);
            return 0;
        }

        case "rollback":
        {
            var a = new Args(rest);
            var host = a.Req("host"); var channel = a.Req("channel"); var version = a.Req("version");
            a.EnsureAllUsed();
            PatchPublisher.Rollback(host, channel, version, Console.WriteLine);
            return 0;
        }

        case "serve":
        {
            var a = new Args(rest);
            var root = a.Req("root"); var port = a.Int("port", 8080);
            var bind = IPAddress.Parse(a.Opt("bind") ?? "127.0.0.1");
            a.EnsureAllUsed();
            await StaticServer.RunAsync(root, bind, port, cts.Token);
            return 0;
        }

        case "pack-skin":
        {
            var a = new Args(rest);
            var src = a.Req("dir"); var outFile = a.Req("out");
            a.EnsureAllUsed();
            SkinPack.Pack(src, outFile);
            var entries = SkinPack.Open(outFile); // read it back: a pack the launcher can't open must not ship
            Console.WriteLine($"Wrote {outFile}: {entries.Count} files, {PatchPublisher.Fmt(new FileInfo(outFile).Length)}.");
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'.\n");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (ArgumentException ex) { Console.Error.WriteLine("error: " + ex.Message); return 2; }
catch (UpdaterException ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
catch (OperationCanceledException) { Console.Error.WriteLine("cancelled"); return 130; }

static string ReadKey(string spec)
{
    if (spec.StartsWith("env:"))
        return Environment.GetEnvironmentVariable(spec[4..]) is { Length: > 0 } v ? v : throw new ArgumentException($"Environment variable {spec[4..]} is empty.");
    return File.ReadAllText(spec);
}

/// <summary>Reports on the calling thread, so console output stays in order.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly object _lock = new();
    public void Report(T value) { lock (_lock) report(value); }
}