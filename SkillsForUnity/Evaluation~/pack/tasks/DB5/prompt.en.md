<!-- usk-eval prompt; task DB5; lang en -->
R1. Under `{scripts}`, create the script `{cls}.cs` declaring `public class {cls} : MonoBehaviour` with a serialized field `label` of type `UnityEngine.UI.Text` (public, or marked [SerializeField]) and a public method `OnClick()` without parameters.
R2. Each call of `OnClick()` adds 1 to a count that starts at 0 and writes the new count as decimal digits into `label.text` (after two calls the label shows 2).
R3. Attach exactly one `{cls}` component to `{button}` and point its `label` field at the Text component of `{label}`.
R4. Add a persistent listener (one serialized into the scene and visible in the Inspector, not a runtime AddListener) to the `onClick` event of the Button component of `{button}` that calls `OnClick` of the `{cls}` on that same object; that event has only this listener, with call state RuntimeOnly or EditorAndRuntime.
R5. Do not add a listener to the onClick of `{other}`, do not modify other objects, keep the label showing 0 in the scene, and add no code file other than this script (editor scripts included).
R6. When the scene runs in Play Mode no error or exception may be logged, and after the button's onClick has been triggered twice, the label shows 2.
R7. In the answer, report the asset path of the script, the instanceId of the button object carrying the component, and the instanceId of the object whose Text component the label field points at.
