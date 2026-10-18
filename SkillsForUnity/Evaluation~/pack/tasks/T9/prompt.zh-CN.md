<!-- usk-eval prompt; task T9; lang zh-CN -->
R1. 脚本 `{path}`（类 `{cls}`）挂在 `{guards}` 下的 Guard_1、Guard_2、Guard_3 上，每个 Guard 都设置了各自的 speed、waypoint 和 laps。请在该脚本中把字段 `speed` 改名为 `moveSpeed`、把字段 `waypoint` 改名为 `patrolTarget`；类名、其他字段以及所有字段的类型和默认值保持不变。
R2. 以旧字段名保存的数据必须继续加载到改名后的字段中：改名后三个 Guard 的 moveSpeed 仍是各自原来的 speed 值，patrolTarget 仍引用各自原来的 waypoint，laps 也不变；以后打开的、按旧字段名保存的场景或预制体同样如此。
R3. 不要通过技能给 Guard 设置 moveSpeed、patrolTarget、speed 或 waypoint：这些值必须在改名过程中自行保留下来。
R4. 直接修改这个脚本文件本身：不要删除、重建、移动或重命名该文件（它的 GUID 保持不变）；不要修改其他脚本；不要新增任何其他代码文件（包括编辑器脚本）。
R5. 不要修改其他对象和资产。
R6. 进入 Play 模式运行场景时不得出现错误或异常日志，并且运行时每个 Guard 的 moveSpeed、patrolTarget 和 laps 仍是 R2 所述的值。
R7. 在答案中报告该脚本的资产路径，并对每个 Guard 报告它的 instanceId、moveSpeed 值，以及它的 patrolTarget 所引用的 GameObject 的 instanceId。
