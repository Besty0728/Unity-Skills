#!/usr/bin/env python3
"""SKILL.md Frontmatter Compliance Check (CI).

背景：Codex / Claude 等原生 skill 发现器把每个含 SKILL.md 的子目录当成一个独立
skill 注册，discovery 阶段读取 frontmatter `description` 做触发匹配。
`description` 超过 1024 字符会被直接拒绝加载（"Skipped loading N skill(s) due
to invalid SKILL.md files"）。v2.3.0 发布曾因超限 skill 被发现器拒载而翻车，
此前完全靠人工用 /skillcheck 步骤 3k 复核。本脚本把其中可自动化的硬性部分
落成 CI 门槛。

校验范围：
  - SkillsForUnity/unity-skills~/SKILL.md（顶层入口）
  - SkillsForUnity/unity-skills~/skills/*/SKILL.md（各模块，REST + advisory）

校验规则（任一违规即失败）：
  1. description 长度 ≤ 1024 字符（按字符数，非字节数，含中文）
  2. name 长度 ≤ 64 字符
  3. YAML frontmatter 结构合法：以 `---` 开头并正确闭合；`name` 与
     `description` 两个必填键存在且非空
  4. 文件开头无 UTF-8 BOM（EF BB BF）

用法：
    python3 .github/scripts/check_skill_frontmatter.py [仓库根目录，默认当前目录]
"""
from __future__ import annotations

import glob
import os
import sys

try:
    import yaml
except ImportError:  # pragma: no cover
    print("[frontmatter-check] 需要 PyYAML（pip install pyyaml）", file=sys.stderr)
    raise

MAX_DESCRIPTION_CHARS = 1024
MAX_NAME_CHARS = 64
BOM = b"\xef\xbb\xbf"
DISCOVERY_SOFT_BUDGET = 8000


def find_skill_md_paths(repo_root: str) -> list[str]:
    base = os.path.join(repo_root, "SkillsForUnity", "unity-skills~")
    paths = []

    top_level = os.path.join(base, "SKILL.md")
    if os.path.isfile(top_level):
        paths.append(top_level)

    paths.extend(sorted(glob.glob(os.path.join(base, "skills", "*", "SKILL.md"))))
    return paths


def parse_frontmatter(raw_bytes: bytes) -> tuple[bool, dict | None, str | None]:
    """返回 (结构合法, 解析出的 dict 或 None, 错误信息或 None)。"""
    text = raw_bytes.decode("utf-8", errors="replace")
    if not text.startswith("---"):
        return False, None, "文件未以 `---` 开头，frontmatter 缺失"

    # 跳过起始 `---` 行，找下一个独占一行的 `---` 作为闭合标记
    lines = text.split("\n")
    if lines[0].strip() != "---":
        return False, None, "首行不是独立的 `---`"

    closing_index = None
    for i in range(1, len(lines)):
        if lines[i].strip() == "---":
            closing_index = i
            break

    if closing_index is None:
        return False, None, "`---` frontmatter 未正确闭合"

    yaml_block = "\n".join(lines[1:closing_index])
    try:
        data = yaml.safe_load(yaml_block)
    except yaml.YAMLError as exc:
        return False, None, f"YAML 解析失败: {exc}"

    if not isinstance(data, dict):
        return False, None, "frontmatter 内容不是合法的 YAML 映射"

    return True, data, None


def main() -> int:
    repo_root = sys.argv[1] if len(sys.argv) > 1 else "."
    skill_md_paths = find_skill_md_paths(repo_root)

    if not skill_md_paths:
        print("[frontmatter-check] 未找到任何 SKILL.md，检查扫描路径是否正确", file=sys.stderr)
        return 1

    violations: list[str] = []
    total_description_chars = 0
    longest = ("", 0)

    for path in skill_md_paths:
        rel_path = os.path.relpath(path, repo_root).replace(os.sep, "/")

        with open(path, "rb") as f:
            raw_bytes = f.read()

        if raw_bytes.startswith(BOM):
            violations.append(f"{rel_path}: 文件开头含 UTF-8 BOM（EF BB BF），应存为 UTF-8 无 BOM")
            raw_bytes = raw_bytes[len(BOM):]

        structurally_ok, data, err = parse_frontmatter(raw_bytes)
        if not structurally_ok:
            violations.append(f"{rel_path}: frontmatter 结构非法 — {err}")
            continue

        name = data.get("name")
        description = data.get("description")

        if not name or not str(name).strip():
            violations.append(f"{rel_path}: frontmatter 缺少必填键 `name` 或为空")
        else:
            name_len = len(str(name))
            if name_len > MAX_NAME_CHARS:
                violations.append(f"{rel_path}: name 长度 {name_len} 字符 > {MAX_NAME_CHARS}")

        if not description or not str(description).strip():
            violations.append(f"{rel_path}: frontmatter 缺少必填键 `description` 或为空")
        else:
            desc_len = len(str(description))
            total_description_chars += desc_len
            if desc_len > longest[1]:
                longest = (rel_path, desc_len)
            if desc_len > MAX_DESCRIPTION_CHARS:
                violations.append(
                    f"{rel_path}: description 长度 {desc_len} 字符 > {MAX_DESCRIPTION_CHARS}"
                    "，会被 Codex/Claude 发现器拒载"
                )

    print("🔍 SKILL.md Frontmatter Compliance Check (CI)")
    print("━" * 30)
    print(f"扫描文件数：{len(skill_md_paths)}")
    print(f"description 总字符数：{total_description_chars}（发现器软预算 ~{DISCOVERY_SOFT_BUDGET}）")
    if longest[0]:
        print(f"最长 description：{longest[0]}（{longest[1]} 字符）")

    if violations:
        print(f"\n🔴 发现 {len(violations)} 项违规")
        for v in violations:
            print(f"  - {v}")
        print("\n" + "━" * 30)
        print(f"❌ 未通过：{len(violations)} 项违规")
        return 1

    print("\n" + "━" * 30)
    print("✅ 通过：0 项超限，0 BOM，frontmatter 结构全部合法")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
