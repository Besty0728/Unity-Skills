# UnitySkills Evaluation (advisory)

Advisory module, no REST skills. The full flow (asking for scope, track, tier and language, then running every task) lives in the `unity-skills-eval` skill, installed beside this one. Use that skill when it is available.

Without it, `<skill-root>` is the folder holding the root `SKILL.md` and `scripts/` (two levels above this file):

```
python3 -B <skill-root>/scripts/unity_eval.py start --track ops --tier basic --lang en
```

Then read `data.sessionPromptFile` and follow it: it loops `next` and `submit` until `data.done`.

## Pitfalls
- Never read or edit the runner (`Evaluation~/`) or its state.
- Never grade yourself: the score appears only in the Unity window (Evaluation tab).
