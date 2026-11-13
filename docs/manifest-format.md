# Patch format

A patch host is plain static files: any web server, GitHub release assets, or a `file://` folder.

```
<host root>/
  blobs/                         shared by every channel and version (content-addressed)
    3f/3fa53f5c...e1             raw blob, named by the SHA-256 of its uncompressed bytes
    a7/a7e1aade...42.gz          gzip-stored blob (".gz" = stored compressed)
  <channel>/                     e.g. live, test, server-live
    channel.json                 signed pointer to the current version  <- clients poll this
    channel.json.sig
    manifests/<version>.json.gz  full file list of each version
    history/<version>.channel.json(.sig)   every channel.json ever published, for rollback
```

## channel.json

```json
{
  "format": 1,
  "product": "UOClient",
  "channel": "live",
  "version": "0.4.0",
  "manifest": "manifests/0.4.0.json.gz",
  "manifestSha256": "<sha-256 of the UNcompressed manifest JSON>",
  "blobBase": "../blobs/",
  "published": "2026-11-09T21:00:00+00:00",
  "totalSize": 2147483648
}
```

`manifest` and `blobBase` are resolved relative to the channel.json URL, so the blob store can live
on another host (a CDN, for example) if `blobBase` is set to an absolute URL.

### Signatures (`*.sig`)

One line: `<keyId> <base64 signature>`.

- The signature is ECDSA P-256 with SHA-256, in IEEE P1363 (r‖s) form, over the exact bytes of
  `channel.json` or `updater.json`.
- `keyId` is the first 16 hex characters of the SHA-256 of the public key's SubjectPublicKeyInfo.
- Clients accept only keys compiled in from `keys/patch*.pub` (game channels) or
  `keys/updater*.pub` (the updater feed), plus any extra keys in `BritanniaReborn.json`.
- The signature covers `manifestSha256`, and the manifest covers every blob hash, so one signature
  check protects the whole version.

## Manifest (`manifests/<version>.json.gz`, gzip of UTF-8 JSON)

```json
{
  "format": 1,
  "product": "UOClient",
  "version": "0.4.0",
  "created": "2026-11-09T21:00:00+00:00",
  "launch": "UOClient.exe",
  "launchArgs": null,
  "chunking": { "algorithm": "gear-cdc-v1", "threshold": 8388608, "min": 262144, "avg": 1048576, "max": 4194304 },
  "files": [
    { "path": "UOClient_Data/Managed/Assembly-CSharp.dll", "size": 500000, "sha256": "…", "enc": "gzip", "stored": 231004 },
    { "path": "UOClient_Data/resources.assets", "size": 25165824, "sha256": "…",
      "chunks": [ { "sha256": "…", "size": 1180412, "enc": "none", "stored": 1180412 }, … ] }
  ]
}
```

- `path`: relative, `/`-separated. The client rejects absolute paths, `..`, drive letters, reserved
  Windows names and anything under `.patch/`.
- Files below `threshold` are one blob, whose name is the file's `sha256`. `enc` and `stored` give how
  that blob is kept on the host.
- Files at or above `threshold` have `chunks` in order. Each chunk is its own blob. Chunk offsets are
  the running sum of the sizes.
- `enc` is `none` or `gzip`. The publisher stores gzip only if it saves at least 5%. Unity bundles
  (LZ4) and IL2CPP binaries mostly go up raw.

### Chunking (`gear-cdc-v1`)

FastCDC-style content-defined chunking:

- A 64-bit gear rolling hash: `h = (h << 1) + GEAR[byte]`. The `GEAR` table is fixed (splitmix64 with
  a fixed seed; see `GearChunker.cs`).
- Normalized chunking: the first `min` bytes are skipped. A boundary falls where `h & mask == 0`,
  using a harder mask (avg bits + 2) before `avg` bytes and an easier one (avg bits − 2) after.
- Every chunk is cut at `max` bytes at the latest.

Boundaries depend only on nearby bytes. Inserting or deleting data changes the chunks around the edit
and leaves the rest of the file's chunks byte-identical. Changing the table or the parameters changes
every boundary, so do that only with a new algorithm name.

## Client algorithm (`PatchClient`)

1. **Check.** Fetch `channel.json` and `.sig` and verify the signature. Fetch the manifest and verify
   `manifestSha256`.
2. **Plan.** For every manifest file:
   - Take its local hash from `.patch/installed.json` when size and mtime still match. Otherwise
     hash the file, or chunk it if the target file is chunked. `--verify` always hashes.
   - If the hashes are equal, the file is unchanged.
   - If not, the file is marked changed and its current content is indexed as a source of reusable
     chunks.
   - Files from the previous manifest that are gone get deleted, but they are indexed first, so a
     renamed file copies from disk instead of downloading.
3. **Download** every needed blob that no local source has, 4 at a time, into `.patch/cache/`.
   Each blob is decompressed and hashed while it streams, and a mismatch aborts the update.
4. **Assemble** each changed file in `.patch/staging/` from cached blobs and local ranges. Every
   chunk is re-hashed, and so is the whole file.
5. **Swap.** Check that no target file is locked (the game is running). Move each original to
   `.patch/backup/` and the staged file into place, then delete the removed files.
   - If anything fails, every move is reversed.
   - If the process dies mid-swap, the next run restores from `backup/` first. `installed.json` is
     written only after a complete swap.
6. Write `installed.json` (the manifest and file stamps), then clean up staging, backup and cache.

An interrupted download resumes from the cache: blobs already fetched are hash-checked and reused.

## updater.json (GitHub release asset)

```json
{ "format": 1, "version": "1.2.0", "url": "https://github.com/…/releases/download/v1.2.0/BritanniaReborn.exe",
  "sha256": "…", "size": 51626722, "notes": null }
```

It is signed like `channel.json`, with `updater.json.sig`. The updater fetches
`…/releases/latest/download/updater.json`, which is always the newest non-prerelease release.
