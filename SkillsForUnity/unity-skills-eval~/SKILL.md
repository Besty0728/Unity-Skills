---
name: unity-skills-eval
description: Run the UnitySkills evaluation set (a scored benchmark of tasks) on the user's own Unity project. Ask first for scope, track, tier and prompt language, then start a session with the bundled runner and complete its tasks one by one. Use only when the user explicitly asks to run, resume or continue the UnitySkills evaluation or benchmark, types /unity-skills-eval, or pastes an evaluation session prompt. Never use it for ordinary Unity work, Unity questions, or testing the user's own game. 仅当用户明确要求运行或继续 UnitySkills 评测集（跑测评集、评测集测试、UnitySkills 评测），输入 /unity-skills-eval，或贴出评测会话提示时使用；日常 Unity 开发、Unity 问答、测试用户自己的游戏一律不要使用。
---

# UnitySkills Evaluation

This skill drives the evaluation runner that ships with the UnitySkills package. Throughout this file, `<eval-runner>` means `<skills-dir>/unity-skills/scripts/unity_eval.py`, where `<skills-dir>` is the parent of the folder holding this SKILL.md; resolve it to an absolute path before running anything. Commands run from the Unity project folder, so never use a relative path. Each task temporarily opens a run scene and adds assets under `Assets/__UnitySkillsEval/`; after the submit the runner restores the user's scenes and deletes them. Scores appear in Unity (UnitySkills window, Evaluation tab, which also shows progress for sessions started this way), never in the chat.

## Ask first

Before starting, ask the user in one message:

1. Scope: one track and tier (default), all tasks, or picked tasks (task IDs).
2. Track: `ops` or `dev` (skip when scope is all tasks).
3. Tier: `basic`, `standard` or `challenge` (skip when scope is all tasks).
4. Prompt language: `zh-CN`, `en` or `ru`.

## Start

- One track and tier: `python3 -B <eval-runner> start --track <track> --tier <tier> --lang <lang>`. For picked tasks add `--tasks ID[,ID...]`; all of them must belong to the same track and tier.
- All tasks: run six sessions one after another: ops basic, ops standard, ops challenge, dev basic, dev standard, dev challenge. Start the next only after the previous session has finished, because the runner refuses a second active session.
- Run from the Unity project folder, or add `--project <path to the project>`. Python 3.9 or newer; use `python` or `py -3` where `python3` is missing.
- Optional: `--no-usage` (see Rules).
- stdout is one JSON line: `ok`, `command`, `data`, `problems`, `next`.

## Run

1. When `problems` is not empty, give the user each `fix` text verbatim and stop. Do not repair the environment yourself: no mode switching, no cleanup, no workaround.
2. Otherwise read `data.sessionPromptFile` (UTF-8 text) and follow it exactly: it loops `next`, read the task's prompt file, do the task, `submit`, until `next` returns `data.done`.
3. If the user already pasted a session prompt, follow that one; never start a second session.
4. Run the commands as given (`next.line`). If a quoted executable path does not run in PowerShell, prefix it with `& ` or run `next.argv` instead.
5. To cancel on request, run `python3 -B <eval-runner> abort`.

## Rules

- Never read or edit the runner (`Evaluation~/`), its state folder `Library/UnitySkills/eval`, verification files or reports, and never look for reference answers. Submit returns no score by design; do not grade yourself.
- Work through UnitySkills REST skills as each task prompt says; the protocol in the sibling unity-skills skill (`<skills-dir>/unity-skills/SKILL.md`) applies.
- Token usage: after a submit the runner reads the token numbers for that task, never message text, from this machine's local host session log (Claude Code or Codex) and keeps them in the report. Add `--no-usage` to `start` when the user does not want that.
