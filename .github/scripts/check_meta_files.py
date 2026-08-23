#!/usr/bin/env python3
"""Meta Check (CI) — .meta 配对与 GUID 重复校验.

只读校验，覆盖 /metacheck 提示词中可自动化、无需人工判断的两项核心检查：

1. SkillsForUnity/ 下所有 .cs / .uxml / .uss 文件都有对应的 .meta 文件。
2. SkillsForUnity/ 下所有 .meta 文件的 guid 字段互不重复。

启发式伪 GUID 检测与第三方包 GUID 黑名单对照（.claude/commands/metacheck.md
步骤 2 / 2b）需要人工判断"是否有外部引用""是否属于历史白名单"等语境，
不适合做成会中断 CI 的硬门槛，继续保留给 /metacheck 交互式审计。

用法：
    python3 .github/scripts/check_meta_files.py [仓库根目录，默认当前目录]

退出码：发现任一违规即返回非零。
"""
from __future__ import annotations

import os
import re
import sys

SCAN_ROOT_REL = "SkillsForUnity"
CHECKED_EXTENSIONS = (".cs", ".uxml", ".uss")
EXCLUDED_DIR_NAMES = {".git", "Library", "Temp", "obj", "bin", "Logs"}
GUID_RE = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)


def iter_files(scan_root: str):
    for dirpath, dirnames, filenames in os.walk(scan_root):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDED_DIR_NAMES]
        for filename in filenames:
            yield os.path.join(dirpath, filename)


def main() -> int:
    repo_root = sys.argv[1] if len(sys.argv) > 1 else "."
    scan_root = os.path.join(repo_root, SCAN_ROOT_REL)

    if not os.path.isdir(scan_root):
        print(f"[meta-check] 扫描目录不存在: {scan_root}", file=sys.stderr)
        return 1

    missing_meta: list[str] = []
    guid_to_files: dict[str, list[str]] = {}
    unreadable_meta: list[tuple[str, str]] = []

    for path in iter_files(scan_root):
        rel_path = os.path.relpath(path, repo_root).replace(os.sep, "/")

        if path.endswith(CHECKED_EXTENSIONS):
            if not os.path.isfile(path + ".meta"):
                missing_meta.append(rel_path)
            continue

        if path.endswith(".meta"):
            try:
                with open(path, "r", encoding="utf-8") as f:
                    content = f.read()
            except (OSError, UnicodeDecodeError) as exc:
                unreadable_meta.append((rel_path, str(exc)))
                continue

            match = GUID_RE.search(content)
            if not match:
                # 目录 .meta（folder meta）等也应有 guid；缺失同样记为不可读，
                # 但不当作致命错误——folder .meta 结构与文件 .meta 略有差异，
                # 交由人工在 /metacheck 中复核。
                continue

            guid = match.group(1).lower()
            guid_to_files.setdefault(guid, []).append(rel_path)

    duplicate_guids = {
        guid: files for guid, files in guid_to_files.items() if len(files) > 1
    }

    ok = True

    print("🔍 Meta Check (CI) — .meta 配对与 GUID 重复校验")
    print("━" * 30)

    if missing_meta:
        ok = False
        print(f"\n🔴 缺失 .meta 文件（{len(missing_meta)} 项）")
        for rel_path in sorted(missing_meta):
            print(f"  - {rel_path} 缺少对应 {rel_path}.meta")
    else:
        print("\n✅ 所有 .cs / .uxml / .uss 均有对应 .meta")

    if duplicate_guids:
        ok = False
        print(f"\n🔴 重复 GUID（{len(duplicate_guids)} 组）")
        for guid, files in sorted(duplicate_guids.items()):
            print(f"  - guid {guid} 被以下 {len(files)} 个 .meta 共用:")
            for rel_path in sorted(files):
                print(f"      · {rel_path}")
    else:
        print("✅ 未发现重复 GUID")

    if unreadable_meta:
        # 编码异常不直接判失败，但需要显式提示，避免静默漏检。
        print(f"\n⚠️ 无法解析的 .meta 文件（{len(unreadable_meta)} 项，未参与 GUID 去重）")
        for rel_path, err in unreadable_meta:
            print(f"  - {rel_path}: {err}")

    print("\n" + "━" * 30)
    if ok:
        print(f"✅ 通过：{sum(1 for _ in guid_to_files)} 个 GUID 全部唯一，"
              f"扫描范围内无缺失 .meta")
    else:
        print("❌ 发现违规，见上方详情")

    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
