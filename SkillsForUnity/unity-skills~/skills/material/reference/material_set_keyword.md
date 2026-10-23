### material_set_keyword
Enable/disable shader keywords.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `entityId` | string | No* | - | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `keyword` | string | Yes | - | Keyword name |
| `enable` | bool | No | true | Enable or disable |

**Common Keywords**: `_EMISSION`, `_NORMALMAP`, `_METALLICGLOSSMAP`, `_ALPHATEST_ON`, `_ALPHABLEND_ON`
