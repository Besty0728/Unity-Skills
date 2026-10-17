<!-- usk-eval prompt; task T1; lang zh-CN -->
R1. 在 `{scripts}` 下创建下面两个 C# 脚本，文件名与类名一致，内容逐字照抄，不要增删任何内容。
   `{cfg_class}.cs`：{cfg_src}
   `{ai_class}.cs`：{ai_src}
R2. 两个脚本编译通过后，创建 `{cfg_class}` 类型的资产 `{asset}`：fireRate = 2.5，burstCount = 3，projectilePrefab 指向 `{prefab}`。
R3. `{turrets}` 下有三个同名的 Turret；只给其中世界坐标 X 最大的那个添加 `{ai_class}` 组件：config 指向 R2 的资产，target 指向 `{range}` 下标签为 Respawn 的那个 Dummy 的 Transform，turnSpeed = 90。
R4. 除这两个脚本外，不要新增或修改任何代码文件（包括编辑器脚本）。
R5. 不要修改其他对象和资产。
R6. 进入 Play 模式运行场景时不得出现错误或异常日志，并且 `{ai_class}` 组件在运行时的 config、target、turnSpeed 仍是上述设置。
R7. 在答案中报告两个脚本的资产路径、配置资产的路径、添加了组件的 Turret 的 instanceId，以及 target 所指 Dummy 的 instanceId。
