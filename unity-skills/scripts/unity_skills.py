#!/usr/bin/env python3
"""
UnitySkills - Minimal Python client for Unity REST API.
AI agents use this library to directly control Unity Editor.

Usage:
    import unity_skills
    unity_skills.create_cube(x=1, y=2, z=3)
"""

import requests
import sys
import os
import io
from typing import Any, Dict, Optional

# Windows 控制台编码终极修复：
# 1. 强制将 CMD/PowerShell 代码页切换为 65001 (UTF-8)
# 2. 强制 Python 标准输出使用 UTF-8
if sys.platform == 'win32':
    try:
        os.system('chcp 65001 >nul 2>&1')
        if hasattr(sys.stdout, 'reconfigure'):
            sys.stdout.reconfigure(encoding='utf-8')
            sys.stderr.reconfigure(encoding='utf-8')
    except Exception:
        pass

UNITY_URL = "http://localhost:8090"
DEFAULT_PORT = 8090

def get_registry_path():
    import os
    return os.path.join(os.path.expanduser("~"), ".unity_skills", "registry.json")

class UnitySkills:
    """
    Client for interacting with a specific Unity Editor instance.
    """
    def __init__(self, port: int = None, target: str = None, url: str = None):
        """
        Initialize client.
        Args:
            port: Connect to specific localhost port (e.g. 8091)
            target: Connect to instance by Name or ID (e.g. "MyGame" or "MyGame_A1B2") - auto-discovers port.
            url: Full URL override.
        """
        self.url = url
        
        if not self.url:
            if port:
                self.url = f"http://localhost:{port}"
            elif target:
                found_port = self._find_port_by_target(target)
                if found_port:
                    self.url = f"http://localhost:{found_port}"
                else:
                    raise ValueError(f"Could not find Unity instance matching '{target}' in registry.")
            else:
                # Default behavior: Try standard port, or first avail in registry?
                # Backward compatibility: Default to 8090
                self.url = f"http://localhost:{DEFAULT_PORT}"

    def _find_port_by_target(self, target: str) -> Optional[int]:
        import json
        import os
        reg_path = get_registry_path()
        if not os.path.exists(reg_path):
            return None
        try:
            with open(reg_path, 'r') as f:
                data = json.load(f)
                # target can be ID or Name
                # 1. Exact ID match
                for path, info in data.items():
                    if info.get('id') == target:
                        return info.get('port')
                # 2. Exact Name match (if unique?) - return first found
                for path, info in data.items():
                    if info.get('name') == target:
                        return info.get('port')
                return None
        except:
            return None

    def call(self, skill_name: str, verbose: bool = False, **kwargs) -> Dict[str, Any]:
        """
        Call a skill on this instance.
        Args:
            skill_name: Name of skill
            verbose: If True, returns full details. If False (default), returns summary for large datasets.
            **kwargs: Skill parameters
        """
        try:
            # Combine verbose into kwargs for JSON body
            kwargs['verbose'] = verbose
            response = requests.post(f"{self.url}/skill/{skill_name}", json=kwargs, timeout=30)
            response.encoding = 'utf-8'  # 强制 UTF-8 解码，确保中文正确显示
            return response.json()
        except requests.exceptions.ConnectionError:
             return {"status": "error", "error": f"Connection failed to {self.url}. Unity instance may be down."}
        except Exception as e:
            return {"status": "error", "error": str(e)}

    # --- Proxies for common skills ---
    def create_cube(self, x=0, y=0, z=0, name="Cube"): return self.call("create_cube", x=x, y=y, z=z, name=name)
    def create_sphere(self, x=0, y=0, z=0, name="Sphere"): return self.call("create_sphere", x=x, y=y, z=z, name=name)
    def delete_object(self, name): return self.call("delete_object", objectName=name)
    # ... allow calling any skill dynamically really ...


# Global Default Client (for backward compatibility)
_default_client = UnitySkills()

