<!-- usk-eval prompt; task OC5; lang en -->
R1. The asset `{prefab}` is the source prefab of the scene object `{hud}`. Edit this prefab asset itself, in place (at the asset level): do not delete, replace or recreate it; its GUID must be unchanged afterwards.
R2. In that prefab, set the following 6 RectTransforms (all direct children of the prefab root) to the values below; every item is written as (x, y), tolerance 0.001:{layout}
R3. The direct children of the prefab root must be in this order (sibling index from 0): {order}. Only reorder these 6 nodes; do not change any node's parent.
R4. The node `Notice` must be active in the prefab (activeSelf true).
R5. The HorizontalLayoutGroup on the node `TopBar`: spacing {spacing}, and padding left, right, top, bottom of {padL}, {padR}, {padT}, {padB} respectively.
R6. The scene object `{hud}` must remain the same connected instance of that prefab (not unpacked, replaced or renamed), and the overrides it already has must be kept as they are: do not add overrides on this instance or its children, and do not apply or revert its overrides.
R7. Apart from the above, do not change anything else in the prefab (nodes, components, other values), and do not modify any other object in the scene.
R8. In the answer, report the prefab asset's path and the names of the prefab root's direct children after the edit, in order.
