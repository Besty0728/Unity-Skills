---
name: unity-manual-gameobject
description: Manually create GameObjects, organize the Hierarchy, and adjust Transforms using Unity Editor UI. For clicks-and-drag workflows that do not need REST automation.
---

## Triggers
- Creating a single GameObject by hand
- Parenting or reordering objects in the Hierarchy
- Moving, rotating, or scaling objects via the Inspector or Scene view
- Telling a user the exact menu path when they want to do it themselves

# Manual GameObject Tasks

Step-by-step instructions for common GameObject operations inside the Unity Editor. Paths assume the default English Unity UI; translate the labels into the user's Unity language when replying.

## When to guide manually vs automate

Guide manually when the user can finish the task in **3 clicks or fewer**: one-off creation, a single drag in the Hierarchy, or a quick transform tweak.

Automate when the task involves **bulk creation, precise numeric values across many objects, programmatic hierarchy layout, or searching/renaming many items** — use the [gameobject](../gameobject/SKILL.md) REST module instead.

## Create a GameObject

#### Step 1 — Open the creation menu
Use any of these equivalent paths:
- Main menu `GameObject`
- Right-click in the Hierarchy window
- Press `Ctrl + Shift + N` for a blank GameObject

#### Step 2 — Pick a primitive or empty object
- `GameObject > Create Empty`
- `GameObject > 3D Object > Cube`
- `GameObject > 3D Object > Sphere`
- `GameObject > 3D Object > Plane`
- `GameObject > Light`, `GameObject > Camera`, etc.

The new object appears in the Hierarchy as a root object.

## Create a child GameObject

#### Step 1 — Select the parent in the Hierarchy
Click the parent GameObject.

#### Step 2 — Create the child
- `GameObject > Create Empty Child` (shortcut `Alt + Shift + N`)
- Or `GameObject > 3D Object > Cube`

The child is indented under the parent in the Hierarchy.

## Organize the Hierarchy with drag-and-drop

#### Step 1 — Reparent a GameObject
Drag the object onto another object in the Hierarchy. A blue highlight indicates the new parent.

#### Step 2 — Reorder siblings
Drag an object up or down between siblings. A horizontal blue line shows the drop position.

#### Step 3 — Make an object a root
Drag it into an empty area of the Hierarchy above or below other root objects.

## Adjust a Transform in the Inspector

#### Step 1 — Select the GameObject
Select the object in the Hierarchy or Scene view.

#### Step 2 — Edit the Transform component
In the Inspector, change Position, Rotation, or Scale numerically.

Tips:
- Click and drag the label to scrub small values.
- Right-click a field and choose `Reset` to return to default.
- The gear icon on the component lets you `Reset`, `Copy Component`, or `Paste Component Values`.

## Adjust a Transform in the Scene view

#### Step 1 — Select the GameObject
Click it in the Scene view or Hierarchy.

#### Step 2 — Activate a transform tool
- `Q` — Pan (Hand)
- `W` — Translate (move)
- `E` — Rotate
- `R` — Scale
- `T` — Rect Transform (for UI objects)

#### Step 3 — Use the gizmo
Drag the red, green, or blue axis arrows to move along one axis. Drag the square planes to move along two axes. Hold `Ctrl` to snap; hold `Shift` for precision or to enable the enhanced transform menu depending on Unity version.

#### Step 4 — Focus the camera
With the object selected, press `F` to frame the Scene view camera on it.

## Rename or duplicate

#### Rename
- Select the object and press `F2`.
- Or right-click in the Hierarchy and choose `Rename`.

#### Duplicate
- Select the object and press `Ctrl + D`.
- Or right-click in the Hierarchy and choose `Duplicate`.

## Automate instead?

If the user wants to create or modify many GameObjects, parent them programmatically, or apply exact transforms across many objects, switch to the REST module:
- [gameobject](../gameobject/SKILL.md)
