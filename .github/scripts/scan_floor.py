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


def tracked_files(repo_root: str, rel_dir: str, suffixes: tuple[str, ...]) -> set[str] | None:
    """Existing files under rel_dir that git tracks, as repo-relative '/' paths; None when git can't answer."""
    try:
        out = subprocess.run(
            ["git", "-C", repo_root, "ls-files", "-z", "--", rel_dir],
            capture_output=True, check=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    result = set()
    for raw in out.split(b"\0"):
        if not raw:
            continue
        rel = raw.decode("utf-8", errors="replace")
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
