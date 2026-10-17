<!-- usk-eval prompt; task OC3; lang zh-CN -->
R1. 在文件夹 `{enemies}`（包括其所有子文件夹）下的预制体中，凡是带有 `{lootType}` 组件（它的字段 `table` 引用一个 `{tableType}` 资产）的预制体，都要按 R2–R4 设置 `table`；没有 `{lootType}` 组件的预制体不要动。
R2. 一个预制体的等级由它上方最近的、名为 `Common`、`Rare` 或 `Elite` 的文件夹决定（更深的子文件夹，例如 `Rare/Bosses`，沿用所在等级）。该预制体上第一个 `{lootType}` 组件（componentIndex 0）的 `table` 必须引用对应等级的资产：`{common}`、`{rare}` 或 `{elite}`，并且必须是这个路径上的那个资产。
R3. 若一个预制体带有多于一个 `{lootType}` 组件，第一个仍按 R2 设置，其余每一个的 `table` 都引用 `{bonus}`。
R4. 预制体变体（以另一个预制体为基础的预制体）无论位于哪个文件夹都不得修改：它们通过基础预制体继承 `table`，只在基础预制体上设置。
R5. 已经引用了某个资产的 `table` 也在范围内：引用错误的必须改正，引用正确的可以保留。除这些 `{lootType}` 组件的 `table` 字段外，任何预制体、资产和其他字段都不得改动（包括 `dropChance`、各个 `{tableType}` 资产以及 `{enemies}` 之外的资产）；`{scripts}` 下的两个脚本是题目环境的一部分，同样不得修改。
R6. 在答案中列出你设置或确认过的每个 `{lootType}` 组件：预制体路径、componentIndex 和它最终引用的资产路径；列出你未改动的变体预制体路径；并给出你引用的 `LootTable_Common` 资产的 GUID。
