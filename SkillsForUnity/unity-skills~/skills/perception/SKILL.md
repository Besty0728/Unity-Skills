---
name: unity-perception
description: "Scene understanding and analysis. Use when users want to get a summary, overview, dependency report, or export of the current scene state. Triggers: scene summary, analyze, overview, statistics, count, export report, 场景摘要, Unity分析, Unity概览, Unity统计, 导出报告, 依赖分析."
---

# Unity Perception Skills

Use this module for read-only scene and project analysis. It is available in Semi-Auto by default.

## Guardrails

**Mode**: Semi-Auto

**DO NOT** (common hallucinations):
- `perception_analyze`, `perception_scan`, and `perception_describe` do not exist
- `scene_context` is not `editor_get_context`: it exports hierarchy/components/references, while editor context focuses on current editor state
- `scene_analyze`, `scene_health_check`, `scene_contract_validate`, `scene_component_stats`, `scene_find_hotspots`, and `project_stack_detect` belong to this module even if the prefix looks like `scene_*` or `project_*`

**Routing**:
- Current selection/play-mode/editor state -> `editor_get_context`
- Object search by name/path -> `scene_find_objects` or `gameobject_find`
- Script dependency closure -> `script_dependency_graph`

## Skills

### Scene Health and Summary

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `scene_analyze` | Combined scene + project analysis | `topComponentsLimit?`, `issueLimit?`, `deepHierarchyThreshold?`, `largeChildCountThreshold?` |
| `scene_health_check` | Read-only health report | `issueLimit?`, `deepHierarchyThreshold?`, `largeChildCountThreshold?` |
| `scene_summarize` | Structured scene summary | `includeComponentStats?`, `topComponentsLimit?` |
| `scene_component_stats` | Component and facility stats | `topComponentsLimit?` |
| `scene_find_hotspots` | Deep hierarchy / large group / empty node hotspots | thresholds + `maxResults?` |
| `scene_tag_layer_stats` | Tag/layer usage | none |
| `scene_performance_hints` | Prioritized optimization hints | none |

### Scene Snapshots and Exports

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `scene_diff` | Capture or compare lightweight snapshots | `snapshotJson?` |
| `hierarchy_describe` | Return text hierarchy tree | `maxDepth?`, `includeInactive?`, `maxItemsPerLevel?` |
| `scene_context` | Export hierarchy, components, references | `maxDepth?`, `maxObjects?`, `rootPath?`, `includeValues?`, `includeReferences?`, `includeCodeDeps?` |
| `scene_export_report` | Save markdown scene report | `savePath?`, `maxDepth?`, `maxObjects?` |
| `scene_dependency_analyze` | Analyze impact / dependency graph in-scene | `targetPath?`, `savePath?` |

### Project and Script Analysis

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `project_stack_detect` | Detect pipeline, input, UI, packages, tests, folders | none |
| `script_analyze` | Analyze one MonoBehaviour / ScriptableObject / user class by class name | `scriptName`, `includePrivate?` |
| `script_dependency_graph` | N-hop dependency closure for one script class name | `scriptName`, `maxHops?`, `includeDetails?` |

### Spatial and Material Queries

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `scene_spatial_query` | Find objects near a point or object | `x/y/z?`, `radius?`, `nearObject?`, `componentFilter?`, `maxResults?` |
| `scene_materials` | Summarize scene materials and shaders | `includeProperties?` |
| `scene_contract_validate` | Validate default roots/tags/layers/UI EventSystem conventions | `requiredRootsJson?`, `requiredTagsJson?`, `requiredLayersJson?`, `requireEventSystemForUi?` |

## High-Frequency Skill Differences

### `scene_summarize` vs `scene_analyze` vs `scene_health_check`

| Skill | Best for | Typical output focus |
|-------|----------|----------------------|
| `scene_summarize` | Fast overview | object counts, hierarchy depth, top components |
| `scene_analyze` | Broad diagnosis | summary + findings + warnings + recommendations + next-skill hints |
| `scene_health_check` | Hygiene / red flags | missing scripts, duplicate names, deep hierarchy, empty nodes, hotspot-style findings |

### `scene_context` vs `hierarchy_describe`

| Skill | Best for | Output style |
|-------|----------|-------------|
| `hierarchy_describe` | Human-readable tree | text tree, lightweight |
| `scene_context` | AI coding context | structured hierarchy + components + references + optional code dependencies |

### `scene_dependency_analyze` vs `script_dependency_graph`

| Skill | Scope | Use when |
|-------|-------|----------|
| `scene_dependency_analyze` | Scene object references | ask "who depends on this object if I delete or disable it" |
| `script_dependency_graph` | Script class dependency closure | ask "which scripts do I have to touch to change this feature" |

## Key Return Shapes

### `scene_summarize`

Returns `sceneName`, `stats`, and optional `topComponents`.

### `scene_analyze`

Returns `summary`, `stats`, `findings`, `warnings`, `recommendations`, and `suggestedNextSkills`.

### `scene_health_check`

Returns `summary`, `findings`, `hotspots`, and `suggestedNextSkills`.

### `scene_context`

Returns a structured export with `objects`, `references`, and optional `codeDependencies`. Use it when another AI step needs full scene context, not just a human summary.

High-frequency options:
- `rootPath` to export only one subtree
- `includeValues=true` when serialized field values matter
- `includeCodeDeps=true` when AI needs a rough scene-to-code dependency picture

### `scene_export_report`

Writes a markdown artifact to disk and returns `savedTo`, object/script/reference counts, and success state. Prefer this when the user wants a durable report file.

Defaults:
- `savePath = "Assets/Docs/SceneReport.md"`
- `maxDepth = 10`
- `maxObjects = 500`

## When to Use Which Skill

| Need | Best first skill |
|------|------------------|
| Quick scene overview | `scene_summarize` |
| Full diagnosis | `scene_analyze` |
| Suspicious hierarchy or clutter | `scene_find_hotspots` |
| Safe-to-delete / impact question | `scene_dependency_analyze` |
| AI coding context export | `scene_context` |
| Script reading order | `script_dependency_graph` |
| Render/input/UI stack detection | `project_stack_detect` |

## Minimal Example

```python
import unity_skills

summary = unity_skills.call_skill("scene_summarize", includeComponentStats=True)
health = unity_skills.call_skill("scene_health_check", issueLimit=50)
context = unity_skills.call_skill("scene_context", maxDepth=6, maxObjects=120)
report = unity_skills.call_skill("scene_export_report", savePath="Assets/Docs/SceneReport.md")
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
