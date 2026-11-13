"""Build LauncherSkin.pak, the launcher's private skin, from Synty's Interface Fantasy packs.

This script holds only the recipe (which sprites, how they are tinted, cut and combined). The art itself
is licensed, paid content: it is read from your local copy of the packs, combined in a temporary folder,
packed into one encrypted file, and the temporary folder is deleted. Never commit the output, attach it to
a GitHub release, or put it anywhere public. Ship it only inside the friends-only game patch, at the
install root (see docs/launcher-skin.md).

Packs used (kitbashed): Interface - Fantasy Menus, Interface - Fantasy Screens, Interface - Fantasy Warrior HUD.

Usage:
    python tools/launcher-skin/build_skin.py [--synty <Assets/Synty folder>] [--out <LauncherSkin.pak>]
                                             [--patchtool <patchtool.exe>] [--preview <png>]

Needs Python 3.10+ with Pillow.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageEnhance

REPO = Path(__file__).resolve().parents[2]
DEFAULT_SYNTY = Path(r"G:\Ultima Online\UnityClient\Assets\Synty")
DEFAULT_OUT = Path(os.environ.get("USERPROFILE", str(Path.home()))) / ".uo-unity-updater" / "LauncherSkin.pak"

MENUS = ("InterfaceFantasyMenus/Sprites/FantasyMenus", "SPR_FantasyMenus_")
WARRIOR = ("InterfaceFantasyWarriorHUD/Sprites/FantasyWarrior", "SPR_FantasyWarrior_")
SCREENS_SAMPLES = ("InterfaceFantasyScreens/Samples/Sprites", "SPR_FantasyScreens_")

# Pieces are stored at 2x the launcher's logical (96 DPI) size and drawn with scale 0.5, so they stay
# sharp on 150-200% displays.
HI = 2


class Synty:
    def __init__(self, root: Path):
        self.root = root

    def path(self, pack: tuple[str, str], name: str) -> Path:
        p = self.root / pack[0] / f"{pack[1]}{name}.png"
        if not p.exists():
            sys.exit(f"Missing sprite: {p}\nIs the pack imported? Pass --synty <path to Assets/Synty>.")
        return p

    def image(self, pack, name) -> Image.Image:
        return Image.open(self.path(pack, name)).convert("RGBA")

    def border(self, pack, name) -> tuple[int, int, int, int]:
        """Unity's 9-slice border as (left, top, right, bottom). Unity stores x=left, y=bottom, z=right, w=top."""
        meta = self.path(pack, name).with_suffix(".png.meta").read_text(encoding="utf-8", errors="replace")
        m = re.search(r"spriteBorder: \{x: (\d+), y: (\d+), z: (\d+), w: (\d+)\}", meta)
        if not m:
            return (0, 0, 0, 0)
        x, y, z, w = map(int, m.groups())
        return (x, w, z, y)


# ---- image helpers ----

