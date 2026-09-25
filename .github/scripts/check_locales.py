#!/usr/bin/env python3
"""Locale Check (CI) — 多语言 JSON 资产校验与三语对齐.

检查项：
1. 校验 SkillsForUnity/Editor/Locales/ 下 en.json, zh-CN.json, ru.json 格式合法。
2. 校验 EN, CN, RU 三种语言在键名集合上保持 100% 对齐（无缺失键）。
3. 校验 SkillsForUnity/Editor/ 下所有 C# 代码中调用的 SkillsLocalization.Get/TryGet/Has 键均存在于字典中，
   覆盖直接字面量、`Get("key", args...)` 格式化重载、以及三元表达式内联字面量（如
   `Get(cond ? "k1" : "k2")`）；变量/表达式构造的动态键（如 Get(cmd.LocKey)）无法静态解析，跳过。

用法：
    python3 .github/scripts/check_locales.py [仓库根目录，默认当前目录]
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys

from scan_floor import floor_problem

LOCALES_DIR_REL = os.path.join("SkillsForUnity", "Editor", "Locales")
REQUIRED_FILES = ("en.json", "zh-CN.json", "ru.json")

# Every SkillsLocalization accessor that takes a key as its first argument.
# TryGet/Has have no call sites today, but are covered so a future call site
# is checked without needing to touch this script again.
CALL_RE = re.compile(r"SkillsLocalization\.(?:Get|TryGet|Has)\(")
# Locale keys are exclusively [a-z0-9_] (verified against en.json); a quoted
# literal that fully matches this charset inside a call's argument list is
# treated as a referenced key. This also catches literals in the formatted
# overload (`Get("key", args...)`) and in ternaries (`Get(cond ? "a" : "b")`),
# both of which the previous single-shot regex missed because it required the
# closing `)` immediately after the string.
KEY_LITERAL_RE = re.compile(r'"([a-z0-9_]+)"')


def _extract_call_args(content: str, start: int) -> str:
    """Return the raw text between the '(' at content[start-1] and its
    matching ')', tracking paren depth while skipping over the contents of
    string literals (so a stray '(' / ')' inside a string cannot desync the
    depth counter).
    """
    depth = 1
    i = start
    n = len(content)
    while i < n and depth > 0:
        ch = content[i]
        if ch == '"':
            i += 1
            while i < n and content[i] != '"':
                if content[i] == "\\":
                    i += 1
                i += 1
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        i += 1
    return content[start : i - 1]


def find_key_references(content: str) -> list[str]:
    """Collect every locale key literal referenced via SkillsLocalization.
    Get/TryGet/Has in a C# source string. Keys built from a variable or
    expression (e.g. Get(cmd.LocKey), Get(skill.Name)) cannot be resolved
    statically and are silently skipped — those call sites rely on runtime
    coverage (tests / manual QA), not this check.
    """
    keys: list[str] = []
    for m in CALL_RE.finditer(content):
        arg_text = _extract_call_args(content, m.end())
        keys.extend(KEY_LITERAL_RE.findall(arg_text))
    return keys


def main() -> int:
    repo_root = sys.argv[1] if len(sys.argv) > 1 else "."
    locales_dir = os.path.join(repo_root, LOCALES_DIR_REL)

    if not os.path.isdir(locales_dir):
        print(f"[locale-check] ❌ 多语言目录不存在: {locales_dir}", file=sys.stderr)
        return 1

    print("🔍 Locale Check (CI) — 多语言 JSON 与三语对齐校验")
    print("━" * 40)

    dicts: dict[str, dict[str, str]] = {}
    for filename in REQUIRED_FILES:
        filepath = os.path.join(locales_dir, filename)
        if not os.path.isfile(filepath):
            print(f"❌ 缺少必要的多语言文件: {filename}", file=sys.stderr)
            return 1
        try:
            with open(filepath, "r", encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                print(f"❌ 多语言文件根节点必须为 Object: {filename}", file=sys.stderr)
                return 1
            dicts[filename] = data
            print(f"  ✓ {filename}: 包含 {len(data)} 个词条")
        except Exception as ex:
            print(f"❌ 解析 {filename} 失败: {ex}", file=sys.stderr)
            return 1

    # 1. Parity Check
    en_keys = set(dicts["en.json"].keys())
    cn_keys = set(dicts["zh-CN.json"].keys())
    ru_keys = set(dicts["ru.json"].keys())
    all_keys = en_keys | cn_keys | ru_keys

    errors: list[str] = []
    missing_en = all_keys - en_keys
    missing_cn = all_keys - cn_keys
    missing_ru = all_keys - ru_keys

    if missing_en:
        errors.append(f"en.json 缺失键 ({len(missing_en)}): {sorted(missing_en)[:10]}")
    if missing_cn:
        errors.append(f"zh-CN.json 缺失键 ({len(missing_cn)}): {sorted(missing_cn)[:10]}")
    if missing_ru:
        errors.append(f"ru.json 缺失键 ({len(missing_ru)}): {sorted(missing_ru)[:10]}")

    # 2. C# Calls Check
    editor_cs_glob = os.path.join(repo_root, "SkillsForUnity", "Editor", "**", "*.cs")
    cs_files = glob.glob(editor_cs_glob, recursive=True)
    cs_missing_keys: list[tuple[str, str]] = []
    total_refs = 0
    for cs_file in cs_files:
        try:
            with open(cs_file, "r", encoding="utf-8") as f:
                content = f.read()
            rel_cs = os.path.relpath(cs_file, repo_root).replace(os.sep, "/")
            for key in find_key_references(content):
                total_refs += 1
                if key not in all_keys:
                    cs_missing_keys.append((key, rel_cs))
        except Exception as ex:
            errors.append(f"读取 C# 文件 {cs_file} 失败: {ex}")

    print(f"  ✓ 扫描 {len(cs_files)} 个 C# 文件（含 Editor/UI/Controllers/），发现 {total_refs} 处 SkillsLocalization.Get/TryGet/Has 键引用")

    scanned = {os.path.relpath(p, repo_root).replace(os.sep, "/") for p in cs_files}
    coverage = floor_problem("C# 文件", scanned, repo_root, os.path.join("SkillsForUnity", "Editor"), (".cs",))
    if coverage:
        errors.append(f"扫描覆盖不足（遍历多半坏了）: {coverage}")
    if total_refs == 0:
        errors.append("没有找到任何 SkillsLocalization 键引用 —— 调用匹配多半坏了，悬挂键检查形同虚设")
    if not all_keys:
        errors.append("三个词典都是空的")

    if cs_missing_keys:
        for k, f in cs_missing_keys:
            errors.append(f"C# 代码引用了未定义的多语言键: \"{k}\" (在 {f})")

    print("━" * 40)
    if errors:
        print(f"❌ 校验失败，发现 {len(errors)} 个问题：", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1

    print(f"✅ 通过：三语 100% 对齐，{len(all_keys)} 个词条完整无缺失，C# 调用无悬挂键。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
