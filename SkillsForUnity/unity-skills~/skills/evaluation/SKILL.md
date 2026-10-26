---
name: unity-evaluation
description: Run the UnitySkills evaluation set (a scored benchmark of tasks) on the user's own Unity project - start a session with the bundled entry script and complete its tasks one by one. Use only when the user explicitly asks to run, resume or continue a UnitySkills evaluation or benchmark (for example "run the UnitySkills evaluation"), or pastes an evaluation session prompt. Never use it for ordinary Unity work, Unity questions, or testing the user's own game. 仅当用户明确要求运行或继续 UnitySkills 评测集（跑测评集、评测集测试、UnitySkills 评测），或贴出评测会话提示时使用；日常 Unity 开发、Unity 问答、测试用户自己的游戏一律不要使用。
---

# UnitySkills Evaluation (advisory)

**Advisory module, no REST skills.** The full evaluation flow (asking for scope, track, tier and language, then running every task) lives in the `unity-skills-eval` skill, installed beside this one. Use that skill when it is available.

Without it, `<skill-root>` is the folder holding the root `SKILL.md` and `scripts/` (two levels above this file):

```
python3 -B <skill-root>/scripts/unity_eval.py start --track ops --tier basic --lang en
```

Then read `data.sessionPromptFile` and follow it: it loops `next` and `submit` until `data.done`. Never read or edit the runner (`Evaluation~/`) or its state, and never grade yourself: the score appears only in the Unity window (Evaluation tab).
