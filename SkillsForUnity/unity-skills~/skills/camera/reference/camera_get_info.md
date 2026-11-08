### camera_get_info
Get **the editor Scene View viewport camera's** position and rotation — the developer's viewport, *not* a Camera component in the scene. It takes no target parameters for exactly that reason. For a Game Camera's state use `camera_get_properties` (or `camera_list` to find one); if you just created a camera with `camera_create` and want to confirm its transform, `camera_get_info` will not show it.
**Parameters:** None.
