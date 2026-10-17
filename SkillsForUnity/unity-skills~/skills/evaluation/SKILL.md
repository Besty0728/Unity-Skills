---
name: unity-evaluation
description: Run the UnitySkills evaluation set (a scored benchmark of tasks) on the user's own Unity project - start a session with the bundled entry script and complete its tasks one by one. Use only when the user explicitly asks to run, resume or continue a UnitySkills evaluation or benchmark (for example "run the UnitySkills evaluation"), or pastes an evaluation session prompt. Never use it for ordinary Unity work, Unity questions, or testing the user's own game. 仅当用户明确要求运行或继续 UnitySkills 评测集（跑测评集、评测集测试、UnitySkills 评测），或贴出评测会话提示时使用；日常 Unity 开发、Unity 问答、测试用户自己的游戏一律不要使用。
---

## Triggers
- The user asks to run, resume or continue the UnitySkills evaluation set
- The user pastes a prompt starting with "This is a UnitySkills evaluation"
- 用户要求跑 UnitySkills 测评集、评测集测试，或贴出以"这是一次 UnitySkills 评测"开头的提示

Not for building, debugging or testing the user's own project.

# UnitySkills Evaluation (advisory)

**Advisory module — no REST skills.** It only tells you how to drive the evaluation runner that ships with the UnitySkills package. Each task temporarily opens a run scene and adds assets under `Assets/__UnitySkillsEval/`; after the submit the runner restores the user's scenes and deletes them. The score appears in Unity (UnitySkills window, Evaluation tab), never in the chat.

## Start

`<skill-root>` is the folder holding the root `SKILL.md`, `scripts/` and `skills/` — two levels above this file.

```
python3 -B <skill-root>/scripts/unity_eval.py start --track ops --tier basic --lang en
```

- Run it from the Unity project folder, or add `--project <path to the project>`. The script finds the project and its UnitySkills package itself. Python 3.9 or newer; use `python` or `py -3` where `python3` is missing.
- `--lang` is `zh-CN` when the user writes Chinese, otherwise `en`. Keep `--track ops --tier basic` unless the user names others (`--track dev`, `--tier standard`, `--tier challenge`). Optional: `--mode strict` (one task per agent session), `--tasks ID[,ID...]`.
- stdout is one JSON line: `ok`, `command`, `data`, `problems`, `next`.

## Run

1. When `problems` is not empty, give the user each `fix` text verbatim and stop. Do not repair the environment yourself: no mode switching, no cleanup, no workaround.
2. Otherwise read `data.sessionPromptFile` (UTF-8 text) and follow it exactly: it loops `next`, read the task's prompt file, do the task, `submit`, until `next` returns `data.done`.
3. If the user already pasted a session prompt, follow that one; never start a second session.
4. Run the commands as given (`next.line`). If a quoted executable path does not run in PowerShell, prefix it with `& ` or run `next.argv` instead.
5. To cancel on request, run the same script with `abort`.

## Rules

- Never read or edit the runner (`Evaluation~/`), its state folder `Library/UnitySkills/eval`, verification files or reports, and never look for reference answers. Submit returns no score by design; do not grade yourself.
- Work through UnitySkills REST skills as each task prompt says; the protocol in the root `SKILL.md` applies.
- Token usage: after a submit the runner reads the token numbers for that task, never message text, from this machine's local host session log (Claude Code or Codex) and keeps them in the report. Add `--no-usage` to `start` when the user does not want that.
