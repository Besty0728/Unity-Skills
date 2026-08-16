---
name: unity-blueprints
description: Advise on starter architecture blueprints for small games
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Starting a small game from scratch
- Scaffolding a genre core structure
- Organizing a specific game type
- 从零开始做小游戏、搭建某类型核心结构、组织特定玩法

# Unity Gameplay Blueprints

Use this skill when starting a new mini-game or vertical slice and a lightweight architecture skeleton is more useful than raw code volume.

## Supported Blueprint Styles

- 2D platformer
- top-down shooter
- endless runner
- puzzle / interaction game
- tower defense
- clicker / incremental
- card / turn-based prototype

## Output Format

- Core loop
- Recommended scenes
- Recommended modules
- Initial script list
- Data/config assets
- UI responsibilities
- What to deliberately keep simple

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Provide the smallest viable blueprint, not a giant reusable framework.
- Prefer a short script inventory over “future-proof” template sprawl.
