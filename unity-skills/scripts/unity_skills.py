#!/usr/bin/env python3
"""
UnitySkills - Minimal Python client for Unity REST API.
AI agents use this library to directly control Unity Editor.

Usage:
    import unity_skills
    unity_skills.create_cube(x=1, y=2, z=3)
"""

import requests
from typing import Any, Dict, Optional

UNITY_URL = "http://localhost:8090"


def call_skill(name: str, **kwargs) -> Dict[str, Any]:
    """
    Call a Unity skill by name.
    
    Args:
        name: Skill name (e.g., "create_cube")
        **kwargs: Skill parameters
        
    Returns:
        Response dict with 'status' and 'result' keys
    """
    try:
        response = requests.post(
            f"{UNITY_URL}/skill/{name}",
            json=kwargs,
            timeout=30
        )
        return response.json()
    except requests.exceptions.ConnectionError:
        return {"status": "error", "error": "Unity not running. Start REST server in Unity: Window > UnitySkills > Start REST Server"}
    except Exception as e:
        return {"status": "error", "error": str(e)}


def get_skills() -> Dict[str, Any]:
    """Get list of all available skills."""
    try:
        response = requests.get(f"{UNITY_URL}/skills", timeout=5)
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


def delete_object(object_name: str) -> Dict:
    """Delete a GameObject by name."""
    return call_skill("delete_object", objectName=object_name)


def get_scene_info() -> Dict:
    """Get information about the current scene."""
    return call_skill("get_scene_info")


def find_objects_by_tag(tag: str) -> Dict:
    """Find all GameObjects with a specific tag."""
    return call_skill("find_objects_by_tag", tag=tag)


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
