# Unity Perception Skills

Read-only analysis of the open scene, the project and scripts. Parameters come from the schema (`GET /skills/schema?category=Perception&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Gathering context before editing an unfamiliar scene or project
- Auditing scene structure, hygiene or hotspots without changing anything
- Checking what depends on an object or script before a change
- Exporting structured scene context for another AI step

## Rules
- 17 skills are `SemiAuto` and run in every mode without a grant; 16 of them are pure reads.
- `scene_dependency_analyze` is `SemiAuto` but not read-only: passing `savePath` writes a markdown file and imports it as an asset. Without `savePath` it returns `analysis` and `markdown` in the response and writes nothing. Pass `savePath` only when the user asked for a file; the response then names it in `savedTo`.
- `scene_export_report` writes a markdown file (default `Assets/Docs/SceneReport.md`) and is `FullAuto`: under Approval it needs a grant.
- No skill here deletes, enters Play Mode or triggers a reload.

## Pitfalls
- `scene_context` exports hierarchy, components and references for AI coding. It is not `editor_get_context`, which reports the current editor state.
- `scene_context` `maxObjects` defaults to 200; use `rootPath` to export one subtree.

## Routing
- Current selection, Play Mode or editor state → `editor_get_context`.
- Object search by name or path → `scene_find_objects` or `gameobject_find`.
- Script dependency closure → `script_dependency_graph`.

## Choosing between similar skills

| Need | Use | Why |
|------|-----|-----|
| Quick overview | `scene_summarize` | Object counts, depth, top components |
| Broad diagnosis | `scene_analyze` | Summary, findings, warnings, recommendations, next-skill hints |
| Hygiene and red flags | `scene_health_check` | Missing scripts, duplicate names, deep hierarchy, empty nodes |
| Human-readable tree | `hierarchy_describe` | Lightweight text tree |
| Context for another AI step | `scene_context` | Structured hierarchy, components, references, optional code dependencies |
| What depends on this object | `scene_dependency_analyze` | Scene object references, for deletion or disabling |
| Which scripts a feature touches | `script_dependency_graph` | Script class dependency closure |
| Render, input and UI stack | `project_stack_detect` | Pipeline and package detection |

## Key return shapes
- `scene_summarize`: `sceneName`, `stats`, optional `topComponents`.
- `scene_analyze`: `summary`, `stats`, `findings`, `warnings`, `recommendations`, `suggestedNextSkills`.
- `scene_health_check`: `summary`, `findings`, `hotspots`, `suggestedNextSkills`.
- `scene_context`: `objects`, `references`, optional `codeDependencies`. Set `includeValues=true` when serialized field values matter, and `includeCodeDeps=true` for a rough scene-to-code picture.
- `scene_export_report`: `savedTo`, object, script and reference counts, success.

**DO NOT** (common hallucinations):
- `perception_analyze`, `perception_scan` and `perception_describe` do not exist.
- `scene_analyze`, `scene_health_check`, `scene_contract_validate`, `scene_component_stats`, `scene_find_hotspots` and `project_stack_detect` belong to this module, even though `project_*` or `scene_*` prefixes suggest otherwise.

## Skills

### scene_analyze
Combined scene and project analysis. **Parameters:** `topComponentsLimit?`, `issueLimit?`, `deepHierarchyThreshold?`, `largeChildCountThreshold?`

### scene_health_check
Read-only health report. **Parameters:** `issueLimit?`, `deepHierarchyThreshold?`, `largeChildCountThreshold?`

### scene_summarize
Structured scene summary. **Parameters:** `includeComponentStats?`, `topComponentsLimit?`

### scene_component_stats
Component and facility stats. **Parameters:** `topComponentsLimit?`

### scene_find_hotspots
Deep hierarchy, large group and empty node hotspots. **Parameters:** thresholds, `maxResults?`

### scene_tag_layer_stats
Tag and layer usage. **Parameters:** none.

### scene_performance_hints
Prioritized optimization hints. **Parameters:** none.

### scene_diff
Capture or compare lightweight snapshots. **Parameters:** `snapshotJson?`

### hierarchy_describe
Text hierarchy tree. **Parameters:** `maxDepth?`, `includeInactive?`, `maxItemsPerLevel?`

### scene_context
Export hierarchy, components, references. **Parameters:** `maxDepth?`, `maxObjects?`, `rootPath?`, `includeValues?`, `includeReferences?`, `includeCodeDeps?`

### scene_export_report
Save a markdown scene report. **Parameters:** `savePath?`, `maxDepth?`, `maxObjects?`

### scene_dependency_analyze
Impact and dependency graph within the scene. **Parameters:** `targetPath?`, `savePath?` (writes a file)

### project_stack_detect
Detect pipeline, input, UI, packages, tests, folders. **Parameters:** none.

### script_analyze
Analyse one MonoBehaviour, ScriptableObject or user class by class name. **Parameters:** `scriptName`, `includePrivate?`

### script_dependency_graph
N-hop dependency closure for one script class name. **Parameters:** `scriptName`, `maxHops?`, `includeDetails?`

### scene_spatial_query
Objects near a point or object. **Parameters:** `x/y/z?`, `radius?`, `nearObject?`, `componentFilter?`, `maxResults?`

### scene_materials
Scene materials and shaders. **Parameters:** `includeProperties?`

### scene_contract_validate
Check default roots, tags, layers and UI EventSystem conventions. **Parameters:** `requiredRootsJson?`, `requiredTagsJson?`, `requiredLayersJson?`, `requireEventSystemForUi?`
