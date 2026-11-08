### material_create_batch
Create multiple materials.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, path}]}`

```bash
curl -s "http://localhost:<port>/skill/material_create_batch?expectProject=<project>" -d '{"items": "[{\"name\": \"Red\", \"savePath\": \"Assets/Materials\"}, {\"name\": \"Blue\", \"savePath\": \"Assets/Materials\"}, {\"name\": \"Green\", \"savePath\": \"Assets/Materials\"}]"}'
```

Batch-first: one `material_create_batch`, one `material_set_colors_batch` and one `material_assign_batch` replace six single calls:

```bash
# Six single calls
curl -s "http://localhost:<port>/skill/material_create?expectProject=<project>" -d '{"name":"Mat1","savePath":"Assets/Materials"}'
curl -s "http://localhost:<port>/skill/material_create?expectProject=<project>" -d '{"name":"Mat2","savePath":"Assets/Materials"}'
curl -s "http://localhost:<port>/skill/material_set_color?expectProject=<project>" -d '{"path":"Assets/Materials/Mat1.mat","r":1,"g":0,"b":0}'
curl -s "http://localhost:<port>/skill/material_set_color?expectProject=<project>" -d '{"path":"Assets/Materials/Mat2.mat","r":0,"g":0,"b":1}'
curl -s "http://localhost:<port>/skill/material_assign?expectProject=<project>" -d '{"name":"Cube1","materialPath":"Assets/Materials/Mat1.mat"}'
curl -s "http://localhost:<port>/skill/material_assign?expectProject=<project>" -d '{"name":"Cube2","materialPath":"Assets/Materials/Mat2.mat"}'
```

The batch version of the same work is three calls:

```bash
curl -s "http://localhost:<port>/skill/material_create_batch?expectProject=<project>" -d '{"items": "[{\"name\": \"Mat1\", \"savePath\": \"Assets/Materials\"}, {\"name\": \"Mat2\", \"savePath\": \"Assets/Materials\"}]"}'
curl -s "http://localhost:<port>/skill/material_set_colors_batch?expectProject=<project>" -d '{"items": "[{\"path\": \"Assets/Materials/Mat1.mat\", \"r\": 1, \"g\": 0, \"b\": 0}, {\"path\": \"Assets/Materials/Mat2.mat\", \"r\": 0, \"g\": 0, \"b\": 1}]"}'
curl -s "http://localhost:<port>/skill/material_assign_batch?expectProject=<project>" -d '{"items": "[{\"name\": \"Cube1\", \"materialPath\": \"Assets/Materials/Mat1.mat\"}, {\"name\": \"Cube2\", \"materialPath\": \"Assets/Materials/Mat2.mat\"}]"}'
```