def connect(port: int = None, target: str = None) -> UnitySkills:
    """
    Connect to a specific Unity instance.
    Returns a UnitySkills client object.
    
    Usage:
      client = unity_skills.connect(target="MyGame")
      client.create_cube()
    """
    return UnitySkills(port=port, target=target)

def list_instances() -> list:
    """Return list of active Unity instances from registry."""
    import json
    import os
    reg_path = get_registry_path()
    if not os.path.exists(reg_path):
        return []
    try:
        with open(reg_path, 'r') as f:
            data = json.load(f)
            return list(data.values())
    except:
        return []

def call_skill(skill_name: str, **kwargs) -> Dict[str, Any]:
    return _default_client.call(skill_name, **kwargs)


def get_skills() -> Dict[str, Any]:
    """Get list of all available skills."""
    try:
        response = requests.get(f"{UNITY_URL}/skills", timeout=5)
        response.encoding = 'utf-8'
        return response.json()
    except Exception as e:
        return {"status": "error", "error": str(e)}


def health() -> bool:
    """Check if Unity server is running."""
    try:
        response = requests.get(f"{UNITY_URL}/health", timeout=2)
        return response.json().get("status") == "ok"
    except:
        return False


# ============================================================
# Convenience functions for common skills
# ============================================================

def create_cube(x: float = 0, y: float = 0, z: float = 0, name: str = "Cube") -> Dict:
    """Create a cube at the specified position."""
    return call_skill("create_cube", x=x, y=y, z=z, name=name)


def create_sphere(x: float = 0, y: float = 0, z: float = 0, name: str = "Sphere") -> Dict:
    """Create a sphere at the specified position."""
    return call_skill("create_sphere", x=x, y=y, z=z, name=name)


def set_object_color(object_name: str, r: float = 1, g: float = 1, b: float = 1) -> Dict:
    """Set the color of a GameObject's material."""
    return call_skill("set_object_color", objectName=object_name, r=r, g=g, b=b)


def set_colors_batch(items: list) -> Dict:
    """
    Set colors for multiple objects in one call.
    Args:
        items: List of dicts, e.g. [{"name": "Cube", "r": 1, "g": 0, "b": 0}]
    """
    import json
    return call_skill("material_set_colors_batch", items=json.dumps(items))


def delete_object(object_name: str) -> Dict:
    """Delete a GameObject by name."""
    return call_skill("delete_object", objectName=object_name)


def get_scene_info() -> Dict:
    """Get information about the current scene."""
    return call_skill("get_scene_info")


def find_objects_by_tag(tag: str) -> Dict:
    """Find all GameObjects with a specific tag."""
    """Find all GameObjects with a specific tag."""
    return call_skill("find_objects_by_tag", tag=tag)


# ============================================================
# Batch Operations (Efficient)
# ============================================================

def create_gameobjects_batch(items: list) -> Dict:
    """
    Create multiple GameObjects in one call.
    Args:
        items: List of dicts, e.g. [{"name": "Cube1", "primitiveType": "Cube", "x": 0, "y": 0, "z": 0}]
    """
    import json
    return call_skill("gameobject_create_batch", items=json.dumps(items))


def delete_gameobjects_batch(items: list) -> Dict:
    """
    Delete multiple objects.
    Args:
        items: List of strings (names) OR dicts ({"name": "Cube1", "instanceId": 123})
    """
    import json
    return call_skill("gameobject_delete_batch", items=json.dumps(items))


def set_active_batch(items: list) -> Dict:
    """
    Set active state for multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "active": False}]
    """
    import json
    return call_skill("gameobject_set_active_batch", items=json.dumps(items))


def set_transforms_batch(items: list) -> Dict:
    """
    Set transform properties for multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "x": 10, "rotY": 90}]
    """
    import json
    return call_skill("gameobject_set_transform_batch", items=json.dumps(items))


def add_components_batch(items: list) -> Dict:
    """
    Add components to multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "componentType": "Rigidbody"}]
    """
    import json
    return call_skill("component_add_batch", items=json.dumps(items))


