---
name: unity-manual-component
description: Manually add, configure, reorder, and copy components on GameObjects using Unity Editor UI. For one-off Inspector workflows that do not need REST automation.
---

## Triggers
- Adding a single component to a GameObject
- Tuning component fields in the Inspector
- Reordering components on one object
- Copying component values to another object

# Manual Component Tasks

Step-by-step instructions for component operations inside the Unity Editor. Paths assume the default English Unity UI; translate the labels into the user's Unity language when replying.

## When to guide manually vs automate

Guide manually when the user can finish the task in **3 clicks or fewer**: adding one common component, tweaking a few exposed fields, or copying a single component value.

Automate when the task involves **adding components to many objects, batch-configuring serialized fields, reflecting over property names, or enabling/disabling many components at once** — use the [component](../component/SKILL.md) REST module instead.

## Add a component

#### Step 1 — Select the GameObject
Select the target object in the Hierarchy or Scene view.

#### Step 2 — Open Add Component
At the bottom of the Inspector, click **Add Component**.

#### Step 3 — Choose the component
- Type a name in the search box (e.g., `Rigidbody`).
- Or navigate the category list (`Physics > Rigidbody`).
- Click the component to add it.

Alternative paths:
- `Component > Physics > Rigidbody`
- `Component > Mesh > Mesh Renderer`
- `Component > Audio > Audio Source`

## Configure common fields

#### Step 1 — Expand the component in the Inspector
Click the component header if it is collapsed.

#### Step 2 — Edit exposed fields
- Drag sliders or numeric fields.
- Click color swatches to open the color picker.
- Drag asset references (Materials, AudioClips, Prefabs) into object fields.
- Toggle checkboxes for bool fields.
- Choose enum values from dropdowns.

Tip: Hover a field label for the tooltip. The field name shown in the Inspector is the human-readable label, not always the exact serialized property name.

## Reorder components

#### Step 1 — Drag the component header
In the Inspector, click and drag the grey header bar of a component.

#### Step 2 — Drop it into the desired position
A horizontal blue line indicates the drop position. Release to reorder.

Note: The Transform component is always first and cannot be moved.

## Copy and paste component values

#### Step 1 — Copy the source component
Click the gear icon on the component header and choose **Copy Component**.

#### Step 2 — Paste onto the target
Select the destination GameObject, click the gear icon on the matching component (or Add Component if it is missing), and choose **Paste Component Values**.

To paste the whole component including its type:
- Choose **Paste Component As New** from the gear menu.

## Remove a component

#### Step 1 — Open the component context menu
Click the gear icon on the component header.

#### Step 2 — Remove
Choose **Remove Component**. Unity may warn about missing script references if other systems depend on it.

## Automate instead?

If the user wants to add or configure components on many objects, batch-modify serialized properties, or copy values across many objects, switch to the REST module:
- [component](../component/SKILL.md)
