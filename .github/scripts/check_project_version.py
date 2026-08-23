#!/usr/bin/env python3
"""Validate UnitySkills project-version anchors and an optional release tag.

The editor update banner compares ``SkillsLogger.Version`` with GitHub's latest
stable Release. A release is therefore safe only when every user-facing package
version anchor agrees with that runtime version and a release tag uses the exact
``vMAJOR.MINOR.PATCH`` form.

Usage:
    python3 .github/scripts/check_project_version.py [repo-root]
    python3 .github/scripts/check_project_version.py . --expected 2.5.0
    python3 .github/scripts/check_project_version.py . --tag v2.5.0
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path


SEMVER_PATTERN = r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)"
SEMVER_RE = re.compile(rf"^{SEMVER_PATTERN}$")
RELEASE_TAG_RE = re.compile(rf"^v({SEMVER_PATTERN})$")


def read_text(repo_root: Path, relative_path: str) -> str:
    path = repo_root / relative_path
    try:
        return path.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise ValueError(f"无法读取 {relative_path}: {exc}") from exc


def extract_single(
    repo_root: Path,
    relative_path: str,
    pattern: str,
    label: str,
) -> str:
    text = read_text(repo_root, relative_path)
    matches = re.findall(pattern, text, flags=re.MULTILINE)
    if len(matches) != 1:
        raise ValueError(
            f"{relative_path}: 预期恰好 1 个 {label}，实际找到 {len(matches)} 个"
        )
    return matches[0].strip()


def collect_versions(repo_root: Path) -> dict[str, str]:
    package_path = repo_root / "SkillsForUnity/package.json"
    try:
        package_data = json.loads(package_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"无法解析 SkillsForUnity/package.json: {exc}") from exc

    package_version = package_data.get("version")
    if not isinstance(package_version, str) or not package_version.strip():
        raise ValueError("SkillsForUnity/package.json: `version` 缺失或不是字符串")

    changelog = read_text(repo_root, "CHANGELOG.md")
    changelog_match = re.search(
        rf"^## \[({SEMVER_PATTERN})\]\s+-\s+\d{{4}}-\d{{2}}-\d{{2}}\s*$",
        changelog,
        flags=re.MULTILINE,
    )
    if not changelog_match:
        raise ValueError("CHANGELOG.md: 未找到合法的顶部版本标题")

    return {
        "SkillsLogger.Version": extract_single(
            repo_root,
            "SkillsForUnity/Editor/Skills/SkillsLogger.cs",
            rf'^\s*public\s+const\s+string\s+Version\s*=\s*"({SEMVER_PATTERN})"\s*;',
            "SkillsLogger.Version",
        ),
        "package.json.version": package_version.strip(),
        "unity_skills.py.__version__": extract_single(
            repo_root,
            "SkillsForUnity/unity-skills~/scripts/unity_skills.py",
            rf'^__version__\s*=\s*"({SEMVER_PATTERN})"\s*$',
            "Python helper 版本",
        ),
        "agent.md current version": extract_single(
            repo_root,
            "agent.md",
            rf"^\|\s*版本\s*\|\s*({SEMVER_PATTERN})\s*\|\s*$",
            "项目版本表格行",
        ),
        # README 的"当前版本"标记已于 2.7.0 移除（README 不再承载版本锚点），勿回加。
        "CHANGELOG.md latest entry": changelog_match.group(1),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repo_root", nargs="?", default=".")
    parser.add_argument(
        "--expected",
        help="要求所有版本锚点都等于这个 MAJOR.MINOR.PATCH 版本",
    )
    parser.add_argument(
        "--tag",
        help="同时校验发布 tag；必须严格为 vMAJOR.MINOR.PATCH 并与项目版本一致",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_root = Path(args.repo_root).resolve()

    try:
        versions = collect_versions(repo_root)
    except ValueError as exc:
        print(f"❌ 项目版本检查失败：{exc}", file=sys.stderr)
        return 1

    errors: list[str] = []
    for label, version in versions.items():
        if not SEMVER_RE.fullmatch(version):
            errors.append(f"{label} 不是严格的 MAJOR.MINOR.PATCH：{version!r}")

    canonical_version = versions["SkillsLogger.Version"]
    for label, version in versions.items():
        if version != canonical_version:
            errors.append(
                f"{label}={version}，与 SkillsLogger.Version={canonical_version} 不一致"
            )

    if args.expected:
        if not SEMVER_RE.fullmatch(args.expected):
            errors.append(f"--expected 不是严格的 MAJOR.MINOR.PATCH：{args.expected!r}")
        elif canonical_version != args.expected:
            errors.append(
                f"项目版本为 {canonical_version}，不等于期望版本 {args.expected}"
            )

    if args.tag:
        tag_match = RELEASE_TAG_RE.fullmatch(args.tag)
        if not tag_match:
            errors.append(f"发布 tag 必须严格为 vMAJOR.MINOR.PATCH：{args.tag!r}")
        elif tag_match.group(1) != canonical_version:
            errors.append(
                f"发布 tag {args.tag} 与项目版本 {canonical_version} 不一致"
            )

    print("🔍 UnitySkills Project Version Check")
    print("━" * 36)
    for label, version in versions.items():
        print(f"  {label}: {version}")
    if args.tag:
        print(f"  release tag: {args.tag}")

    if errors:
        print(f"\n🔴 发现 {len(errors)} 项版本违规")
        for error in errors:
            print(f"  - {error}")
        return 1

    print(f"\n✅ 所有版本锚点一致：{canonical_version}")
    if args.tag:
        print(f"✅ 发布 tag 与项目版本一致：{args.tag}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
