<!-- usk-eval prompt; task T2; lang en -->
R1. Directly under `{canvas}`, create the UGUI button `StartButton` with RectTransform, Image and Button components; its text uses TextMeshPro (a TextMeshProUGUI component below the button) and reads `Start Game`.
R2. StartButton's RectTransform: anchorMin and anchorMax both (0.5, 0), pivot (0.5, 0), anchoredPosition (0, 120), size 320 × 80.
R3. The scene has no EventSystem yet: under `{ui}`, create an object named `EventSystem` with the EventSystem and StandaloneInputModule components; it must be the only EventSystem in the scene.
R4. The button's onClick has exactly two persistent listeners, in this order: the first calls GameObject.SetActive(false) on the `Menu` object tagged Finish; the second calls StartGame() on the `{cls}` component of `{gc}`. Both listeners are called at runtime only (RuntimeOnly).
R5. Do not modify any other objects or assets; create no Button other than StartButton and no Canvas.
R6. In Play Mode, after one click on the button the Menu tagged Finish must be inactive, the other Menus still active, and startCount of the `{cls}` component 1; no error or exception may be logged while in Play Mode.
R7. In the answer, report the instanceId of StartButton, the instanceId of the EventSystem object and, in order, for each listener the instanceId of the GameObject it calls into and the method name.
