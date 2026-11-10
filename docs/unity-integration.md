# Unity project changes for Addressables and incremental patches

This plan is for whoever works in `G:\Ultima Online\UnityClient`. Nothing here has been applied to
the Unity project yet. The drop-in files are in [`docs/unity/`](unity/).

Two of them, `PatchPaths.cs` and `publish-patch.ps1`, are complete. The other two,
`PatchBuild.cs` and `EncryptedAssetBundleProvider.cs`, are sketches written against the public
Addressables API. They have **not been compiled** against the package version you'll install, so
check them when you apply them.

## The idea

GameUpdater installs one folder per version: the player **and** the Addressables content. Its manifest
diff downloads only changed files, and only the changed chunks of big files. Addressables contributes
by keeping content in many separately versioned bundles plus a catalog:

- If a world chunk or one art pack changes, only that bundle and the catalog change, so players
  download only those.
- The game never downloads anything itself. Addressables loads its "remote" catalog and bundles from
  `<install>/Content/<BuildTarget>/`, which the updater has already filled and hash-checked. This keeps
  one verified transport for players, offline play, and dedicated servers (which need the same world
  data).
- Content-only patches (world edits, art, data) don't need a player rebuild: **Build Patch (content
  only)** swaps `Content/` and keeps the previous player.

## The blocker today: `content.pak`

`ReleaseBuilder.EncryptBundle` picks a **random nonce on every build**, and all paid art is one
bundle. So every release changes every byte of `content.pak`, and players would re-download all the
paid art on every patch. AES-CTR keyed by position also defeats chunk-level delta. The fix is in
step 4 below:

- Move the paid art into several Addressables groups.
- Encrypt each bundle separately with a nonce derived from its plaintext hash. An unchanged bundle
  then gives the identical file, and the updater skips it.

## Steps

### 1. Install Addressables

Add `com.unity.addressables` through the Package Manager, using the version the Package Manager lists
as verified for 6000.6.8f1. This is a package download, so ask the user first.

### 2. Profile and settings

Window > Asset Management > Addressables > Profiles. Create a profile named **`Patch`**:

| Variable | Value |
|---|---|
| `Remote.BuildPath` | `ServerData/[BuildTarget]` |
| `Remote.LoadPath` | `{UOClient.Patching.PatchPaths.ContentRoot}/[BuildTarget]` |

`{…}` is evaluated at runtime by reflection, so add [`PatchPaths.cs`](unity/PatchPaths.cs) as
`Assets/UOClient/Scripts/Patching/PatchPaths.cs`. Add `ServerData/` to `.gitignore`.

In AddressableAssetSettings:

- **Build Remote Catalog:** on. Build and load paths are `Remote.BuildPath` / `Remote.LoadPath`.
- **Player Version Override:** `content`. The catalog is then always named `catalog_content.*`, so
  each patch changes one file instead of adding a new name.
- **Unique Bundle IDs:** off.
- **Only update catalogs manually:** off. The default startup check then sees the new catalog the
  updater installed.
- Play Mode Script: keep **Use Asset Database** for everyday editor work.

### 3. Groups

Settings for every group:

- Build & Load Paths: **Remote**.
- Compression: **LZ4**.
- **Use Asset Bundle Cache: off.** Bundles are local files; caching would copy them again.
- **Asset Bundle CRC: disabled.** The updater already verifies SHA-256, and CRC won't match
  encrypted files.
- **Bundle Naming: Append Hash to Filename.** A changed bundle gets a new name, the old file drops out
  of the manifest, and the updater deletes it.
- Include in Build: on.

The layout matters most for patch size:

| Group | Contents | Bundle Mode |
|---|---|---|
| `World_<Facet>` | the baked world chunk files, one per facet | **Pack Separately** (one chunk = one bundle, so a world edit ships just that chunk) |
| `Protected_<Pack>` (e.g. `Protected_SyntyFantasyKingdom`, `Protected_Sidekick`, `Protected_BK`) | paid art, one group per source pack (anything `ReleaseBuilder.ProtectedRoots` covers today) | Pack Together, or Pack Together By Label for big packs (split Sidekick into a few labels) |
| `Data` | JSON/text data, prefabs that aren't paid art | Pack Separately |

Don't put the imported UO content database in Addressables or in the build folder. It's
player-generated, so keep it outside anything the manifest lists, for example
`<install>/UserData/` or `%LOCALAPPDATA%`. The updater never touches unlisted files.

### 4. Encrypted paid art (replaces `content.pak`)

1. Add [`EncryptedAssetBundleProvider.cs`](unity/EncryptedAssetBundleProvider.cs) under
   `Assets/UOClient/Scripts/Content/`.
   - On each `Protected_*` group, set **Asset Bundle Provider** to it.
   - First search the installed package for `IDataConverter` / `DataStreamProcessor`. If the package
     still has a stream-processing hook, implementing that with `CtrCipherStream` is simpler.
