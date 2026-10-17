<!-- usk-eval prompt; task T3; lang en -->
R1. `{planks}` contains 5 objects that are all named `Plank`. Number them as planks 1 to 5 in ascending order of world X (the hierarchy order has nothing to do with the numbering). Add a Rigidbody (mass 2) and a HingeJoint to every plank; each plank must end up with exactly one Rigidbody and exactly one HingeJoint.
R2. The connectedBody of plank 1's HingeJoint is the Rigidbody of `{anchor}`; the connectedBody of each of planks 2 to 5 is the Rigidbody of the plank numbered one lower.
R3. The axis of every HingeJoint is (0, 0, 1).
R4. Create a new physic material and save it as `{mat}`, with dynamic friction 0.8, static friction 0.9 and bounciness 0. It must be the only physic material asset under A; do not leave any other physic material asset behind.
R5. Assign this physic material only to the BoxCollider of each of the 5 planks; do not change any other collider.
R6. Set the Layer of the 5 planks themselves (not their children) to `{l1}` (layer {i1}).
R7. In the physics layer collision matrix, disable collision between `{l1}` and `{l2}` (layer {i2}). This is the only project setting this task explicitly allows you to change: do not change the collision setting of any other layer pair, and do not add, remove or rename any layer.
R8. Apart from the above, do not modify any other object, component or asset: the `Plank` under `{spare}` is not one of the planks, and `{anchor}` and `{planks}` themselves and their child order must not change either; the names, tags, active states, positions, rotations, scales and children of the planks, and the order of their existing components, stay unchanged.
R9. In the answer, report the number and instanceId of each of the 5 planks, the asset path of the physic material, and the layer index of the layer the planks are on.
