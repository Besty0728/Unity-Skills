#!/usr/bin/env python3
"""SKILL.md Frontmatter Compliance Check (CI).

背景：Codex / Claude 等原生 skill 发现器把每个名为 SKILL.md 的文件当成一个独立
skill 注册，discovery 阶段读取 frontmatter `description` 做触发匹配。
`description` 超过 1024 字符会被直接拒绝加载（"Skipped loading N skill(s) due
to invalid SKILL.md files"）。v2.3.0 发布曾因超限 skill 被发现器拒载而翻车，
本脚本把其中可自动化的硬性部分落成 CI 门槛；/skillcheck 经 skill_inventory.py
调用它，不另写一套。

校验范围：
  - SkillsForUnity/unity-skills~/SKILL.md（唯一的顶层入口，唯一会被注册的文档）

校验规则（任一违规即失败）：
  1. description 长度 ≤ 1024 字符（按字符数，非字节数，含中文）
  2. name 长度 ≤ 64 字符
  3. YAML frontmatter 结构合法：以 `---` 开头并正确闭合；`name` 与
     `description` 两个必填键存在且非空
  4. 文件开头无 UTF-8 BOM（EF BB BF）
  5. SkillsForUnity/unity-skills~/skills/ 下（任意深度）不得有名为 SKILL.md 的文件：
     模块入口应叫 MODULE.md，索引应叫 INDEX.md，否则发现器会把它注册成独立 skill

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
PACKAGE_DIR = os.path.join("SkillsForUnity", "unity-skills~")
ROOT_DOC = "SKILL.md"


def find_misnamed_skill_docs(repo_root: str) -> list[str]:
    """skills/ 下任意深度的 SKILL.md；只有根 SKILL.md 允许存在。"""
    pattern = os.path.join(repo_root, PACKAGE_DIR, "skills", "**", ROOT_DOC)
    return sorted(glob.glob(pattern, recursive=True))


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


def check_root_doc(path: str, rel_path: str) -> tuple[list[str], int]:
    """返回 (违规列表, description 字符数)。"""
    violations: list[str] = []

    with open(path, "rb") as f:
        raw_bytes = f.read()

    if raw_bytes.startswith(BOM):
        violations.append(f"{rel_path}: 文件开头含 UTF-8 BOM（EF BB BF），应存为 UTF-8 无 BOM")
        raw_bytes = raw_bytes[len(BOM):]

    structurally_ok, data, err = parse_frontmatter(raw_bytes)
    if not structurally_ok:
        return violations + [f"{rel_path}: frontmatter 结构非法 — {err}"], 0

    name = data.get("name")
    description = data.get("description")

    if not name or not str(name).strip():
        violations.append(f"{rel_path}: frontmatter 缺少必填键 `name` 或为空")
    elif len(str(name)) > MAX_NAME_CHARS:
        violations.append(f"{rel_path}: name 长度 {len(str(name))} 字符 > {MAX_NAME_CHARS}")

    if not description or not str(description).strip():
        violations.append(f"{rel_path}: frontmatter 缺少必填键 `description` 或为空")
        return violations, 0

    desc_len = len(str(description))
    if desc_len > MAX_DESCRIPTION_CHARS:
        violations.append(
            f"{rel_path}: description 长度 {desc_len} 字符 > {MAX_DESCRIPTION_CHARS}"
            "，会被 Codex/Claude 发现器拒载"
        )
    return violations, desc_len


def main() -> int:
    repo_root = sys.argv[1] if len(sys.argv) > 1 else "."
    root_doc = os.path.join(repo_root, PACKAGE_DIR, ROOT_DOC)

    if not os.path.isfile(root_doc):
        print("[frontmatter-check] 未找到根 SKILL.md，检查扫描路径是否正确", file=sys.stderr)
        return 1

    rel_root = os.path.relpath(root_doc, repo_root).replace(os.sep, "/")
    violations, desc_len = check_root_doc(root_doc, rel_root)
    for path in find_misnamed_skill_docs(repo_root):
        rel_path = os.path.relpath(path, repo_root).replace(os.sep, "/")
        violations.append(
            f"{rel_path}: skills/ 下不得有 SKILL.md（发现器会注册为独立 skill）；"
            "模块入口改名 MODULE.md，索引改名 INDEX.md"
        )

    print("🔍 SKILL.md Frontmatter Compliance Check (CI)")
    print("━" * 30)
    print(f"根文档：{rel_root}")
    print(f"description: {desc_len} 字符（上限 {MAX_DESCRIPTION_CHARS}）")

    if violations:
        print(f"\n🔴 发现 {len(violations)} 项违规")
        for v in violations:
            print(f"  - {v}")
        print("\n" + "━" * 30)
        print(f"❌ 未通过：{len(violations)} 项违规")
        return 1

    print("\n" + "━" * 30)
    print("✅ 通过：0 项超限，0 BOM，frontmatter 结构全部合法，skills/ 下无误名 SKILL.md")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
