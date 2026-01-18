
import sys
import json
import time

# Helper to ensure we can import unity_skills
sys.path.insert(0, 'c:/Users/Betsy/.gemini/antigravity/skills/unity-skills/scripts')
from unity_skills import call_skill

def is_success(r):
    if r.get('success') is True: return True
    if r.get('status') == 'success': return True
    if r.get('result') and isinstance(r['result'], dict):
         if r['result'].get('success') is True: return True
         if r['result'].get('status') == 'success': return True
    return False

def run():
    print("=== Applying URP Material to Cube ===")

    # 1. Create Cube
    cube_name = f"URP_Cube_{int(time.time())}"
    print(f"\n[1] Creating Cube: {cube_name}")
    call_skill('gameobject_create', name=cube_name, primitiveType="Cube", x=0, y=1, z=0)

    # 2. Create Material and SAVE IT
    mat_path = "Assets/URP_Visual_Test.mat"
    print(f"\n[2] Creating & Saving Material: {mat_path}")
    # Note: Default shader is now URP/Lit from previous fix
    res = call_skill('material_create', name="URP_Visual", savePath=mat_path)
    print(f"Create Result: {json.dumps(res)}")

    if not is_success(res):
        print("Stopping: Material creation failed.")
        return

    # 3. Assign to Cube
    print(f"\n[3] Assigning {mat_path} to {cube_name}")
    assign_res = call_skill('material_assign', name=cube_name, materialPath=mat_path)
    print(f"Assign Result: {json.dumps(assign_res)}")

    if is_success(assign_res):
        print("\nSUCCESS! Check Unity Scene:")
        print(f"1. Find object '{cube_name}'")
        print(f"2. Verify it has 'URP_Visual_Test' material (White/Grey URP Lit)")
        print(f"3. Verify material exists in Project at '{mat_path}'")
    else:
        print("\nFAILED to assign material.")

if __name__ == "__main__":
    run()
