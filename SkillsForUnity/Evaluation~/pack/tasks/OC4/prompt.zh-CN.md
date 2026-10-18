<!-- usk-eval prompt; task OC4; lang zh-CN -->
R1. 审计 `{root}` 下全部 240 个原始 MeshRenderer，包括每个未激活分支和未激活对象。按原始状态分类每个材质槽，并在整个层级中完成下述替换；查询仅返回有限列表或样本时，也必须覆盖全部对象。
R2. 对原始已赋值且材质着色器名称恰好为 `Standard` 的每个槽，替换为已有的 Fallback 材质 `{material}`。必须使用这一确切资产，也包括槽 0 之后的所有槽。
R3. 把原始材质引用丢失的每个槽替换为同一 Fallback 资产。引用丢失（`referenceState` = `missing`）与真正为空（`referenceState` = `none`）的槽都可能读作 `null`，必须分别分类。
R4. 不得填充或移除任何原始为空的槽：它必须保持为空。原始已赋值且着色器非 `Standard` 的每个槽，必须在原索引保留原材质身份，包括已使用 Fallback 的槽。
R5. 不得新增、删除或替换对象或组件，不得改变材质数组的长度或顺序，不得改变名称、父子关系、兄弟顺序、标签、层、激活状态、Transform 或允许的材质槽替换之外的任何组件属性。
R6. 不得修改、移动或删除任何已有材质资产，包括 Fallback 和 Standard 材质；它们的 GUID 必须保持不变。应修复 renderer 的引用，而非编辑材质的着色器。
R7. 报告原始状态的整数计数：`renderers` = 已审计的 renderer 数，`slots` = 材质槽数，`standardSlots` = 着色器恰好为 `Standard` 的已赋值槽数，`missingSlots` = 引用丢失槽数，`emptySlots` = 真正为空的槽数，`otherSlots` = 着色器非 `Standard` 的已赋值槽数（包括 Fallback）。按槽计数，而非不同材质资产数；四种槽分类互不重叠，其总和为 `slots`。
