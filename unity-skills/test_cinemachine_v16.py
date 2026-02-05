
import unity_skills
import json
import time

def test_cinemachine_v16():
    print("🚀 Starting Cinemachine v1.6 Enhancement Verification...")
    
    # 1. Create a VCam
    print("\n[1/6] Creating VCam...")
    vcam_name = "CM_v16_Test_Cam"
    res = unity_skills.cinemachine_create_vcam(name=vcam_name)
    if not res.get('success'):
        print(f"❌ Failed to create VCam: {res}")
        return
    print("✅ VCam Created")

    # 2. Test cinemachine_set_component (Switching procedural components)
    print("\n[2/6] Testing Component Switching (Rotation Control)...")
    
    # Switch Aim to Composer
    print("   -> Setting Aim to 'CinemachineComposer'")
    res = unity_skills.cinemachine_set_component(vcamName=vcam_name, stage="Aim", componentType="CinemachineComposer")
    print(f"   Result: {res}")

    # Verify via Inspection
    inspect = unity_skills.cinemachine_inspect_vcam(objectName=vcam_name)
    comps = [c.get('_type') for c in inspect.get('components', [])]
    if "CinemachineComposer" in comps:
        print("   ✅ Validated: CinemachineComposer is present")
    else:
        print(f"   ❌ Failed: Components found: {comps}")

    # Switch Body to OrbitalFollow
    print("   -> Setting Body to 'CinemachineOrbitalFollow'")
    res = unity_skills.cinemachine_set_component(vcamName=vcam_name, stage="Body", componentType="CinemachineOrbitalFollow")
    print(f"   Result: {res}")
    
    inspect = unity_skills.cinemachine_inspect_vcam(objectName=vcam_name)
    comps = [c.get('_type') for c in inspect.get('components', [])]
    if "CinemachineOrbitalFollow" in comps:
        print("   ✅ Validated: CinemachineOrbitalFollow is active")
    else:
        print(f"   ❌ Failed: Components found: {comps}")

    # Remove Body Component
    print("   -> Removing Body Component (Setting to None)")
    res = unity_skills.cinemachine_set_component(vcamName=vcam_name, stage="Body", componentType="None")
    
    inspect = unity_skills.cinemachine_inspect_vcam(objectName=vcam_name)
    comps = [c.get('_type') for c in inspect.get('components', [])]
    if "CinemachineOrbitalFollow" not in comps:
        print("   ✅ Validated: Body component removed")
    else:
        print(f"   ❌ Failed: Body component still present")

    # 3. Test Impulse with JSON Params
    print("\n[3/6] Testing Impulse with JSON Params...")
    # Ensure an impulse source exists (add one to VCam for testing if needed, though usually separate)
    # We will try to add one to the VCam object itself for simplicity of testing
    unity_skills.run_command_open(command=f'Undo.AddComponent<{unity_skills.CinemachineImpulseSource}>(GameObject.Find("{vcam_name}"))')
    
    # Parametrized Impulse
    params = {
        "velocity": {"x": 0, "y": 10, "z": 0}
    }
    json_params = json.dumps(params)
    print(f"   -> Sending Impulse with params: {json_params}")
    res = unity_skills.cinemachine_impulse_generate(sourceParams=json_params)
    if res.get('success'):
         print(f"   ✅ Success: {res.get('message')}")
    else:
         print(f"   ❌ Failed: {res}")

    # 4. Clean up
    # print("\n[4/6] Cleanup...")
    # unity_skills.delete_gameobject(name=vcam_name) 
    # print("✅ Cleanup Done")

    print("\n🎉 Verification v1.6 Complete!")

if __name__ == "__main__":
    test_cinemachine_v16()
