"""
Test v1.3.0 Material Skills
Focuses on:
1. Material Creation
2. Assignment using Flexible Finding (Name/Path)
3. Modification using sharedMaterial (No Leaks)
"""
import sys
import time
import json

# Ensure local script usage
sys.path.insert(0, 'c:/Users/Betsy/.gemini/antigravity/skills/unity-skills/scripts')
from unity_skills import is_unity_running, call_skill

def run_test():
    print("=== Testing v1.3.0 Material Skills ===")
    
    if not is_unity_running():
        print("[FAIL] Not connected to Unity. Is the REST server running?")
        return
    
    url = "http://localhost:8090" # Used for reference if needed, but unity_skills handles it

    # 1. Setup Scene
    print("\n[1] Setting up test object...")
    cube_name = f"MatTestCube_{int(time.time())}"
    call_skill('gameobject_create', name=cube_name, primitiveType='Cube', x=2, y=1, z=0)
    
    # Helper to check success
    def is_success(r):
        if r.get('success') is True: return True
        if r.get('status') == 'success': return True
        # Check wrapped result
        if r.get('result') and isinstance(r['result'], dict):
             if r['result'].get('success') is True: return True
             if r['result'].get('status') == 'success': return True
        return False

    # 2. Create Material
    print("\n[2] Creating Material Asset...")
    mat_path = "Assets/TestMat_v1.3.mat"
    res = call_skill('material_create', name="TestMat_v1.3", savePath=mat_path)
    if is_success(res):
        print(f"  [PASS] Created {mat_path}")
    else:
        print(f"  [FAIL] Create Material: {res}")
        return

    # 3. Assign Material (Using Name Finding)
    print("\n[3] Assigning Material (using name finding)...")
    res = call_skill('material_assign', name=cube_name, materialPath=mat_path)
    if is_success(res):
        print(f"  [PASS] Assigned material to {cube_name}")
    else:
        print(f"  [FAIL] Assign Material: {res}")
        return

    # 4. Set Color (This uses sharedMaterial on backend - CHECK UNITY CONSOLE FOR LEAKS)
    print("\n[4] Setting Color (Red) - Should NOT cause leak warning...")
    res = call_skill('material_set_color', name=cube_name, r=1, g=0, b=0, a=1)
    if is_success(res):
        print(f"  [PASS] Set Color to Red")
    else:
        print(f"  [FAIL] Set Color: {res}")

    # 5. Test Path Finding (New Feature)
    print("\n[5] Testing Path Finding (Create child and modify)...")
    # Create child
    call_skill('gameobject_create', name="ChildCube", primitiveType='Cube')
    call_skill('gameobject_set_parent', childName="ChildCube", parentName=cube_name)
    
    # Modify child using PATH
    child_path = f"{cube_name}/ChildCube"
    print(f"  Targeting path: '{child_path}'")
    
    # Assign same material to child
    res = call_skill('material_assign', path=child_path, materialPath=mat_path)
    if is_success(res):
        print(f"  [PASS] Assigned material using PATH finding")
    else:
        print(f"  [FAIL] Path Finding Assignment: {res}")

    # Set Child Color (Blue) - impacting shared material (both will turn blue)
    res = call_skill('material_set_color', path=child_path, r=0, g=0, b=1)
    if is_success(res):
        print(f"  [PASS] Set Color (Blue) using PATH finding")
        print("  (Note: Since they share material, BOTH should now be blue)")
    else:
        print(f"  [FAIL] Path Finding Set Color: {res}")

    print("\n=== Test Complete ===")
    print("Check Unity Console: There should be NO 'Instantiating material' warnings.")

if __name__ == "__main__":
    run_test()
