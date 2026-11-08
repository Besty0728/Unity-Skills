### cinemachine_create_vcam
Create a new Virtual Camera.
**Parameters:**
- `name` (string): Name of the VCam GameObject.
- `folder` (string): Parent folder path (default: "Assets/Settings").

Example (with `cinemachine_set_targets`: a vcam that follows and looks at the player):

```bash
curl -s "http://localhost:<port>/skill/cinemachine_create_vcam?expectProject=<project>" -d '{"name":"PlayerCam"}'
curl -s "http://localhost:<port>/skill/cinemachine_set_targets?expectProject=<project>" -d '{"vcamName":"PlayerCam","followName":"Player","lookAtName":"Player"}'
```
