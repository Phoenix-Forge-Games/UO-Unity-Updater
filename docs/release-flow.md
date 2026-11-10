# Release flow

There are two separate things to release:

| What | Where it's hosted | Signed with | How it's released |
|---|---|---|---|
| **GameUpdater.exe** (this repo) | GitHub Releases on `Phoenix-Forge-Games/UO-Unity-Updater` | `updater` key (GitHub secret `UPDATER_SIGNING_KEY`) | push a `vX.Y.Z` tag |
| **Game patches** (player build + Addressables content) | phoenix-server ([hosting.md](hosting.md)) | `patch` key (on the build PC only) | `patchtool publish`, then upload |

## Keys

Generate a key pair with `patchtool keygen --out <dir> --name <patch|updater>`. The public halves
are in `keys/` and compiled into every GameUpdater.

| Key | Private half | Public half |
|---|---|---|
| `updater` | `%USERPROFILE%\.uo-unity-updater\keys\updater.pem` and the repo secret `UPDATER_SIGNING_KEY` | `keys/updater.pub` |
| `patch` | `%USERPROFILE%\.uo-unity-updater\keys\patch.pem` (build PC only; never on a server or in CI) | `keys/patch.pub` |

**Back up both `.pem` files** somewhere offline, such as a password manager. Losing a key means you
can't publish anything installed updaters will accept. Recovering from that means shipping a new
GameUpdater that trusts a new key, by hand.

To rotate a key:

1. Add the new `.pub` to `keys/` (for example `patch-2027.pub`; any `patch*.pub` or `updater*.pub`
   file is trusted).
2. Release an updater build. Updaters update themselves, so they will now trust both keys.
3. Start signing with the new key.
4. Later, remove the old `.pub`.

## Releasing the updater

1. Merge to `main`. `build.yml` builds and runs `tests/e2e.ps1`.
2. Tag and push:

   ```bash
   git tag v1.2.0
   git push origin v1.2.0
   ```

3. `release.yml` then does the following:
   - Runs the e2e test.
   - Publishes `GameUpdater.exe` as a self-contained single exe, with the version taken from the tag.
   - Publishes `patchtool` for win-x64 and linux-x64.
   - Writes `updater.json` (version, absolute download URL, SHA-256, size) and signs it with
     `UPDATER_SIGNING_KEY`.
   - Creates the GitHub release with all of these as assets.

   A tag with a suffix, such as `v1.3.0-beta.1`, becomes a prerelease. `releases/latest` skips
   prereleases, so players don't get it.
4. Every installed GameUpdater checks `releases/latest/download/updater.json` at startup. A newer
   version is downloaded, hash-checked and swapped in, and then the updater restarts. If the feed is
   unreachable, the current updater carries on as normal.

Dev builds (version `0.0.0-dev`, as in any local build without `-p:Version`) never self-update.
Otherwise a debug session would replace itself.

## Releasing a game patch

On the build PC:

1. Build the game into a clean folder, player and Addressables content together. See
   [unity-integration.md](unity-integration.md) for **UO Client > Release > Build Patch**. The result
   is the exact folder players should end up with, for example `Builds/Patch/UOClient`.
2. Publish into the local mirror of the patch host:

   ```powershell
   patchtool publish --build "G:\Ultima Online\UnityClient\Builds\Patch\UOClient" `
     --host "G:\Ultima Online\PatchHost" --channel live --version 0.4.0 --product UOClient `
     --launch UOClient.exe --key "$env:USERPROFILE\.uo-unity-updater\keys\patch.pem"
   ```

   It prints how many new blobs the release added and how many bytes need uploading. Only those
   blobs are new on disk. Everything else is shared with earlier versions.
3. Upload the mirror with `tools/deploy-patches.ps1`. It copies new blobs first, then manifests and
   history, and `channel.json` last, so a client never sees a channel that points at missing data.
4. Players get the patch the next time they start GameUpdater. Dedicated servers get it with
   `patchtool sync` ([unity-integration.md](unity-integration.md#dedicated-servers)).

Versions are SemVer and must be new for each publish to a channel. The publisher refuses to
overwrite one unless you pass `--force`.

### Channels

A channel is a folder name, such as `live`, `test` or `server-live`. Blobs are shared between
channels, so promoting a build that was already published to `test` uploads almost nothing new.
To promote, publish the same build folder to `live`. To point a player at a channel, set
`channelUrl` in their `GameUpdater.json`.

### Rolling back

```powershell
patchtool rollback --host "G:\Ultima Online\PatchHost" --channel live --version 0.3.0
```

This restores that version's signed `channel.json` from `history/`. Clients then patch back to it,
again downloading only what differs, provided the old blobs haven't been pruned.

### Pruning

```powershell
patchtool prune --host "G:\Ultima Online\PatchHost" --keep 5 --dry-run
```

This deletes blobs that none of the newest 5 versions of any channel uses. Run it on the mirror, then
mirror the deletions to the server with `deploy-patches.ps1 -Purge`. Keep enough versions for
rollback.
