### camera_screenshot
Capture a screenshot from a Game Camera to file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| savePath | string | No | "Assets/screenshot.png" | File path to save the screenshot |
| width | int | No | 1920 | Screenshot width in pixels |
| height | int | No | 1080 | Screenshot height in pixels |
| name | string | No | null | Name of the camera GameObject |
| entityId | string | No | null | Entity ID of the camera GameObject (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |
| returnImage | bool | No | false | Also return the PNG as base64 in the response (`imageBase64`), for clients without filesystem access |
| maxDimension | int | No | 1280 | Only used when `returnImage=true`; downscales the returned image (not the saved file) so its longer edge is ≤ this value. Clamped to 256–4096 |

**Returns:** `{ success, path, width, height }`, plus `{ imageBase64, imageWidth, imageHeight, imageBytes }` when `returnImage=true`. If the base64 payload would exceed 8MB, the skill returns an error asking for a smaller `maxDimension` — the file at `path` is still saved.