2. [`PatchBuild.cs`](unity/PatchBuild.cs) encrypts those bundles after the Addressables build.
   - It uses the same header as `content.pak` (magic, format, nonce) and the same
     `ContentKey.Get()` / `CtrCipherStream`.
   - The nonce is `SHA256(plaintext)[0..8]`. CTR is only unsafe when one key and nonce encrypt
     *different* plaintexts, and here they never do.
3. Switch `WorldView`'s asset fields and house-kit lookups from the `ContentPack` asset to
   Addressables loads: `Addressables.LoadAssetAsync<T>(address)` or `AssetReference` fields.
4. Then remove `content.pak` from `ReleaseBuilder`:
   - delete the `ContentPack` bundle build, `EncryptBundle` and the pak verification;
   - **keep** `ReleaseBuildGuard`, the `Resources`-folder move-aside, IL2CPP, the DoNotShip
     relocation, and the scan that checks no protected asset names appear in the player data.
   - Extend that scan to check that every `Protected_*` bundle in `Content/` starts with the pak magic,
     meaning it really is encrypted.

### 5. Build script

Add [`PatchBuild.cs`](unity/PatchBuild.cs) as `Assets/UOClient/Editor/PatchBuild.cs`. It adds:

- **UO Client > Release > Build Patch:** Addressables build, then encryption, then
  `ReleaseBuilder.Build("Builds/Patch/UOClient")`, then a copy of `ServerData/StandaloneWindows64`
  into `Builds/Patch/UOClient/Content/StandaloneWindows64`.
- **… Build Patch (content only):** the same, but keeps the previous player. Use it only when no code
  or scene in the player changed.
- Batch mode: `-executeMethod UOClient.EditorTools.PatchBuild.BuildFromCommandLine [-contentOnly]`.

At game startup, call `Addressables.InitializeAsync()` before the world loads. It reads the catalog
from `Content/`. Nothing goes over the network, because Remote.LoadPath is a local path.

### 6. Publish script

Add [`publish-patch.ps1`](unity/publish-patch.ps1) as `Tools/Release/publish-patch.ps1`:

```powershell
./Tools/Release/publish-patch.ps1 -Version 0.4.0              # publish to 'live' and upload
./Tools/Release/publish-patch.ps1 -Version 0.4.1-test.1 -Channel test -NoDeploy
```

It runs the same must-not-ship checks as `publish-release.ps1`, then
`patchtool publish --launch UOClient.exe` into `G:\Ultima Online\PatchHost`, then
`deploy-patches.ps1` to phoenix-server.

The old `publish-release.ps1`, which zips the build into a public GitHub release, should be retired
once patches go live. A public zip would also put the encrypted art on GitHub.

### 7. Dedicated servers

Build the headless server (Dedicated Server target) into `Builds/Patch/UOServer` the same way,
with the world bundles but without the paid-art groups if the server doesn't render. Then:

- Publish it to its own channel:

  ```powershell
  patchtool publish --channel server-live --launch UOServer.exe …
  ```

- On the server box, update before every start, from a scheduled task or start script:

  ```powershell
  patchtool sync --channel-url https://<patch host>/server-live/channel.json --dir C:\GameServers\UOServer
  ```

  `patchtool` comes from the updater's GitHub release, in win-x64 and linux-x64 builds. It trusts
  the compiled-in patch key.

Blobs are shared across channels, so world bundles common to client and server are stored once.

### 8. Keep builds deterministic

Anything that changes bytes needlessly makes players download it:

- **Don't change the Unity version** between ordinary patches. An editor upgrade rebuilds every
  bundle, so expect a near-full download.
- No timestamps or random values in serialized data or bundle contents. The build already avoids
  them, except for the old random pak nonce.
- `GameAssembly.dll` (IL2CPP) changes on any code change. It is large, so the chunker sends only the
  changed regions, but expect a few MB per code patch.
- Leave Addressables' build cache alone between builds, and don't "clean build" bundles unless
  something is wrong. Unchanged groups then reproduce identical bundles.

### 9. CI for game builds

The game repo can't build in GitHub Actions because the paid packs aren't in it. So game patches are
built and published from the build PC with the two menu items and scripts above. If you want push
button builds later, a self-hosted GitHub Actions runner on the build PC can run the batch-mode
command plus `publish-patch.ps1`. That needs the user's go-ahead, since it installs a runner service.

## Checklist for the applying session

- [ ] Addressables installed, with the user's OK for the download
- [ ] `Patch` profile and settings from step 2; `PatchPaths.cs` added; `ServerData/` ignored
- [ ] Groups from step 3; world chunks Pack Separately
- [ ] Paid art in `Protected_*` groups through `EncryptedAssetBundleProvider`; runtime loads switched
      from `ContentPack` to Addressables
- [ ] `content.pak` removed from `ReleaseBuilder`, with the guards kept
- [ ] `PatchBuild.cs` and `publish-patch.ps1` added
- [ ] Test: two patch builds with one world-chunk edit. `patchtool publish` should report about one
      bundle plus the catalog as new blobs, and a `patchtool sync` from the first build to the second
      should download only those.
