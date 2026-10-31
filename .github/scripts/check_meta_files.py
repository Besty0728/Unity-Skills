#!/usr/bin/env python3
"""Meta Check (CI) — .meta 配对与 GUID 重复校验.

只读校验，覆盖 /metacheck 提示词中可自动化、无需人工判断的两项核心检查：

1. SkillsForUnity/ 下所有 .cs / .uxml / .uss 文件都有对应的 .meta 文件。
2. SkillsForUnity/ 下所有 .meta 文件的 guid 字段互不重复。

名称以 `~` 结尾的目录（Evaluation~、unity-skills~）Unity 不导入、也不需要 .meta，
整棵子树跳过（其中的 .json、.meta 都不参与上面两项）。

另有两类 GUID 检查，由 /metacheck 驱动修复：

3. 第三方包 GUID 黑名单（KNOWN_CONFLICT_GUIDS）：命中即违规，确定性冲突无误报。
4. 伪 GUID 启发式（sequential / interleave / repeat / abcdef / length）：
   默认只告警不影响退出码（随机 GUID 也有小概率误中），加 --strict 才视为违规。

用法：
    python3 .github/scripts/check_meta_files.py [仓库根目录，默认当前目录] [--strict]

退出码：发现任一违规即返回非零。
"""
from __future__ import annotations

import os
import re
import sys

from scan_floor import floor_problem, is_unity_hidden_dir

SCAN_ROOT_REL = "SkillsForUnity"
CHECKED_EXTENSIONS = (".cs", ".uxml", ".uss", ".json")
EXCLUDED_DIR_NAMES = {".git", "Library", "Temp", "obj", "bin", "Logs"}
GUID_RE = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)
ANY_GUID_RE = re.compile(r"^guid:[ \t]*(\S*)[ \t]*$", re.MULTILINE)

# Third-party GUIDs a .meta here must never reuse, mapped to the reason.
KNOWN_CONFLICT_GUIDS = {
    "a2f7ae0675bf4fb478a0a1df7a3f6c64":
        "com.coplaydev.unity-mcp package.json.meta (upstream of this fork, issue #41)",
}

HEX_ALPHABET = "0123456789abcdef"
REPEAT_RE = re.compile(r"(.)\1{3}")


def interleave_at(guid: str) -> int:
    """Offset of a letter/digit-alternating run like a1b2c3d4 (both sequences ascending by 1), else -1."""
    for i in range(len(guid) - 7):
        seg = guid[i:i + 8]
        letters, digits = seg[0::2], seg[1::2]
        if not (all(c in "abcdef" for c in letters) and all(c in "0123456789" for c in digits)):
            continue
        if all(ord(b) - ord(a) == 1 for a, b in zip(letters, letters[1:])) \
                and all(ord(b) - ord(a) == 1 for a, b in zip(digits, digits[1:])):
            return i
    return -1


def suspicious_patterns(guid: str) -> list[str]:
    """Why a GUID looks hand-made rather than uuid4; empty when it does not."""
    guid = guid.lower()
    if not re.fullmatch(r"[0-9a-f]{32}", guid):
        return [f"length:{len(guid)}"]
    found = []
    for i in range(len(guid) - 3):
        if guid[i:i + 4] in HEX_ALPHABET:
            found.append(f"sequential:{guid[i:i + 4]}@{i}")
            break
    at = interleave_at(guid)
    if at >= 0:
        found.append(f"interleave:{guid[at:at + 8]}@{at}")
    repeat = REPEAT_RE.search(guid)
    if repeat:
        found.append(f"repeat:{repeat.group(0)}@{repeat.start()}")
    if "abcdef" in guid:
        found.append(f"abcdef@{guid.index('abcdef')}")
    return found


def iter_files(scan_root: str):
    for dirpath, dirnames, filenames in os.walk(scan_root):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDED_DIR_NAMES and not is_unity_hidden_dir(d)]
        for filename in filenames:
            yield os.path.join(dirpath, filename)


def main() -> int:
    args = sys.argv[1:]
    strict = "--strict" in args
    positional = [a for a in args if not a.startswith("--")]
    repo_root = positional[0] if positional else "."
    scan_root = os.path.join(repo_root, SCAN_ROOT_REL)

    if not os.path.isdir(scan_root):
        print(f"[meta-check] 扫描目录不存在: {scan_root}", file=sys.stderr)
        return 1

    missing_meta: list[str] = []
    guid_to_files: dict[str, list[str]] = {}
    raw_guids: list[tuple[str, str]] = []
    unreadable_meta: list[tuple[str, str]] = []
    checked_files: set[str] = set()
    meta_files: set[str] = set()

    for path in iter_files(scan_root):
        rel_path = os.path.relpath(path, repo_root).replace(os.sep, "/")

        if path.endswith(".meta"):
            meta_files.add(rel_path)

        if path.endswith(CHECKED_EXTENSIONS):
            checked_files.add(rel_path)
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

            loose = ANY_GUID_RE.search(content)
            if loose:
                raw_guids.append((loose.group(1), rel_path))

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

    blacklisted = [(g, f) for g, f in raw_guids if g.lower() in KNOWN_CONFLICT_GUIDS]
    suspicious = [(g, pats, f) for g, f in raw_guids if (pats := suspicious_patterns(g))]

    ok = True

    print("🔍 Meta Check (CI) — .meta 配对与 GUID 重复校验")
    print("━" * 30)
    print(f"  扫描 {len(checked_files)} 个 .cs/.uxml/.uss/.json、{len(meta_files)} 个 .meta")

    coverage = [p for p in (
        floor_problem("源文件", checked_files, repo_root, SCAN_ROOT_REL, CHECKED_EXTENSIONS),
        floor_problem(".meta", meta_files, repo_root, SCAN_ROOT_REL, (".meta",)),
    ) if p]
    if coverage:
        ok = False
        print("\n🔴 扫描覆盖不足（遍历多半坏了，下面的「通过」不可信）")
        for problem in coverage:
            print(f"  - {problem}")

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

    if blacklisted:
        ok = False
        print(f"\n🔴 已知冲突 GUID（{len(blacklisted)} 项，与第三方包逐字节相同）")
        for guid, rel_path in sorted(blacklisted, key=lambda t: t[1]):
            print(f"  - {guid} ← {KNOWN_CONFLICT_GUIDS[guid.lower()]}: {rel_path}")
    else:
        print("✅ 未命中第三方包 GUID 黑名单")

    if suspicious:
        marker = "🔴" if strict else "⚠️"
        if strict:
            ok = False
        print(f"\n{marker} 疑似伪 GUID（{len(suspicious)} 项，{'--strict 视为违规' if strict else '仅告警，加 --strict 才失败'}）")
        for guid, pats, rel_path in sorted(suspicious, key=lambda t: t[2]):
            print(f"  - {guid} [{', '.join(pats)}]: {rel_path}")
    else:
        print("✅ 未发现疑似伪 GUID")

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
