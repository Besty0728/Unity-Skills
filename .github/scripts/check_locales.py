#!/usr/bin/env python3
"""Locale Check (CI) — 多语言 JSON 资产校验与三语对齐.

检查项：
1. 校验 SkillsForUnity/Editor/Locales/ 下 en.json, zh-CN.json, ru.json 格式合法。
2. 校验 EN, CN, RU 三种语言在键名集合上保持 100% 对齐（无缺失键）。
3. 校验 SkillsForUnity/Editor/ 下所有 C# 代码中调用的 SkillsLocalization.Get("key") 均存在于字典中。

用法：
    python3 .github/scripts/check_locales.py [仓库根目录，默认当前目录]
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys

LOCALES_DIR_REL = os.path.join("SkillsForUnity", "Editor", "Locales")
REQUIRED_FILES = ("en.json", "zh-CN.json", "ru.json")
GET_KEY_RE = re.compile(r'SkillsLocalization\.Get\(\s*"([^"]+)"\s*\)')


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
    cs_missing_keys: list[tuple[str, str]] = []
    for cs_file in glob.glob(editor_cs_glob, recursive=True):
        try:
            with open(cs_file, "r", encoding="utf-8") as f:
                content = f.read()
            for m in GET_KEY_RE.finditer(content):
                key = m.group(1)
                if key not in all_keys:
                    rel_cs = os.path.relpath(cs_file, repo_root).replace(os.sep, "/")
                    cs_missing_keys.append((key, rel_cs))
        except Exception as ex:
            errors.append(f"读取 C# 文件 {cs_file} 失败: {ex}")

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
