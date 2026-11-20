#!/usr/bin/env python3
"""Generate or check Antigravity/Codex mirrors of the repository's Claude skills."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

SKILL_NAMES = ("skillcheck", "metacheck", "updateversion", "release")
SOURCE_ROOT = Path(".claude") / "skills"
MIRROR_ROOT = Path(".agents") / "skills"
BOM = b"\xef\xbb\xbf"


def read_source(path: Path) -> tuple[dict[str, str], str]:
    raw = path.read_bytes()
    if raw.startswith(BOM):
        raise ValueError("UTF-8 BOM is not allowed")
    text = raw.decode("utf-8")
    lines = text.splitlines(keepends=True)
    if not lines or lines[0].strip() != "---":
        raise ValueError("missing opening frontmatter delimiter")
    try:
        closing = next(index for index in range(1, len(lines)) if lines[index].strip() == "---")
    except StopIteration as exc:
        raise ValueError("missing closing frontmatter delimiter") from exc

    metadata: dict[str, str] = {}
    for line in lines[1:closing]:
        key, separator, value = line.partition(":")
        if separator and key.strip() in ("name", "description"):
            if key.strip() in metadata:
                raise ValueError(f"duplicate {key.strip()} field")
            metadata[key.strip()] = value.strip()
    for key in ("name", "description"):
        value = metadata.get(key, "")
        if not value or value in ("|", ">", "|-", ">-", "|+", ">+"):
            raise ValueError(f"{key} must be a non-empty single-line value")
    return metadata, "".join(lines[closing + 1:])


def render_mirror(metadata: dict[str, str], body: str) -> bytes:
    text = f"---\nname: {metadata['name']}\ndescription: {metadata['description']}\n---\n{body}"
    return text.encode("utf-8")


def expected_mirrors(repo: Path) -> tuple[dict[Path, bytes], list[str]]:
    expected: dict[Path, bytes] = {}
    errors: list[str] = []
    for name in SKILL_NAMES:
        source = repo / SOURCE_ROOT / name / "SKILL.md"
        mirror = repo / MIRROR_ROOT / name / "SKILL.md"
        try:
            metadata, body = read_source(source)
            if metadata["name"] != name:
                raise ValueError(f"name must be {name}")
            expected[mirror] = render_mirror(metadata, body)
        except (OSError, UnicodeError, ValueError) as exc:
            errors.append(f"{source.relative_to(repo).as_posix()}: {exc}")
    return expected, errors


def write_mirrors(expected: dict[Path, bytes]) -> int:
    for path, content in expected.items():
        if path.exists() and path.read_bytes() == content:
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        print(f"wrote {path.as_posix()}")
    return 0


def check_mirrors(repo: Path, expected: dict[Path, bytes], errors: list[str]) -> int:
    for path, content in expected.items():
        try:
            if path.read_bytes() != content:
                errors.append(f"{path.relative_to(repo).as_posix()}: missing or stale mirror; run with --write")
        except OSError:
            errors.append(f"{path.relative_to(repo).as_posix()}: missing mirror; run with --write")
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1
    print(f"{len(SKILL_NAMES)} project skills are in sync")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repo", nargs="?", default=".")
    parser.add_argument("--write", action="store_true", help="regenerate .agents/skills mirrors")
    args = parser.parse_args()
    repo = Path(args.repo).resolve()
    expected, errors = expected_mirrors(repo)
    if args.write:
        if errors:
            for error in errors:
                print(error, file=sys.stderr)
            return 1
        return write_mirrors(expected)
    return check_mirrors(repo, expected, errors)


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
