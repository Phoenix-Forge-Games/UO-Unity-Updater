# Launcher skin

The launcher window (`src/GameUpdater/MainForm.cs`, `Theme.cs`) has two looks:

- **Built-in:** gold-on-dark, drawn entirely in code. This is what the public `BritanniaReborn.exe` carries,
  and what a player sees before the game is installed or if the skin is missing or damaged.
- **Skinned:** art from Synty's *Interface - Fantasy* packs (Menus, Screens and Warrior HUD, kitbashed), so the
  launcher matches the game's UI. Used whenever `<install>\LauncherSkin.pak` exists.

## Keeping the paid art private

Synty art is licensed to us and must not be redistributed. This repository and its GitHub releases are
public, so:

- **No Synty files in this repo or in the exe.** The repo holds only the *recipe*
  (`tools/launcher-skin/build_skin.py`: sprite names and how they are tinted, cut and combined).
- **The skin is one encrypted file, never loose images.** `LauncherSkin.pak` is a zip of `skin.json` plus
  the images, AES-encrypted (`src/Updater.Core/SkinPack.cs`). The launcher decrypts it in memory and never
  writes the images to disk.
- **It ships only in the friends-only game patch**, at the install root, alongside the game's own
  (also encrypted) content. Never attach it to a GitHub release or commit it. `.gitignore` blocks `*.pak`.

The encryption is obfuscation, not DRM: the key is in the public launcher. It stops the art sitting in
the install folder as loose PNGs. Access to the file itself is limited by the private patch host.

## Building the skin

Needs Python 3.10+ with Pillow, the updater built (`dotnet build -c Release`), and the Synty packs
imported in the Unity project (`Assets/Synty`, git-ignored there).

```powershell
python tools/launcher-skin/build_skin.py
```

This writes `%USERPROFILE%\.uo-unity-updater\LauncherSkin.pak` (next to the signing keys, outside every
repo). Options: `--synty <Assets/Synty>`, `--out <file>` (refused if inside this repo),
`--preview <png>` (a contact sheet of the pieces, which contains the art, so keep it private too).

## Shipping it

`docs/unity/publish-patch.ps1` copies the pack into the patch build root before publishing, when it
exists (`-LauncherSkin <file>` to use another path, `-LauncherSkin none` to skip). Players get it with
the next game patch. The launcher picks it up on its next start, or right after an update installs it.

## Trying a skin

```powershell
BritanniaReborn.exe --skin C:\path\to\LauncherSkin.pak   # use this pack
BritanniaReborn.exe --skin none                          # force the built-in look
```

## skin.json

```jsonc
{
  "name": "Britannia Reborn (Synty Interface Fantasy)",
  "colors": { "title": "#F4D98E", "titleShade": "#B07A2C", "text": "#F2EADA", "muted": "#B8A888",
              "accent": "#E8B95A", "logBack": "#14110D", "logText": "#CFC3A8", "buttonText": "#FFF3D6" },
  "images": {
    "panel": { "file": "panel.png", "slice": [60, 60, 60, 60], "scale": 0.5 }
  }
}
```

- `slice` is the nine-slice border `[left, top, right, bottom]` in image pixels (the same numbers as
  Unity's sprite border). `inset` is the content area inside a piece. `scale` maps image pixels to
  logical (96 DPI) pixels, so 0.5 means the image is stored at 2x.
- Roles: `background` (cover-fitted), `crest`, `divider` (fitted), `panel`, `log` (with `inset` for
  the text), `progress.frame` (with `inset` for the channel), `progress.track`, `progress.fill`,
  `button.primary.{normal,hover,pressed,disabled}` and `button.secondary.{...}`.
- Every role is optional. Anything missing is drawn by the built-in look.
