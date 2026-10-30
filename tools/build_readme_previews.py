"""Export lightweight README previews from the existing, unchanged artwork.

Requires Pillow. Run from any directory: python tools/build_readme_previews.py
Use --only gpt to update GPT previews without rewriting existing previews.
"""
import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "desktop-pet" / "assets"
OUTPUT = ROOT / "docs" / "previews"
CLASSIC = ASSETS / "scenes" / "deepseek-small"
GPT = ASSETS / "scenes" / "gpt-small"
SIZE = (320, 320)
BACKGROUND = (245, 247, 251)
STATES = ("idle", "working", "walk-right", "drag", "angry", "sing")
GPT_STATES = ("idle", "working", "walk-right", "drag", "happy", "angry", "jump-right", "climb-right")


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--only", choices=("skins", "classic", "gpt"), help="Rebuild only this group; keep other previews and metadata unchanged")
    args = parser.parse_args()
    groups = (args.only,) if args.only else ("skins", "classic", "gpt")
    report_path = OUTPUT / "sources.json"
    report = json.loads(report_path.read_text(encoding="utf-8-sig")) if report_path.exists() else {}
    report.update(size=list(SIZE), animation_background=list(BACKGROUND))
    for group in groups:
        (OUTPUT / group).mkdir(parents=True, exist_ok=True)
        report[group] = []
    skins = json.loads((ASSETS / "skins" / "manifest.json").read_text(encoding="utf-8-sig"))["skins"] if "skins" in groups else []
    for skin in skins:
        source = ASSETS / "skins" / skin["file"]
        target = OUTPUT / "skins" / (skin["id"] + ".png")
        with Image.open(source) as picture:
            # Full canvas keeps the original composition; RGBA keeps soft edges.
            thumbnail = picture.convert("RGBA").resize(SIZE, Image.Resampling.LANCZOS)
            thumbnail.info.clear()
            thumbnail.save(target, optimize=True)
        report["skins"].append({"id": skin["id"], "source": source.relative_to(ROOT).as_posix(), "source_sha256": sha256(source), "preview_bytes": target.stat().st_size})

    for group, scene, filename, states in (
        ("classic", CLASSIC, "deepseek-small.petanim.json", STATES),
        ("gpt", GPT, "gpt-small.petanim.json", GPT_STATES),
    ):
        if group not in groups:
            continue
        manifest_path = scene / filename
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        for state in states:
            entry = export_animation(scene, manifest, state, group)
            if group == "gpt":
                entry.update(manifest=manifest_path.relative_to(ROOT).as_posix(),
                             manifest_sha256=sha256(manifest_path),
                             source_repeat=manifest["actions"][state].get("repeat", False),
                             preview_repeats_forever=True, added_hold_ms=0)
            report[group].append(entry)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"updated_groups": {group: len(report[group]) for group in groups},
                      "preview_bytes": sum(item["preview_bytes"] for group in groups for item in report[group])}))


def export_animation(scene, manifest, state, group):
    action = manifest["actions"][state]
    phases = (["start", "loop", "loop"] if state == "drag" else ["loop"])
    frames, durations, sources = [], [], []
    for phase_name in phases:
        phase = action[phase_name]
        if phase["type"] != "frames" or len(phase["frames"]) != len(phase["durationsMs"]):
            raise ValueError("Preview expects complete PNG frames with explicit durations")
        for filename, duration in zip(phase["frames"], phase["durationsMs"]):
            if duration <= 0 or duration % 10:
                raise ValueError("GIF must preserve the original 10 ms frame timing")
            source = scene / filename
            with Image.open(source) as picture:
                frame = picture.convert("RGBA").resize(SIZE, Image.Resampling.LANCZOS)
                matte = Image.new("RGBA", SIZE, BACKGROUND + (255,))
                matte.alpha_composite(frame)
                frames.append(matte.convert("RGB"))
            durations.append(duration)
            sources.append({"file": source.relative_to(ROOT).as_posix(), "sha256": sha256(source)})
    # One palette for every frame prevents per-frame palette color changes.
    palette_input = Image.new("RGB", (SIZE[0], SIZE[1] * len(frames)), BACKGROUND)
    for index, frame in enumerate(frames):
        palette_input.paste(frame, (0, index * SIZE[1]))
    palette = palette_input.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
    indexed = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in frames]
    target = OUTPUT / group / (state + ".gif")
    indexed[0].save(target, save_all=True, append_images=indexed[1:], duration=durations,
                    loop=0, disposal=2, optimize=False)
    with Image.open(target) as result:
        actual_durations = []
        for index in range(result.n_frames):
            result.seek(index)
            actual_durations.append(result.info["duration"])
        if actual_durations != durations:
            raise ValueError("Encoded preview differs from the source frame timing")
    return {"id": state, "phases": phases, "frames": len(frames),
            "durations_ms": durations, "duration_ms": sum(durations),
            "preview_bytes": target.stat().st_size, "sources": sources}


if __name__ == "__main__":
    main()
