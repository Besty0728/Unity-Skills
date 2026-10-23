### cinemachine_target_group_add_member
Add or update a member in a TargetGroup.
**Parameters:**
- `groupName` (string): Name of the TargetGroup.
- `groupInstanceId` (int, optional): TargetGroup instance ID.
- `groupPath` (string, optional): TargetGroup hierarchy path.
- `groupEntityId` (string, optional): TargetGroup entity ID (preferred; the only id on Unity 6000.4+).
- `targetName` (string): Name of the member GameObject.
- `targetInstanceId` (int, optional): Member GameObject instance ID.
- `targetPath` (string, optional): Member GameObject hierarchy path.
- `targetEntityId` (string, optional): Member GameObject entity ID (preferred; the only id on Unity 6000.4+).
- `weight` (float): Member weight (default 1).
- `radius` (float): Member radius (default 1).