def tint(img: Image.Image, rgb: tuple[int, int, int], alpha: float = 1.0) -> Image.Image:
    """Multiply a (white) sprite by a colour, the way Unity's Image.color does."""
    r, g, b, a = img.split()
    out = Image.merge("RGBA", (
        r.point(lambda v: v * rgb[0] // 255),
        g.point(lambda v: v * rgb[1] // 255),
        b.point(lambda v: v * rgb[2] // 255),
        a.point(lambda v: int(v * alpha)),
    ))
    return out


def over(*layers: Image.Image) -> Image.Image:
    base = Image.new("RGBA", layers[0].size, (0, 0, 0, 0))
    for layer in layers:
        base = Image.alpha_composite(base, layer)
    return base


def greyscale(img: Image.Image, dim: float) -> Image.Image:
    a = img.getchannel("A")
    g = ImageEnhance.Brightness(img.convert("L").convert("RGB")).enhance(dim).convert("RGBA")
    g.putalpha(a)
    return g


def scaled(img: Image.Image, factor: float, slice_=(0, 0, 0, 0), inset=(0, 0, 0, 0)):
    w, h = max(1, round(img.width * factor)), max(1, round(img.height * factor))
    return (img.resize((w, h), Image.LANCZOS), tuple(round(v * factor) for v in slice_), tuple(round(v * factor) for v in inset))


def crop(img: Image.Image, box, slice_=(0, 0, 0, 0)):
    """Crop to box (l, t, r, b); slice borders shrink by however much was cut from each side."""
    l, t, r, b = box
    s = (max(0, slice_[0] - l), max(0, slice_[1] - t), max(0, slice_[2] - (img.width - r)), max(0, slice_[3] - (img.height - b)))
    return img.crop(box), s


def channel_inset(frame: Image.Image) -> tuple[int, int, int, int]:
    """Distance from each edge to the transparent channel inside a bar frame, measured through its centre."""
    a = frame.getchannel("A")
    w, h = frame.size
    cy, cx = h // 2, w // 2
    def run(points):
        seen = False
        for i, (x, y) in enumerate(points):
            opaque = a.getpixel((x, y)) > 40
            if opaque:
                seen = True
            elif seen:
                return i
        return 0
    left = run([(x, cy) for x in range(cx)])
    right = run([(w - 1 - x, cy) for x in range(cx)])
    top = run([(cx, y) for y in range(cy)])
    bottom = run([(cx, h - 1 - y) for y in range(cy)])
    return (left, top, right, bottom)


# ---- the recipe ----

CRIMSON = (150, 30, 20)
CRIMSON_HOT = (226, 70, 38)
CRIMSON_DOWN = (104, 20, 14)
UMBER = (46, 34, 24)
UMBER_HOT = (104, 78, 46)
UMBER_DOWN = (30, 22, 15)
PANEL_FILL = (18, 14, 10)


def build(synty: Synty, out_dir: Path) -> dict:
    images: dict[str, dict] = {}

    def save(role: str, img: Image.Image, file: str, slice_=None, inset=None, scale=1 / HI, fmt="PNG"):
        path = out_dir / file
        if fmt == "JPEG":
            img.convert("RGB").save(path, "JPEG", quality=88, optimize=True)
        else:
            img.save(path, "PNG", optimize=True)
        spec = {"file": file, "scale": scale}
        if slice_ and any(slice_):
            spec["slice"] = list(slice_)
        if inset and any(inset):
            spec["inset"] = list(inset)
        images[role] = spec

    # Backdrop: the Fantasy Screens fortress vista, cropped to the window and darkened where text sits.
    W, H = 960 * HI, 580 * HI
    bg = synty.image(SCREENS_SAMPLES, "ExampleScreenshot_Fortress_01").convert("RGB")
    k = max(W / bg.width, H / bg.height)
    bg = bg.resize((round(bg.width * k), round(bg.height * k)), Image.LANCZOS)
    left = (bg.width - W) // 2
    bg = bg.crop((left, 0, left + W, H))
    shade = Image.new("L", (1, H))
    for y in range(H):
        t = y / H
        top = max(0.0, 0.62 - t * 1.6)            # behind the title
        bottom = max(0.0, (t - 0.45) / 0.55) * 0.8  # behind the panel
        shade.putpixel((0, y), int(255 * min(0.92, 0.18 + top + bottom)))
    shade = shade.resize((W, H))
    bg = Image.composite(Image.new("RGB", (W, H), (8, 6, 4)), bg, shade)
    # Vignette at the sides.
    vig = Image.new("L", (W, 1))
    for x in range(W):
        d = abs(x / W - 0.5) * 2
        vig.putpixel((x, 0), int(255 * max(0.0, d - 0.55) / 0.45 * 0.7))
    bg = Image.composite(Image.new("RGB", (W, H), (0, 0, 0)), bg, vig.resize((W, H)))
    save("background", bg, "background.jpg", fmt="JPEG")

    # Crest above the title: Warrior HUD's winged gold crest with the blue gem.
    crest = synty.image(WARRIOR, "Frame_Top_01")
    crest = crest.crop(crest.getchannel("A").getbbox())
    crest, _, _ = scaled(crest, (104 * HI) / crest.height)
    save("crest", crest, "crest.png")

    # Rule under the title: Fantasy Menus bar 05, both halves.
    lft, rgt = synty.image(MENUS, "Frame_Bar_05_Left"), synty.image(MENUS, "Frame_Bar_05_Right")
    rule = Image.new("RGBA", (lft.width + rgt.width, lft.height), (0, 0, 0, 0))
    rule.paste(lft, (0, 0)); rule.paste(rgt, (lft.width, 0))
    rule = rule.crop(rule.getchannel("A").getbbox())
    rule, _, _ = scaled(rule, (520 * HI) / rule.width)
    save("divider", rule, "divider.png")

    # Bottom panel: Fantasy Menus large box 12 over its own mask, filled near-black.
    frame = synty.image(MENUS, "Frame_Box_Large_12")
    mask = synty.image(MENUS, "Frame_Box_Large_12_Mask")
    panel = over(tint(mask, PANEL_FILL, 0.9), frame)
    panel, sl, _ = scaled(panel, (60 * HI) / 370, synty.border(MENUS, "Frame_Box_Large_12"))
    save("panel", panel, "panel.png", sl)

    # Log frame: Fantasy Menus medium box 02 with a dark fill inside it.
    lf = synty.image(MENUS, "Frame_Box_Medium_02")
    fill = Image.new("RGBA", lf.size, (0, 0, 0, 0))
    fill.paste(Image.new("RGBA", (lf.width - 36, lf.height - 36), (20, 17, 13, 240)), (18, 18))
    log = over(fill, lf)
    log, sl, _ = scaled(log, 0.5, synty.border(MENUS, "Frame_Box_Medium_02"))
    save("log", log, "log.png", sl, inset=(44, 36, 26, 36))

    # Buttons. Play: Fantasy Menus button 07 (arrow-tipped) in crimson; Verify: button 03 in dark umber.
    def button(prefix: str, name: str, colours: dict[str, tuple[int, int, int]], height: int):
        bgs, frm = synty.image(MENUS, f"{name}_Background"), synty.image(MENUS, f"{name}_Frame")
        border = synty.border(MENUS, f"{name}_Frame")
        box = frm.getchannel("A").getbbox()
        for state, colour in colours.items():
            if state == "disabled":
                img = over(tint(bgs, (52, 48, 42)), greyscale(frm, 0.55))
            else:
                f = ImageEnhance.Brightness(frm).enhance(1.4) if state == "hover" else frm
                img = over(tint(bgs, colour), f)
            cut, sl = crop(img, box, border)
            cut, sl, _ = scaled(cut, (height * HI) / cut.height, sl)
            save(f"{prefix}.{state}", cut, f"{prefix}-{state}.png", sl)

    button("button.primary", "Menu_Button_07",
           {"normal": CRIMSON, "hover": CRIMSON_HOT, "pressed": CRIMSON_DOWN, "disabled": (0, 0, 0)}, 70)
    button("button.secondary", "Menu_Button_03",
           {"normal": UMBER, "hover": UMBER_HOT, "pressed": UMBER_DOWN, "disabled": (0, 0, 0)}, 40)

    # Progress bar frame: the rails of Fantasy Menus horizontal scrollbar 03.
    pf = synty.image(MENUS, "Scrollbar_Horizontal_03_Frame")
    pf_border = synty.border(MENUS, "Scrollbar_Horizontal_03_Frame")
    # Crop away the soft shadow above and below the rails so the channel between them is usefully thick.
    pf, pf_border = crop(pf, pf.getchannel("A").point(lambda v: 255 if v > 60 else 0).getbbox(), pf_border)
    inset = channel_inset(pf)
    pf, pf_sl, pf_in = scaled(pf, (30 * HI) / pf.height, pf_border, inset)
    save("progress.frame", pf, "progress-frame.png", pf_sl, pf_in)
    channel_h = pf.height - pf_in[1] - pf_in[3]
    # Track and fill: Warrior HUD's hex bar mask, which fills the channel between the rails.
    bar_mask = synty.image(WARRIOR, "Bar_Horizontal_03_Mask")
    bar_mask = bar_mask.crop(bar_mask.getchannel("A").getbbox())
    msl = synty.border(WARRIOR, "Bar_Horizontal_03_Mask")
    track, tsl, _ = scaled(tint(bar_mask, (24, 19, 14), 0.92), channel_h / bar_mask.height, msl)
    save("progress.track", track, "progress-track.png", tsl)
    front, fsl, _ = scaled(tint(bar_mask, (238, 178, 76)), channel_h / bar_mask.height, msl)
    save("progress.fill", front, "progress-fill.png", fsl)

    return {
        "name": "Britannia Reborn (Synty Interface Fantasy)",
        "colors": {
            "title": "#F4D98E", "titleShade": "#B07A2C", "text": "#F2EADA", "muted": "#B8A888",
            "accent": "#E8B95A", "logBack": "#14110D", "logText": "#CFC3A8", "buttonText": "#FFF3D6",
        },
        "images": images,
    }


def find_patchtool(given: str | None) -> Path:
    if given:
        return Path(given)
    for c in (REPO / "src/PatchTool/bin/Release/net10.0/patchtool.exe", REPO / "src/PatchTool/bin/Debug/net10.0/patchtool.exe"):
        if c.exists():
            return c
    sys.exit("patchtool.exe not found; run `dotnet build -c Release` in the updater repo or pass --patchtool.")


def preview(staging: Path, manifest: dict, out: Path):
    """Contact sheet of the pieces (for checking the recipe). It contains the art: keep it private too."""
    roles = [r for r in manifest["images"] if r != "background"]
    tiles = [Image.open(staging / manifest["images"][r]["file"]).convert("RGBA") for r in roles]
    w = max(t.width for t in tiles) + 40
    h = sum(t.height + 30 for t in tiles) + 20
    sheet = Image.new("RGBA", (w, h), (60, 60, 70, 255))
    y = 10
    for t in tiles:
        sheet.alpha_composite(t, (20, y))
        y += t.height + 30
    sheet.save(out)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--synty", type=Path, default=DEFAULT_SYNTY, help="the Unity project's Assets/Synty folder")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help=f"output pack (default {DEFAULT_OUT})")
    ap.add_argument("--patchtool", help="path to patchtool.exe")
    ap.add_argument("--preview", type=Path, help="also write a contact sheet of the pieces (private: contains the art)")
    args = ap.parse_args()

    out = args.out.resolve()
    if REPO in out.parents:
        sys.exit(f"Refusing to write the skin inside the public repo ({out}). Pick a private location.")

    synty = Synty(args.synty)
    patchtool = find_patchtool(args.patchtool)
    staging = Path(tempfile.mkdtemp(prefix="br-skin-"))
    try:
        manifest = build(synty, staging)
        (staging / "skin.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        if args.preview:
            preview(staging, manifest, args.preview)
        subprocess.run([str(patchtool), "pack-skin", "--dir", str(staging), "--out", str(out)], check=True)
    finally:
        shutil.rmtree(staging, ignore_errors=True)


if __name__ == "__main__":
    main()
