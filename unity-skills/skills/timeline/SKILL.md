# Timeline Skills

Create and modify Unity Timelines.

## Skills

### `timeline_create`
Create a new Timeline asset and Director instance.
**Parameters:**
- `name` (string): Name of the timeline/object.
- `folder` (string, optional): Folder to save asset (default: "Assets/Timelines").

### `timeline_add_audio_track`
Add an Audio track to a Timeline.
**Parameters:**
- `directorObjectName` (string): Name of the GameObject with PlayableDirector.
- `trackName` (string, optional): Name of the new track.

### `timeline_add_animation_track`
Add an Animation track to a Timeline, optionally binding an object.
**Parameters:**
- `directorObjectName` (string): Name of the GameObject with PlayableDirector.
- `trackName` (string, optional): Name of the new track.
- `bindingObjectName` (string, optional): Name of the GameObject to bind (animator).
