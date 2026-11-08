### script_get_info
Get a compiled script's class name, base class and public members.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |

**Returns**: `{path, className, baseClass, namespaceName, isMonoBehaviour, publicMethods, publicFields: [{name, type}], note?}`. It reads the compiled class. A script not yet compiled (or abstract) returns `{path, className: "(unknown)", note}` with `note` set; wait for compilation after edits.
