<!-- usk-eval prompt; task OC5; lang zh-CN -->
R1. 资产 `{prefab}` 是场景对象 `{hud}` 的来源 prefab。请就地编辑这个 prefab 资产本身（在资产层面修改）：不要删除、替换或重新创建它，修改后它的 GUID 必须不变。
R2. 在该 prefab 中，把下列 6 个 RectTransform（都是 prefab 根节点的直接子节点）设置为下面的值；每一项写成 (x, y)，允许误差 0.001：{layout}
R3. prefab 根节点的直接子节点顺序（sibling index 从 0 起）必须为：{order}。只重排这 6 个节点，不要改变任何节点的父子关系。
R4. 节点 `Notice` 在 prefab 中必须处于激活状态（activeSelf 为 true）。
R5. 节点 `TopBar` 上的 HorizontalLayoutGroup：spacing 为 {spacing}，padding 的 left、right、top、bottom 分别为 {padL}、{padR}、{padT}、{padB}。
R6. 场景对象 `{hud}` 必须保持为该 prefab 的同一个已连接实例（不解包、不替换、不重命名），并且它现有的覆盖（overrides）必须原样保留：不要在这个实例或它的子物体上新增覆盖，也不要 apply 或 revert 它的覆盖。
R7. 除上述要求外，不要修改该 prefab 中的其他任何内容（节点、组件、其他属性值），也不要修改场景中的其他对象。
R8. 在答案中报告该 prefab 资产的路径，以及修改后 prefab 根节点直接子节点的名称顺序。
