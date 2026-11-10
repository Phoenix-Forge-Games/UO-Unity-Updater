# Hosting

Decided on 2026-11-09: **hybrid hosting**.

| What | Host | Why |
|---|---|---|
| GameUpdater releases (`GameUpdater.exe`, `updater.json`) | GitHub Releases on the public `Phoenix-Forge-Games/UO-Unity-Updater` | Free CDN, always up, and no paid content in it |
| Game patches (`blobs/`, `<channel>/…`) | phoenix-server | Private and under your control. The built game contains (encrypted) paid art, which shouldn't sit on a public GitHub repo. |

The client doesn't care where files live. `channelUrl` can be any `http://`, `https://` or
`file://` URL, so moving the patch host later only means changing `Defaults.ChannelUrl` (or a
player's `GameUpdater.json`) and releasing an updater.

## Game patches on phoenix-server: what's still to set up

None of this has been done yet. Each step changes the server or the network, so each one is yours to
approve, and the firewall and router steps are yours to do.

1. **Folder and share.** Create a folder such as `C:\GameServers\Patches`. Share it to your own
   account as `\\phoenix-server\Patches` so `tools/deploy-patches.ps1` can upload to it from the
   build PC.
2. **A static web server** serving that folder read-only. Pick one:
   - **IIS.** Built into Windows 11 Pro, so nothing to download, but it's a Windows feature to enable.
     It needs:
     - a MIME map for `.gz` and for extensionless files (blob names have no extension), both as
       `application/octet-stream`;
     - static compression turned off for this site (blobs are already compressed, and `.gz` must be
       served as-is, without `Content-Encoding`);
     - directory browsing left off.
   - **Caddy.** A single exe (a download, so it needs your OK) that gets HTTPS certificates
     automatically once a domain name points at you. Its config is three lines: `file_server` with
     `root`.
   - Not `patchtool serve`: it's a test server with no hardening.
3. **Reachability for friends outside your LAN.**
   - Forward a port (443, or 80) on the TP-Link Deco to 192.168.68.91.
   - Add an inbound Windows Firewall rule for it. You make this change; the command is in the
     section below.
   - Give it a name: the Deco's built-in TP-Link DDNS, or a domain you own.
4. **HTTPS.** Every file is signature- or hash-checked, so a tampered download is always rejected,
   even over plain HTTP. HTTPS still keeps ISPs from seeing or caching traffic and avoids "not
   secure" warnings, so use it where you can (Caddy makes it automatic).
5. **Set the URL.**
   - Put the public URL (for example `https://patch.example.net/live/channel.json`) in
     `src/Updater.Core/UpdaterConfig.cs` → `Defaults.ChannelUrl`.
   - Tag an updater release. Installed updaters update themselves and start using it.
   - Until then, testers can set `channelUrl` in `GameUpdater.json`.

Upload bandwidth: a first install downloads the whole game from your home connection. Patches are
small. If first installs get too slow, move `blobs/` to a CDN or object storage (for example
Cloudflare R2), set `blobBase` to that absolute URL, and keep `channel.json` at home.

## Firewall command (for you to run, if you pick port 443)

```powershell
New-NetFirewallRule -DisplayName "Game patches (HTTPS)" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow -Profile Any
```

Run it on phoenix-server as administrator.

Foundry VTT also runs on phoenix-server (port 30000). None of the steps above touch it, but check
whether a game is live before you restart anything there.