def set_component_properties_batch(items: list) -> Dict:
    """
    Set component properties for multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "componentType": "Light", "propertyName": "intensity", "value": 5.0}]
    """
    import json
    return call_skill("component_set_property_batch", items=json.dumps(items))


def instantiate_prefabs_batch(items: list) -> Dict:
    """
    Instantiate multiple prefabs.
    Args:
        items: List of dicts [{"prefabPath": "Assets/Cube.prefab", "x": 0, "y": 0, "z": 0}]
    """
    import json
    return call_skill("prefab_instantiate_batch", items=json.dumps(items))


def create_ui_batch(items: list) -> Dict:
    """
    Create multiple UI elements.
    Args:
        items: List of dicts [{"type": "Button", "name": "Btn1", "text": "Click Me"}]
    """
    import json
    return call_skill("ui_create_batch", items=json.dumps(items))


def set_layers_batch(items: list) -> Dict:
    """
    Set layer for multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "layer": "Default", "recursive": True}]
    """
    import json
    return call_skill("gameobject_set_layer_batch", items=json.dumps(items))


def set_tags_batch(items: list) -> Dict:
    """
    Set tag for multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "tag": "Player"}]
    """
    import json
    return call_skill("gameobject_set_tag_batch", items=json.dumps(items))


def set_parents_batch(items: list) -> Dict:
    """
    Set parent for multiple objects.
    Args:
        items: List of dicts [{"childName": "Cube1", "parentName": "Container"}]
    """
    import json
    return call_skill("gameobject_set_parent_batch", items=json.dumps(items))


def remove_components_batch(items: list) -> Dict:
    """
    Remove components from multiple objects.
    Args:
        items: List of dicts [{"name": "Cube1", "componentType": "Rigidbody"}]
    """
    import json
    return call_skill("component_remove_batch", items=json.dumps(items))


def import_assets_batch(items: list) -> Dict:
    """
    Import multiple assets.
    Args:
        items: List of dicts [{"sourcePath": "C:/tmp/tex.png", "destinationPath": "Assets/Tex.png"}]
    """
    import json
    return call_skill("asset_import_batch", items=json.dumps(items))


def delete_assets_batch(items: list) -> Dict:
    """
    Delete multiple assets.
    Args:
        items: List of dicts [{"path": "Assets/OldFolder"}]
    """
    import json
    return call_skill("asset_delete_batch", items=json.dumps(items))


def move_assets_batch(items: list) -> Dict:
    """
    Move/Rename multiple assets.
    Args:
        items: List of dicts [{"sourcePath": "Assets/Old.mat", "destinationPath": "Assets/New.mat"}]
    """
    import json
    return call_skill("asset_move_batch", items=json.dumps(items))


def create_scripts_batch(items: list) -> Dict:
    """
    Create multiple scripts.
    Args:
        items: List of dicts [{"scriptName": "MyScript", "folder": "Assets/S", "namespace": "MyGame"}]
    """
    import json
    return call_skill("script_create_batch", items=json.dumps(items))


# ============================================================
# CLI for testing
# ============================================================

if __name__ == "__main__":
    import sys
    import json
    
    if len(sys.argv) < 2:
        print("Usage: python unity_skills.py <skill_name> [param=value ...]")
        print("       python unity_skills.py --list")
        sys.exit(1)
    
    if sys.argv[1] == "--list":
        print(json.dumps(get_skills(), indent=2))
    elif sys.argv[1] == "--list-instances":
        print(json.dumps(list_instances(), indent=2))
    else:
        skill_name = sys.argv[1]
        kwargs = {}
        for arg in sys.argv[2:]:
            if "=" in arg:
                key, value = arg.split("=", 1)
                # Try to parse as number
                try:
                    value = float(value)
                    if value.is_integer():
                        value = int(value)
                except ValueError:
                    pass
                kwargs[key] = value
        
        result = call_skill(skill_name, **kwargs)
        print(json.dumps(result, indent=2))
