<!-- usk-eval prompt; task DC7; lang zh-CN -->
R1. `{folder}` 下的脚本按程序集定义拆分，由于这些程序集之间缺少引用，工程目前无法编译。请修复程序集定义，使整个工程编译通过、没有任何编译错误。本题允许像 C# 脚本一样直接在 `{scripts}` 下新建和修改 .asmdef 文件；其他修改都要通过技能完成。
R2. 保留三个已有的程序集定义 `{coreAsmdef}`、`{dataAsmdef}` 和 `{playAsmdef}`：不得删除、移动或重命名它们，保持它们的 GUID 和程序集名称，除了往它们的 `references` 中添加条目（按程序集名称或写成 `GUID:<guid>`）之外不做任何改动。运行时脚本留在原来的文件夹中。
R3. 编辑器脚本 `{editorScript}` 必须离开运行时程序集 `{playAsm}`：它最终要归属于一个新建的、名为 `{editorAsm}` 的程序集定义，该 .asmdef 的 `includePlatforms` 只包含 "Editor"，且 `references` 中包含 `{coreAsm}`。可以把文件移动到该 .asmdef 管辖的文件夹中（移动会保留 GUID；不要删除后重建），也可以把该 .asmdef 放在能管辖文件当前所在文件夹的位置。不允许用 `#if UNITY_EDITOR` 或其他预处理指令把编辑器代码留在运行时程序集里。
R4. 不得修改任何已有 .cs 文件的内容（按 R3 移动编辑器脚本可以），也不要新增任何代码文件（包括编辑器脚本）；R1–R3 唯一需要新增的文件就是那个新的 .asmdef。
R5. 工程编译通过后，给 `{fighter}` 挂上 `{combatCls}` 组件（场景中只有这一个该组件）。
R6. 验收会进入 Play 模式：该组件的 `power` 字段在运行时必须为 {power}（它的 Awake 用 `{statsCls}.hp` = {hp} 乘以 `{tuningCls}.factor` = {factor} 得出），且运行期间不得出现错误或异常日志。
R7. 除了 R1–R5 要求的改动之外，不要修改 `{arena}` 以及其他任何对象和资产。
R8. 在答案中报告新建的 Editor .asmdef 文件的资产路径、修复后编辑器脚本的资产路径，以及挂载了 `{combatCls}` 组件的对象的完整层级路径。
