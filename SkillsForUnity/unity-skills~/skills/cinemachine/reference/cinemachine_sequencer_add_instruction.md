### cinemachine_sequencer_add_instruction
Add a child camera instruction to a Sequencer/BlendList camera.
**Parameters:**
- `sequencerName` (string, optional): Sequencer camera name.
- `sequencerInstanceId` (int, optional): Sequencer Instance ID.
- `sequencerPath` (string, optional): Sequencer hierarchy path.
- `sequencerEntityId` (string, optional): Sequencer entity ID (preferred; the only id on Unity 6000.4+).
- `childCameraName` (string, optional): Child VCam name.
- `childInstanceId` (int, optional): Child VCam Instance ID.
- `childPath` (string, optional): Child VCam hierarchy path.
- `childEntityId` (string, optional): Child VCam entity ID (preferred; the only id on Unity 6000.4+).
- `hold` (float): Duration to hold on this child in seconds (default `2`).
- `blendStyle` (string): Blend style to use entering this child (default `EaseInOut`).
- `blendTime` (float): Blend duration in seconds (default `2`).

Provide at least one identifier for each of sequencer and child camera.
