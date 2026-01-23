---
name: unity-skills
description: "Control Unity Editor via Python. Primary interface for ALL Unity operations."
---

# Unity Skills Interface

Use the `unity_skills` Python library to control the Unity Editor.
This interface allows for **Atomic** and **Transactional** operations. All calls are automatically reverted if they fail.

## 1. Setup & Connection

```python
import unity_skills
# Connects to specific Unity project.
# Use terminal `python unity_skills.py --list-instances` to find targets.
client = unity_skills.connect(target="MyProject") 
```

## 2. Core API Reference

The client provides pythonic wrappers for all operations.

### GameObject Operations
*   `client.create_gameobjects_batch(items=[{"name": "Cube", "primitiveType": "Cube", "x": 0, "y": 1}])`
*   `client.delete_gameobjects_batch(items=["Obj1", "Obj2"])`
*   `client.set_transforms_batch(items=[{"name": "Cube", "x": 10, "rotY": 90, "scaleX": 2}])`
*   `client.set_active_batch(items=[{"name": "Cube", "active": False}])`
*   `client.find_objects_by_tag(tag="Player")`

### Components
*   `client.add_components_batch(items=[{"name": "Cube", "componentType": "Rigidbody"}])`
*   `client.set_component_properties_batch(items=[{"name": "Cube", "componentType": "Light", "propertyName": "intensity", "value": 5.0}])`

### Assets (Files)
*   `client.create_scripts_batch(items=[{"scriptName": "MyScript", "folder": "Assets/Scripts", "template": "MonoBehaviour"}])`
*   `client.import_assets_batch(items=[{"sourcePath": "C:/tmp.png", "destinationPath": "Assets/Tex.png"}])`
*   `client.move_assets_batch(items=[{"sourcePath": "Assets/Old", "destinationPath": "Assets/New"}])`

### UI Creation
*   `client.create_ui_batch(items=[{"type": "Button", "name": "StartBtn", "text": "Start", "parent": "Canvas"}])`

## 3. Best Practices (Important)

1.  **Batching**: ALWAYS use `_batch` or `items=[]` versions for >1 objects. It is 100x faster and atomic.
2.  **Wait for Compilation**: After `create_scripts`, Unity reloads. Do not call skills immediately.
3.  **Return Values**: All calls return a Dict: `{'status': 'success', 'result': ...}` or `{'status': 'error', 'error': ...}`.
4.  **Verbose Output**: By default, fetch operations return minimal data (ID/Name). Pass `verbose=True` only if you need full details.
