#!/usr/bin/env python3
"""Coverage floor shared by the CI check scripts.

A check that walks the tree and finds nothing prints the same "passed" line as a check that found no problem.
Each script therefore compares how many files it actually scanned with how many git tracks for the same scope;
fewer means its walk or glob broke (a renamed directory, a wrong pattern, a partial checkout) and the green result
would mean nothing.
"""
from __future__ import annotations

import os
import subprocess


def is_unity_hidden_dir(name: str) -> bool:
    """Unity never imports a folder whose name ends in '~' (Evaluation~, unity-skills~), so nothing in it needs a .meta."""
    return name.endswith("~")


def tracked_files(repo_root: str, rel_dir: str, suffixes: tuple[str, ...]) -> set[str] | None:
    """Existing files under rel_dir that git tracks, as repo-relative '/' paths; None when git can't answer.
    Files below a '~' folder are left out, the same way the scanners skip those folders."""
    try:
        out = subprocess.run(
            ["git", "-C", repo_root, "ls-files", "-z", "--", rel_dir],
            capture_output=True, check=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    base = rel_dir.replace("\\", "/").strip("/")
    result = set()
    for raw in out.split(b"\0"):
        if not raw:
            continue
        rel = raw.decode("utf-8", errors="replace")
        below = rel[len(base):] if rel.startswith(base) else rel
        if any(is_unity_hidden_dir(part) for part in below.split("/")[:-1]):
            continue
        if rel.endswith(suffixes) and os.path.isfile(os.path.join(repo_root, rel)):
            result.add(rel)
    return result


def floor_problem(label: str, scanned: set[str], repo_root: str, rel_dir: str, suffixes: tuple[str, ...]) -> str | None:
    """Why the scan does not cover its scope, or None when it does."""
    if not scanned:
        return f"{label}: scanned 0 files under {rel_dir}"
    tracked = tracked_files(repo_root, rel_dir, suffixes)
    if tracked is None:
        return None
    missed = sorted(tracked - scanned)
    if missed:
        return f"{label}: scanned {len(scanned)} files but git tracks {len(tracked)}; missed e.g. {missed[:3]}"
    return None


# Producer:Betsy
