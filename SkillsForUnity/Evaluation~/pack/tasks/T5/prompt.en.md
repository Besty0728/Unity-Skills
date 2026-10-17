<!-- usk-eval prompt; task T5; lang en -->
R1. Create the prefab variant `{variant}` based on `{base}`.
R2. In the variant asset, set the mass of the root Rigidbody to 50.
R3. `{row}` has three `Crate` objects with the same name, all instances of `{base}`. Replace the one whose world rotation Y is about 75° with a new instance of `{variant}`, and delete the old Crate.
R4. The new instance must be a prefab instance of `{variant}`, be named `Crate`, still be a child of `{row}`, and have the same sibling index the replaced Crate had.
R5. The new instance has the same world position, world rotation and scale (localScale) as the replaced Crate had.
R6. Do not apply the overrides of any other Crate to the prefab; `{base}` must not change in any way.
R7. Apart from the changes above, do not modify other objects or assets: the properties, overrides and sibling order of the other two Crates and of the other objects under `{row}` stay unchanged.
R8. In the answer, report the asset path of the variant, the instanceId of the new instance, and its sibling index (counting from 0).
