
import sys
import json
sys.path.insert(0, 'c:/Users/Betsy/.gemini/antigravity/skills/unity-skills/scripts')
from unity_skills import call_skill

print("Creating URP Test Material...")
res = call_skill('material_create', name="URP_Test_Mat")
print(json.dumps(res, indent=2))
