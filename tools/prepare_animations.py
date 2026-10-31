#!/usr/bin/env python3
"""Convert dsh-pet's VP9-alpha webm clips into OpenPet WebP frame sequences.

ffmpeg must decode with ``-c:v libvpx-vp9``; otherwise the alpha channel in the
webm (BlockAdditional) is dropped and every frame gets a black background.

Output layout::

    assets/animations/<name>/
        0001.webp, 0002.webp, ...
        meta.json   {name, fps, frameWidth, frameHeight, frameCount, source}

Usage:
    python tools/prepare_animations.py
    python tools/prepare_animations.py --all --force --workers 6
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SOURCE = ROOT.parent.parent / "reference" / "dsh-pet" / "dsh-pet" / "assets" / "webm"
DEFAULT_OUTPUT = ROOT / "assets" / "animations"
DEFAULT_MANIFEST = Path(__file__).resolve().parent / "animations.json"


def find_ffmpeg() -> str:
    exe = shutil.which("ffmpeg")
    if not exe:
        raise SystemExit("ffmpeg not found on PATH.")
    return exe


def convert_one(
    ffmpeg: str,
    source: Path,
    out_dir: Path,
    fps: int,
    width: int,
    height: int,
    quality: int,
) -> tuple[str, int, int]:
    name = source.stem
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    pattern = out_dir / "%04d.webp"
    vf = f"fps={fps},scale={width}:{height}:flags=lanczos"
    cmd = [
        ffmpeg,
        "-v",
        "error",
        "-y",
        "-c:v",
        "libvpx-vp9",
        "-i",
        str(source),
        "-vf",
        vf,
        "-c:v",
        "libwebp",
        "-lossless",
        "0",
        "-q:v",
        str(quality),
        "-compression_level",
        "5",
        "-pix_fmt",
        "yuva420p",
        str(pattern),
    ]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed for {name}:\n{result.stderr[-2000:]}")

    frames = sorted(out_dir.glob("*.webp"))
    if not frames:
        raise RuntimeError(f"no frames produced for {name}")

    meta = {
        "name": name,
        "fps": fps,
        "frameWidth": width,
        "frameHeight": height,
        "frameCount": len(frames),
        "source": f"reference/dsh-pet/dsh-pet/assets/webm/{name}.webm",
        "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
    }
    (out_dir / "meta.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    size_kb = sum(f.stat().st_size for f in frames) // 1024
    return name, len(frames), size_kb


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--all", action="store_true", help="convert every webm in the source folder")
    parser.add_argument("--force", action="store_true", help="re-encode even if meta.json exists")
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--limit", type=int, default=0, help="debug: only first N clips")
    args = parser.parse_args()

    ffmpeg = find_ffmpeg()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    fps = int(manifest.get("fps", 12))
    width = int(manifest.get("width", 320))
    height = int(manifest.get("height", 180))
    quality = int(manifest.get("quality", 68))

    if args.all:
        names = sorted(p.stem for p in args.source.glob("*.webm"))
    else:
        names = [item["name"] for item in manifest["animations"]]

    todo: list[tuple[str, Path, Path]] = []
    for name in names:
        source = args.source / f"{name}.webm"
        if not source.exists():
            print(f"[skip] missing source: {name}", file=sys.stderr)
            continue
        out_dir = args.output / name
        if not args.force and (out_dir / "meta.json").exists():
            continue
        todo.append((name, source, out_dir))

    if args.limit > 0:
        todo = todo[: args.limit]

    args.output.mkdir(parents=True, exist_ok=True)
    print(
        f"converting {len(todo)} / {len(names)} clips "
        f"(fps={fps}, {width}x{height}, q={quality}, workers={args.workers})"
    )
    if not todo:
        return 0

    started = time.time()
    failures = 0
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {
            pool.submit(convert_one, ffmpeg, src, out_dir, fps, width, height, quality): name
            for name, src, out_dir in todo
        }
        for index, future in enumerate(as_completed(futures), start=1):
            name = futures[future]
            try:
                _, count, kb = future.result()
                print(f"[{index:>2}/{len(todo)}] {name}: {count} frames, {kb} KB")
            except Exception as exc:  # noqa: BLE001 - report and continue
                failures += 1
                print(f"[{index:>2}/{len(todo)}] FAILED {name}: {exc}", file=sys.stderr)

    elapsed = time.time() - started
    total_kb = sum(f.stat().st_size for f in args.output.rglob("*.webp")) // 1024
    print(f"done in {elapsed / 60:.1f} min; {total_kb / 1024:.1f} MB total; failures={failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
