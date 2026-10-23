### cinemachine_create_vcam
Create a new Virtual Camera.
**Parameters:**
- `name` (string): Name of the VCam GameObject.
- `folder` (string): Parent folder path (default: "Assets/Settings").

Example (with `cinemachine_set_targets`: a vcam that follows and looks at the player):

```python
import unity_skills

unity_skills.call_skill("cinemachine_create_vcam", name="PlayerCam")
unity_skills.call_skill("cinemachine_set_targets", vcamName="PlayerCam", followName="Player", lookAtName="Player")
```
