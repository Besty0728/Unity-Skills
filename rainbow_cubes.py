
import sys
import time
import math
sys.path.insert(0, 'c:/Users/Betsy/.gemini/antigravity/skills/unity-skills/scripts')
from unity_skills import call_skill

# Configuration
SPACING = 1.5
START_X = -4.5
Y_POS = 1
Z_POS = 0

# Rainbow Colors (RGB)
RAINBOW = [
    ("Red",     (1.0, 0.0, 0.0)),
    ("Orange",  (1.0, 0.64, 0.0)),
    ("Yellow",  (1.0, 1.0, 0.0)),
    ("Green",   (0.0, 1.0, 0.0)),
    ("Blue",    (0.0, 0.0, 1.0)),
    ("Indigo",  (0.29, 0.0, 0.51)),
    ("Violet",  (0.56, 0.0, 1.0))
]

def clean_scene():
    print("cleaning scene...")
    # Delete objects seen in user's screenshot
    prefixes = [
        "Visual_Verification",
        "SkillTest", # Common prefix, implies wildcard
        "FullTest_GO", 
        "MatTestCube",
        "URP_Cube",
        "Rainbow_Cube",
        "TestPrefab",
        "MyCube",
        "ChildCube"
    ]
     
    # Hardcoded exact names from logs/screenshots that might not be caught by simple Find if they are unique
    # But actually, Find(name) finds substrings? NO. It finds exact match.
    # We rely on the fact that often we named them exactly "MatTestCube" or "MatTestCube_123..."
    # If they have unique timestamps, we can't find them easily without a generic "FindAll" skill.
    # However, if we named them "MatTestCube" exactly multiple times, this loop fixes it.
    # If they have unique names like MatTestCube_1768..., we need to know the ID.
    
    # Strategy: Try to delete the prefixes as exact names (in case) and known garbage.
    # Since we can't wildcard search in v1.3 without writing a C# script, we do our best.
    
    exact_names = [
        "URP_Cube_1768748308", "URP_Cube_1768748223", "TestPrefab",
        "Visual_Verification_Cube", "SkillTest_1768744...", # Guessing
    ]
    
    # Add the prefixes as potential exact matches to loop on
    candidates = prefixes + exact_names
    
    for name in candidates:
        print(f"  Sweeping for '{name}'...")
        max_attempts = 20 # Safety break
        while max_attempts > 0:
            res = call_skill('gameobject_delete', name=name)
            if not res.get('success'):
                # Unity couldn't find it, so we are done with this name
                break
            print(f"    Deleted one instance of '{name}'")
            max_attempts -= 1

    # Also clean strict rainbow names again
    for _, (cname, _) in enumerate(RAINBOW):
        fullname = f"Rainbow_Cube_{cname}"
        while True:
            res = call_skill('gameobject_delete', name=fullname)
            if not res.get('success'): break

def create_rainbow():
    print("Creating Rainbow Cubes...")
    
    for i, (color_name, rgb) in enumerate(RAINBOW):
        cube_name = f"Rainbow_Cube_{color_name}"
        
        # 1. Create Cube
        x = START_X + (i * SPACING)
        print(f"[{i+1}/7] Creating {cube_name} at ({x}, {Y_POS}, {Z_POS})")
        call_skill('gameobject_create', name=cube_name, primitiveType='Cube', x=x, y=Y_POS, z=Z_POS)
        
        # 2. Create Material (URP Lit) & Assign
        mat_name = f"Mat_Rainbow_{color_name}"
        mat_path = f"Assets/Materials/Rainbow/{mat_name}.mat"
        
        call_skill('material_create', name=mat_name, savePath=mat_path)
        call_skill('material_assign', name=cube_name, materialPath=mat_path)
        
        # 3. Set Color (On the OBJECT's renderer)
        # URP Lit uses _BaseColor. Standard uses _Color.
        # We need to target the CUBE, not the asset path.
        print(f"  Setting color for {cube_name}...")
        call_skill('material_set_color', name=cube_name, r=rgb[0], g=rgb[1], b=rgb[2], propertyName="_BaseColor")
        # Fallback for compatibility or if auto-upgrade happened
        call_skill('material_set_color', name=cube_name, r=rgb[0], g=rgb[1], b=rgb[2], propertyName="_Color")

if __name__ == "__main__":
    clean_scene()
    create_rainbow()
    print("\nDone! Look at the rainbow!")
