# UO Unity Updater

The updater and patch tooling for the UO Unity game (`Phoenix-Forge-Games/UO-Unity-Client`).

- **Updates download only what changed.** Each game version has a manifest that lists every file with
  its SHA-256. Small files are stored once as blobs named by their hash. Large files (8 MB or more)
  are split into content-defined chunks (about 1 MB each). An update fetches only the blobs that
  aren't already on disk, so an edit inside a 2 GB data file downloads a few MB, and a renamed or moved
  file downloads nothing. That covers the player build (exe, DLLs, `*_Data`) and Addressables
  content (catalog plus bundles) alike.
- **Everything is verified.** The channel file is signed with ECDSA P-256. It pins the manifest's
  hash, and the manifest pins every blob's hash. Nothing is installed unless its bytes match. Files
  are staged first and swapped in together, and the swap rolls back if it fails.
- **The updater updates itself.** `BritanniaReborn.exe` reads a signed `updater.json` from this repo's
  latest GitHub release. When it finds a newer version, it downloads it, checks the hash, swaps the exe
  and restarts. Players never do anything by hand.

## What's in the repo

| Path | What |
|---|---|
| `src/Updater.Core` | Manifest format, chunker, signing, publisher, patch client, self-updater |
| `src/GameUpdater` | The player-facing Windows app, the Britannia Reborn launcher (`BritanniaReborn.exe`; `GameUpdater.exe` before v0.3.0): a themed window ([skin](docs/launcher-skin.md)), or `--headless` |
| `src/PatchTool` | `patchtool` CLI: `publish`, `sync`, `updater-release`, `prune`, `rollback`, `keygen`, `pack-skin`, `serve` |
| `tools/launcher-skin` | Recipe that builds the launcher's private Synty skin (`LauncherSkin.pak`); no art in the repo |
| `tests/e2e.ps1` | End-to-end test, also run in CI (see below) |
| `keys/*.pub` | Trusted public keys, compiled into the updater. Private keys are never committed. |
| `docs/` | [Manifest format](docs/manifest-format.md), [release flow](docs/release-flow.md), [hosting](docs/hosting.md), [Unity integration](docs/unity-integration.md), [launcher skin](docs/launcher-skin.md) |

## Player layout

```
<anywhere>\
  BritanniaReborn.exe        <- the only thing a player downloads
  BritanniaReborn.json       <- optional overrides (channel URL, install folder)
  Game\                  <- managed by the updater
    UOClient.exe, UOClient_Data\..., Content\...
    LauncherSkin.pak     <- the launcher's skin, delivered by the private game patch
    .patch\              <- updater state (installed manifest, download cache)
```

Files in `Game\` that aren't in any manifest (settings, saves, the imported UO content database)
are never touched. Only files the previous version installed are ever deleted.

GameUpdater command line: `--headless` (no window; output goes to the console),
`--launch` (start the game when ready), `--verify` (re-hash every file and repair),
`--no-self-update`, `--log <file>`, `--version`, `--skin <pack|none>`.

## Build and test

Needs the .NET 10 SDK.

```powershell
dotnet build -c Release
pwsh tests/e2e.ps1                                   # in CI
powershell -File tests/e2e.ps1 -RuntimeFrameworkVersion 10.0.9   # locally, restores offline from the NuGet cache
```

The e2e test publishes three synthetic builds to a local HTTP host and checks the following:

- A fresh install matches the build, and syncing again downloads nothing.
- The upgrade downloads only the changes (a changed DLL, a new catalog, a new bundle, and the chunks
  around a 100 KB insertion in a 24 MB file). A renamed bundle is reused from disk, and a removed
  bundle is deleted.
- Player files outside the manifest survive.
- A corrupted blob and a tampered `channel.json` are both rejected, and the install is left as it was.
- `--verify` repairs a damaged file that kept its size and timestamp.
- GameUpdater 1.0.0 replaces itself with 1.0.1 from a signed feed, restarts, and installs the game.
- The launcher window opens and draws (`--ui-selftest`) on a fresh install with no skin, with
  `--skin none`, with a garbage or damaged skin pack (built-in look), and with a valid skin.
- `patchtool pack-skin` packs a generated skin, reads it back, gives the same bytes every time, and
  leaves nothing readable without decrypting.

## Releases

- **Updater:** push a tag `vX.Y.Z`. [`release.yml`](.github/workflows/release.yml) tests, builds and signs it,
  then publishes `BritanniaReborn.exe`, `updater.json` and `updater.json.sig`, plus `patchtool` for
  win-x64 and linux-x64. See [docs/release-flow.md](docs/release-flow.md).
- **Game patches:** `patchtool publish` into a local mirror of the patch host, then upload the new files.
  See [docs/release-flow.md](docs/release-flow.md) and [docs/hosting.md](docs/hosting.md).
