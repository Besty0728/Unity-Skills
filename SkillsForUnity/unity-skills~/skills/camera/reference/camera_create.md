### camera_create
Create a new Game Camera.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | "New Camera" | Name of the new camera GameObject |
| x | float | No | 0 | Position X |
| y | float | No | 1 | Position Y |
| z | float | No | -10 | Position Z |
| addAudioListener | bool | No | false | Also attach an `AudioListener` component |

**Returns:** `{ success, name, instanceId }`
