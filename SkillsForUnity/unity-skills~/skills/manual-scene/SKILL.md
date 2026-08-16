---
name: unity-manual-scene
description: Manually navigate, save, and manage scenes using Unity Editor UI. For simple Scene view workflows that do not need REST automation.
---

## Triggers
- Navigating the Scene view with the mouse and keyboard
- Saving the current scene
- Switching between loaded scenes or additive scene workflows
- Telling a user how to frame the camera on an object

# Manual Scene Tasks

Step-by-step instructions for Scene view navigation and scene management inside the Unity Editor. Paths assume the default English Unity UI; translate the labels into the user's Unity language when replying.

## When to guide manually vs automate

Guide manually when the user can finish the task in **3 clicks or fewer**: orbiting the view, saving, setting a scene active, or loading one additive scene.

Automate when the task involves **querying scene contents, batch loading/unloading, dependency analysis, screenshot capture, or switching scenes programmatically across many scenes** — use the [scene](../scene/SKILL.md) REST module instead.

## Navigate the Scene view

#### Step 1 — Orbit
Hold `Alt` and drag with the left mouse button to orbit around the current pivot.

#### Step 2 — Pan
Hold `Alt` and drag with the middle mouse button (or scroll wheel pressed) to pan.

#### Step 3 — Zoom
Scroll the mouse wheel to zoom in and out.

#### Step 4 — Focus on an object
Select a GameObject and press `F` to frame the Scene view on it.

## Toggle 2D / orthographic view

#### Step 1 — Click the 2D toggle
In the Scene view toolbar, click the **2D** button to switch between perspective and orthographic views (relevant for 2D projects or flat alignment).

#### Step 2 — Change projection with a hotkey
Press the scene-view toolbar shortcut `Right-click > Perspective/Orthographic` or use the dropdown on the view axis gizmo, depending on Unity version.

## Save the current scene

#### Step 1 — Save
Press `Ctrl + S`.

Alternative path:
- Main menu `File > Save`

#### Step 2 — Save as a new file
Choose `File > Save As...` and pick a location under `Assets/`.

## Open a different scene

#### Step 1 — Open the scene asset
Double-click a `.unity` scene file in the Project window.

Alternative path:
- Main menu `File > Open Scene`.

#### Step 2 — Confirm save
If the current scene has unsaved changes, Unity prompts to save or discard.

## Additive scene workflow

#### Step 1 — Open the first scene
Double-click the first scene asset.

#### Step 2 — Add a scene additively
Right-click another scene asset in the Project window and choose **Open Scene Additive**.

Alternative path:
- Drag the scene asset into the Hierarchy to add it additively.

#### Step 3 — Manage loaded scenes
Open the **Scenes in Build** list from `File > Build Profiles` (or `File > Build Settings` in older Unity versions) to set which scenes are included in builds and their order.

## Automate instead?

If the user wants to load or unload scenes programmatically, query scene hierarchy data, capture scene screenshots, or analyze scene dependencies, switch to the REST module:
- [scene](../scene/SKILL.md)
