---
name: unity-manual-material
description: Manually create and edit Materials and assign them to objects using Unity Editor UI. For one-off material tweaks that do not need REST automation.
---

## Triggers
- Creating a new Material asset
- Changing a material color or shader
- Dragging a material onto a MeshRenderer or Mesh

# Manual Material Tasks

Step-by-step instructions for Material operations inside the Unity Editor. Paths assume the default English Unity UI; translate the labels into the user's Unity language when replying.

## When to guide manually vs automate

Guide manually when the user can finish the task in **3 clicks or fewer**: creating one Material, picking a color, or assigning a Material to one object.

Automate when the task involves **bulk material creation, batch property edits, shader keyword toggles across many materials, or render-queue changes** — use the [material](../material/SKILL.md) REST module instead.

## Create a Material asset

#### Step 1 — Open the create menu in the Project window
Right-click in the Project window, then choose **Create > Material**.

Alternative path:
- Main menu `Assets > Create > Material`

#### Step 2 — Name the Material
Type the new name and press Enter.

## Change the Material color

#### Step 1 — Select the Material
Click the Material asset in the Project window.

#### Step 2 — Edit the Base Color / Albedo
In the Inspector:
- For URP/HDRP Lit shaders, click the **Base Map** color swatch (or **Base Color** field).
- For Built-in Standard, click the **Albedo** color swatch.
- Pick a color in the color picker and close it.

## Change the shader

#### Step 1 — Select the Material
Select the Material asset in the Project window.

#### Step 2 — Change Shader
At the top of the Inspector, click the **Shader** dropdown and choose a shader (e.g., `Universal Render Pipeline > Lit`, `Standard`, `Unlit/Color`).

The available shader list depends on the active render pipeline and installed packages.

## Assign a Material to an object

#### Step 1 — Drag the Material
Click and drag the Material asset from the Project window.

#### Step 2 — Drop it onto the object
Drop the Material onto:
- The object in the Scene view.
- The object's entry in the Hierarchy.
- The Material slot in the Mesh Renderer component in the Inspector.

## Create and assign a Material quickly

#### Step 1 — Drag the Material
Drag a Material asset from the Project window.

#### Step 2 — Drop it onto a blank Project folder
Hold `Ctrl` (or follow the on-screen modifier hint) while dropping if you want to duplicate the Material instead.

## Automate instead?

If the user wants to edit many Materials, set exact numeric properties, swap shaders in bulk, or assign Materials to many objects, switch to the REST module:
- [material](../material/SKILL.md)
