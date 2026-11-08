### camera_sceneview_screenshot
Capture the **editor Scene View** (the developer's editing viewport — can overlook the whole scene incl. off-camera objects). Distinct from `scene_screenshot` (Game View / player camera) and `camera_screenshot` (one Game Camera). By default captures the full Scene View incl. grid/gizmos/selection (on-screen read); auto-falls back to a clean offscreen render if the editor build lacks the internal API. The Scene View window must be open and visible for the overlay capture.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filename | string | No | "sceneview.png" | Bare filename only (no path separators); saved under `Assets/Screenshots/` |
| includeOverlays | bool | No | true | True = full Scene View with grid/gizmos/selection (falls back to a clean render if unsupported); false = clean offscreen scene render only |
| returnImage | bool | No | false | Also return the PNG as base64 in the response (`imageBase64`), for clients without filesystem access |
| maxDimension | int | No | 1280 | Only used when `returnImage=true`; downscales the returned image (not the saved file) so its longer edge is ≤ this value. Clamped to 256–4096 |

**Returns:** `{ success, path, width, height, mode, note }` — `mode` is `"screen_with_overlays"` or `"offscreen_clean"` — plus `{ imageBase64, imageWidth, imageHeight, imageBytes }` when `returnImage=true`. If the base64 payload would exceed 8MB, the skill returns an error asking for a smaller `maxDimension` — the file at `path` is still saved.

**returnImage usage tip:** a local agent that can read files (e.g. Claude Code against a local Unity Editor) should generally omit `returnImage` and just read the PNG at `path` — it's cheaper on tokens. Use `returnImage=true` for remote/MCP clients that have no filesystem access to the Unity project.
