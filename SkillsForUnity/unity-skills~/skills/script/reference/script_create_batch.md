### script_create_batch
Create several scripts in one call: one domain reload instead of one per script.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `scriptName` (or `name`), `folder`, `template`, `namespaceName` (or `namespace`), `content`, each as in `script_create`.

**Returns**: `{success, totalItems, successCount, failCount, results}`; each result is a full `script_create` result (`path`, `className`, `jobId`, `waitUrl`, ...) or `{target, success: false, error}`. Not atomic: one failed item (e.g. the file already exists) turns the whole response into an error, yet the other scripts are already written. Read `results` and resend only the failed items. All items share one compilation; each `waitUrl` reports its own file's diagnostics.

Before creating, decide each class role: thin MonoBehaviour bridge, ScriptableObject configuration asset, or plain C# domain/service class (pass it as `content`).

```python
# 1 call + 1 domain reload instead of 3 calls + 3 reloads
unity_skills.call_skill("script_create_batch", items=[
    {"scriptName": "PlayerController", "folder": "Assets/Scripts/Player", "template": "MonoBehaviour"},
    {"scriptName": "EnemyAI", "folder": "Assets/Scripts/Enemy", "template": "MonoBehaviour"},
    {"scriptName": "GameSettings", "folder": "Assets/Scripts/Data", "template": "ScriptableObject"}
])
```
