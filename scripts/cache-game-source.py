#!/usr/bin/env python3
"""Keep locally decompiled assemblies separate by game build and mod package."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def segment(value):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 ._+-]*", value):
        raise argparse.ArgumentTypeError("use a game build or package id without path separators")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-build", required=True, type=segment, help="Full game version and revision")
    parser.add_argument("--package", required=True, type=segment, help="Mod package id or ludeon.rimworld")
    parser.add_argument("--assembly", required=True, type=Path)
    parser.add_argument("--reference-dir", type=Path)
    parser.add_argument("--ilspycmd", default="ilspycmd")
    parser.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "rimcp" / "source")
    args = parser.parse_args()
    assembly = args.assembly.resolve(strict=True)
    if assembly.suffix.lower() != ".dll":
        parser.error("--assembly must be a local DLL")
    tool = shutil.which(args.ilspycmd)
    if not tool:
        parser.error("ilspycmd is unavailable. Pass --ilspycmd with the local executable path.")
    tool_version = subprocess.check_output([tool, "--version"], text=True).strip()
    references = args.reference_dir.resolve(strict=True) if args.reference_dir else assembly.parent
    digest = sha256(assembly)
    identity = {"gameBuild": args.game_build, "packageId": args.package, "dll": assembly.name,
                "sha256": digest, "ilspycmd": tool_version, "referenceDirectory": str(references)}
    key = hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()[:16]
    parent = args.cache.expanduser().resolve() / args.game_build / args.package
    destination = parent / f"{assembly.name}-{key}"
    if not destination.is_dir():
        parent.mkdir(parents=True, exist_ok=True)
        staging = Path(tempfile.mkdtemp(prefix=".decompile-", dir=parent))
        try:
            subprocess.run([tool, "-p", "-r", str(references), "-o", str(staging / "source"), str(assembly)], check=True)
            (staging / "manifest.json").write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
            try:
                os.rename(staging, destination)
            except OSError:
                if not destination.is_dir():
                    raise
        finally:
            if staging.exists():
                shutil.rmtree(staging)
    print(destination)


if __name__ == "__main__":
    main()
