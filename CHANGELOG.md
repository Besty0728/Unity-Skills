# Changelog

All notable changes to **UnitySkills** will be documented in this file.

## [2.1.2] - 2026-07-13

### Fixed

- **CI EditMode 测试在纯净工程下必挂两处** — `NewCapabilitiesTests` 首次纳入编译矩阵（tag `v2.1.1`）即全线红。两处均为测试隔离缺陷、与产品/运行时代码无关：
  - `BatchDiff_DeleteExistingObject_ReportsRemoved` — fixture 未强制 Bypass 模式，`gameobject_delete`（never-in-semi）在默认 `auto` 模式下被门禁拦为 `MODE_FORBIDDEN` 空操作，删除从未发生、`sceneDiff.removed` 恒为 0。此前仅因作者机器 EditorPrefs 残留 Bypass 才通过，CI 空工程默认 `auto` 即暴露。现于 SetUp/TearDown 保存并切换到 `SkillsOperatingMode.Bypass`（对齐 `DeepInspectorSkillTests` 等同类 fixture）。
  - `BuildPlayer_ExplicitScenesValidate` — 硬编码假设 `Assets/Scenes/SampleScene.unity` 存在于磁盘，CI 临时空工程无此文件。改为自建自清理临时场景（`Assets/Temp/BuildPlayerValidation_<guid>.unity`），不再依赖宿主工程布局。

### Changed

- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.1.2`。

## [2.1.1] - 2026-07-13

### Added

- **Analytics 面板按时间窗删除遥测数据** — Analytics 标签页工具栏新增红色垃圾桶图标按钮，可按当前所选时间窗（`1h`/`24h`/`7d`/`all`）删除遥测记录：`all` 清空主日志 + 全部滚动文件，其他窗仅删除 `ts >= cutoff` 的窗内记录、窗外数据保留。服务层 `SkillTelemetryService.DeleteWindow` 在写锁内先 flush 队列再改写，防并发 `Record` 回写；删除后清空 analytics/recommendation 缓存并即时刷新面板。删除前弹确认框（带时间窗名），成功后提示已删/剩余条数。

### Changed

- **全部脚本追加制作者水印** — 为项目内全部 128 个 C# 脚本（`SkillsForUnity/**/*.cs`）与 Python 客户端 `unity_skills.py` 在文件末尾追加一行制作者标记注释（`Producer:Betsy`），以行注释形式置于文件最外层、不影响任何编译或运行逻辑；追加操作幂等（重复执行不会叠加）。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.1.1`。

## [2.1.0] - 2026-07-08

### Added

- **`build_player` 异步出包 Skill** — 基于 `BuildPipeline.BuildPlayer` 与现有 Job/Event 通道构建 Player，支持当前或显式 BuildTarget、场景列表、Development 与项目内安全输出路径，并返回 BuildReport 汇总。
- **`editor_play_capture` 运行时验证 Skill** — 自动进入 Play Mode、按时长采集去重后的 Error/Exception/Assert、按需截取 Game View、退出并生成 `healthy` 汇总；状态可跨 Domain Reload 恢复。
- **ScriptableObject 序列化属性路径写入三件套（+3 skills，共 729）** — 原 `scriptableobject_set` 是纯反射实现,只认顶层 public 字段的字面名,嵌套 serializable class、数组/List 元素、资产引用(ObjectReference)、private `[SerializeField]` 字段全部改不动——恰是游戏配置 SO 的主体,AI 只能退回现写 Editor 脚本。新增 `scriptableobject_set_serialized_property`(assetPath + propertyPath + value/valueAssetPath/valueObjectType),复用组件侧已有的 `SerializedPropertySkillUtility` 引擎,原生支持 `nested.field`、`items.Array.data[2]`、`items.Array.size` 扩容、按 assetPath 的资产引用赋值与置空、enum 按名、Color/Vector/Rect/Bounds 等全类型;配套 `scriptableobject_get_serialized_properties`(枚举全部属性路径,先看再改)与 `scriptableobject_set_serialized_property_batch`(BatchExecutor 批量)。实机全类型矩阵 9/9 通过并经 export 验证磁盘落盘。
- **`POST /skills/batch` 跨技能聚合执行端点** — AI 连续 N 个异构操作原需 N 轮工具调用 + N 次 HTTP。新端点一次请求顺序执行 ≤50 步(`{steps:[{skill,args}...], continueOnError}`),每步走完整单技能管线(校验/权限门/undo/审计,独立 undo 组);默认 fail-fast(失败步之后返回 `skipped`),`continueOnError=true` 跳过失败步,授权类响应(`MODE_RESTRICTED`/`CONFIRMATION_REQUIRED`)无论开关一律中断并在该步 error 里透传 grant token;`?mode=dryRun` 整序列预演(逐步校验、从不中断)。后续迭代补齐步间引用与事务:任意深度 `args` 里 `{"$ref":"$N.path"}` 取第 N 步 unwrap 后 result 的 `SelectToken`(解析失败该步 `SEMANTIC_INVALID`,dryRun 下做结构校验并记入 `refsValidated`);`?mode=transactional` 全有或全无,任一步失败经 Unity Undo 回滚已执行步并返回顶层 `status:"rolled_back"`,MutatesAssets 步标 `rollbackReliability:"partial"`(磁盘资产写入 Undo 无法完全回滚)。
- **HTTP 线程直答已缓存 GET(快速通道)+ ETag/304 协商缓存** — 全部请求(含已缓存的 schema/manifest)原先都排队等主线程唤醒(KeepAlive 50ms 轮询节奏),单请求 60-95ms。现 `GET /skills`/`/skills/schema` 命中服务端缓存字符串时由 HTTP 线程直接写回(零 Unity API,不违跨线程硬约束),实测 70ms→1.3ms;响应带内容哈希 `ETag`,`If-None-Match` 命中返 304 空体(etag 绑定缓存字符串引用,域重载/Refresh 后自动失效)。`/health` 保持走主线程队列(主线程存活探针语义)。
- **`GET /skills?brief=1` 目录层(第 0 层 schema)** — summary 层 143KB≈35K token 作为"每会话首取"认知税过高而典型会话只用少数技能。新增按模块分组的技能名目录(729 技能/49 模块,实测 19KB≈3.4K token,约 summary 的 1/10),配合 scoped schema 组成"目录锁模块→按模块拉签名"的低成本链路,典型会话认知成本 35K→约 10K token;融入 filtered 输出缓存,自动享受快速通道与 ETag。
- **Python 客户端:`search_skills()` 本地检索、registry 优先端口发现、ETag+磁盘缓存** — ① `search_skills(query, category, limit)` 在缓存的 summary 上做本地多词 AND 检索,143KB 留在磁盘、只有命中条目进上下文,CLI 支持 `--search`;② 端口发现原先盲扫 8090-8100,现优先读 registry.json 心跳新鲜条目直连(实测构造 0.029s),失败自动回退扫描,并砍掉构造期重复的 /health;③ summary/full schema 缓存落盘 `~/.unity_skills/cache/`(键含 instanceId 防多项目串缓存),跨进程命中 0.008s,TTL 过期带 `If-None-Match` 重验,304 续期——CLI 短进程模式从"每进程重拉 604KB"变为磁盘/304 复用。
- **`SerializedPropertySkillUtility` 引擎补 Gradient/AnimationCurve/flags enum 分支** — 共享引擎新增 `gradientValue`(colorKeys/alphaKeys/mode JSON)与 `animationCurveValue`(keys/pre/postWrapMode JSON)写入;flags enum 支持逗号/竖线分隔多名组合与 raw 数值(经 enumValueIndex↔intValue 往返求各常量底层值后 OR,兼容无 managed FieldInfo 的原生组件属性)。`component_set_serialized_property` 同步受益,组件侧回归通过。
- **`GET /events` 长轮询事件通道** — AI 原先只能靠轮询 `/compile/status` 感知编译结果。新增 500 条内存环形缓冲的事件通道,`since`/`cursor` 续拉、`timeout`(默认 25s,clamp 1–55)、`types` 过滤;事件类型含 `compilation_started/finished`(携带前 5 条 `firstErrors`)、`before/after_domain_reload`、`server_restored`(payload 带 `lastCompilation` 作重连锚点)、`playmode_changed`、`console_error`(20/s 节流 + `droppedSinceLast`)、`job_completed/failed`。`seq` 跨 domain reload 单调不回退,reload 丢弃在途事件时以 `dropped:true` 标记。长轮询整程跑在 ThreadPool 线程、不进主线程队列,挂起的长轮询不阻塞 `/health` 与其它请求。
- **`GET /compile/status` 编译结果反馈服务** — `script_*` 写入触发 Domain Reload、服务短暂 503/504 后,无需重读文件即可拿到上次编译结论:`{status, isCompiling, isUpdating, domainReloadPending, lastCompilation:{finishedAtUtc, durationMs, success, errorCount, warningCount, errors:[{file,line,column,message,assembly}], warnings, truncated}}`;`errors` 上限 200、`warnings` 上限 50。`lastCompilation` 经 `SessionState` 跨 reload 存活,服务恢复后 pass/fail 与精确错误行仍在。
- **写操作可选语义 sceneDiff(`?diff=1`)** — 单个写型技能追加 `?diff=1`,成功响应携带顶层 `sceneDiff` 概述场景增量:`{changed:[{target:{name,type,instanceId}, changes:[{path,before,after}], truncated}], added, removed, captureLimited}`,无需 follow-up 读取即可确认改动符合预期。属性路径带类型前缀(如 `Rigidbody.m_Mass`);捕获上限 20 对象 × 每对象 50 条变更(`captureLimited`/`truncated` 标记截断)。
- **面板新增 Analytics 标签页 + 遥测开关** — 服务端遥测(`Library/UnitySkillsTelemetry.jsonl`,记录技能名/agent/模式/成功与否/耗时,**不含参数或字段值**;1MB×4 滚动;写入走线程池 worker、路径主线程解析确保 flush 零 Unity API)此前只有 `GET /analytics` 一个 REST 出口,人类在面板看不到。新增第 4 个标签页 Analytics(复用同一聚合与 30s 缓存,仅在标签激活/切换时间窗/手动刷新时拉取,绝不落在 500ms live tick 上):汇总卡片 + 最常用/高失败率/最慢/错误码/最近错误五张表 + 时间窗下拉(1h/24h/7d/all)+ Reveal log。遥测默认开启,**设置抽屉 Runtime 组新增开关**(EditorPrefs `UnitySkills_TelemetryEnabled`)可随时关闭;数据仅存本地,绝不外传。
- **可配置编辑器快捷键 + 冲突检测** — 面板命令统一注册在 `ShortcutActions.cs`(Unity 官方 `[Shortcut]` 特性,出厂不绑定默认键,开箱零冲突),设置抽屉 Shortcuts 节自动枚举;用户绑定由 ShortcutManager profile 持久化(不写 EditorPrefs)。改绑时遍历全部已注册 shortcut 做冲突检测并提示冲突命令名;只读 profile 给出明确指引。首批可绑定:打开主面板 / 打开审计日志。
- **`editor_get_changes` 持久化编辑器变更日志(+1 skill)** — 读取常驻的编辑器变更流水(`Library/UnitySkills/editor_changes.jsonl`,500 条环形缓冲、跨 domain reload 存活),替代解析 `.unity` YAML。用于"用户趁 AI 不在时手改了工程 / 外部文件变动 / 请用户手动改编辑器后"的场景:返回比 `since` 新的场景结构/属性摘要与导入/删除/移动的资产路径;`types`(all/scene/file/undo/lifecycle)与 `source`(all/editor/manual/rest)过滤,`_restDepth` 标记区分 REST 触发与手动编辑。
- **`gameobject_set_sibling_index` 技能(+1 skill)** — 设置 GameObject 在父物体子级(或无父时场景根级)中的同级索引,支持 name/instanceId/path;index 越界自动 clamp 并在响应里以 `clamped=true` 标记。

### Changed

- **遥测反哺 `/skills/recommend`** — 最近 7 天有效执行样本达到门槛后，对长期高失败 Skill 做最多 3 分的有限降权并附失败率/耗时提示；权限和参数类客户端错误不计入，关闭遥测时保持纯语义排序。
- **开发中的示教录制宏系统在发布前被变更追踪取代(对已发布用户无影响)** — 本轮迭代曾引入 `macro_record_*` / `macro_save/list/get/delete/run` 一组示教录制→回放技能(`MacroRecorderService` 等约 2700 行),随后在发布前整体移除,改由被动、只读的 `editor_get_changes` 变更日志承接"感知编辑器改了什么"的诉求。这套宏系统自始至终只存在于开发分支、从未进入任何已发布版本(上一发布版为 2.0.9),故从 2.0.9 升级的用户不受影响。二者能力边界不同:变更日志只观测、不回放;"录制并回放可复用宏"这一能力本次不随包提供。
- **`POST /skills/batch?diff=1` 聚合净变化** — 跨步保留首个 before 与最终 after，新建后修改只报 added，事务模式在回滚后生成最终 sceneDiff。
- **SKILL.md 调用链路按任务形态分流** — 原文档"每会话必先拉 summary(35K token)"与"不确定才查 schema"两条指引互相矛盾,且 token 大头(上下文认知成本)未被既有网络层优化覆盖。重写为四层择廉取用:意图明确→`/skills/recommend`(2-5KB,带参数 schema,扶正为首选)或已知技能直接 dryRun;一两个领域→brief 目录+scoped;探索式/跨模块才拉 summary;full(618KB)标注 rare。并补充 `search_skills` 本地检索与磁盘缓存说明。
- **string 参数自动接受原生 JSON 数组/对象** — batch 系技能签名为 `string items`,AI 按直觉传原生 JSON 数组时原先抛 "Can not convert Array to String" 的 TYPE_MISMATCH 且零指引(高频首踩坑)。现目标参数为 string 且传入 JArray/JObject 时自动 `ToString(Formatting.None)` 降级(技能内部本就会 Parse 回来,无损),其他类型严格性不变。
- **did-you-mean 参数建议补语义组第三级兜底** — 原两级匹配(6 技能硬编码别名表 + Levenshtein≤3/互为子串)对"语义同义、字面不相近"的参数名系统性失效(全库路径类参数 60+ 命名变体,实测 `assetPath`→`savePath` 编辑距离 4 零建议)。前两级无结果时按驼峰拆词取共享 token(path/name/id 等)纳入建议,`assetPath`→`savePath` 现可给出 suggestions;前两级有结果时行为不变(零噪音增量)。
- **Python `call()` 透传结构化纠错字段** — 官方客户端原先把服务端错误响应剥成 `{success, error, message:''}`,`errorCode`/`details`(unknownParams suggestions/allowedParams)/`suggestedFixes`/`retryStrategy`/`relatedSkills` 全部丢弃,服务端整套容错投资到不了走 Python 通道的 AI 手里。现存在即透传,恒空的 `message` 移除。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.1.0`。

### Fixed

- **Unity 6 Advanced Text Generator 拒绝静态 CJK FontAsset** — Unity 6 的 ATG 文本路径不支持 `UnitySkillsCN-UI` 静态 FontAsset，会持续输出 `Advanced text system cannot use/render using static font asset` 并导致中文无法渲染。现按编辑器版本选择同一份自定义字体的绑定方式：Unity 6 通过 `FontDefinition.FromFont` 直接绑定 `UnitySkillsCN-Regular.ttf`，由 ATG 管理动态字体数据；Unity 2022 继续使用预烘焙静态 FontAsset，保留其图集/材质不会被卸载的稳定性。
- **`scriptableobject_import_json` 裸 JSON 假成功** — `EditorJsonUtility.FromJsonOverwrite` 要求根层为原生类名包装(SO 即 `{"MonoBehaviour":{...}}`,与 export 对称),传裸字段 JSON 时静默 no-op,但技能仍无条件 SetDirty+SaveAssets 并返回 `success=true`——AI 以为写入成功且事后不可感知。现根层无包装键自动包一层(兼容已包装格式),并以写前/写后序列化快照比对做写入效果校验,零变化时返回 `warning` 提示字段未匹配。
- **`?mode=`/`?dryRun=` 拼错静默真执行** — `mode=dry_run`/`validate`/`dryRun=1` 等未知值原先被静默忽略、请求当真执行并返回 success,AI 以为在预演、实际场景已被改(全部容错缺口中唯一"格式错误→静默破坏性执行"路径)。现未知值返回新错误码 `INVALID_MODE`(400,附 received/validValues)且不执行,实机验证含副作用检查(对象确未创建)。
- **`verbose` 类型错落 INTERNAL 死循环** — `{"verbose":"yes"}` 原先在 `ToObject<bool>` 裸抛落通用 catch,返回 `INTERNAL "[Transactional Revert]"` + `wait_and_retry`,AI 按策略傻等重发同一坏 body。现宽容解析 "yes"/"1"/"no"/"0" 等字符串,不可解析时返回 `TYPE_MISMATCH` + `fix_and_retry` 并明示需要 boolean。
- **DryRun/Plan 内部异常误报 INVALID_JSON** — 两处 `catch(Exception)` 把合法 JSON 下语义校验的 NRE 等也报成 "Invalid JSON" + `fix_and_retry`,误导 AI 反复修一个没问题的 body。对齐 Execute 先例细分:`JsonException`→INVALID_JSON,其余→INTERNAL+真实异常消息+abort(防两种循环)。
- **`/skills/batch` 步间对象不可见** — batch 各步共享一个 POST job,`GameObjectFinder` 的请求级场景缓存只在整个 POST 结束后失效,后步按 name 查不到前步刚创建的对象(实测:单步成功、batch 内同参数失败)。现每步 Execute 后即失效场景缓存,建-改-删 5 步回归全绿。
- **Play 模式切换后面板中文掉字(TextCore atlas material 被回收致 MissingReference)** — 进出 Play 模式会让 UI Toolkit 回收 CJK FontAsset 的 atlas material,存活的编辑器窗口 TextElement 仍引用它,触发 MissingReference 与掉字。`UISkillsFont` 现在在 Play 模式切换后重建并重应用字体,从机制上规避回收窗口。
- **`EditorUiScheduler` 渲染期 `delayCall` mutate 抛 `InvalidOperationException`** — 周期 tick 推迟到 `delayCall` 执行时,若恰逢 repaint/`generateVisualContent`,个别 mutation 仍可能抛 `InvalidOperationException`。现对该异常做幂等吞噬,避免一次命中滚成高频 Console 循环(延续 issue #44 的收口)。

## [2.0.9] - 2026-07-04

### Fixed

- **`UnitySkillsWindow` 周期性 UI 刷新触发 `InvalidOperationException` 死循环(issue #44)** — 主窗口 4 处 `schedule.Execute(...).Every(...)` 周期 tick(500ms 的 footer/topbar 状态刷新、500ms 的 topbar 响应式自愈、1000ms 的待批权限 banner、1000ms 的 Settings 抽屉权限轮询)都在回调里直接改 visual tree(`label.text=`、`AddToClassList`/`RemoveFromClassList`)。该回调运行在 panel 的 update 阶段,与 EditorWindow 的 layout/repaint(`generateVisualContent`)可能重叠,命中时抛 `InvalidOperationException`;因为是周期 tick,一次命中就滚成高频 Console 循环,面板常开几秒即可堆到 999+。新增 `EditorUiScheduler.RepeatSafe` 收口:仍用 `element.schedule` 绑定生命周期(detach 自动暂停/attach 自动恢复),但把实际 mutation 推迟到 `EditorApplication.delayCall`(帧间安全点)执行,并对多次排队做合并、对已销毁的 panel 做双重判空;4 处 tick 全部改用该 helper,主 tick 额外在 `OnDisable` 里显式 `Pause`,避免窗口关闭后继续排队刷新。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.0.9`。

## [2.0.8] - 2026-06-27

### Added

- **`?summary=1` 轻量认知 manifest 端点(第三层 schema 取用)** — 此前 agent 取技能清单只有两个粒度:全量 `/skills/schema`(~618KB/~150K token,含每技能参数 schema)或 scoped `/skills/schema?category=`(~13–44KB/类,只看一个模块,易致跨模块盲选)。新增 `GET /skills?summary=1`(也接受 `?includeSchema=false`,对齐 `/skills/recommend` 既有约定),返回每技能 `name`/`description`(完整,非截断)/`category`/`operation`/`riskLevel` 五字段,实测 ~143KB/~35K token(全量约 23%),服务端按 query 缓存。省 token 的来源是省掉 `parameters`/`tags`/`outputs` 等执行细节字段,描述本身保留全文以保认知完整(awareness 层无 `parameters` 字段,描述里的参数提示不与之冗余,截断反损认知);执行前由 dryRun gate 补精确参数。供 agent 首次拉取做**完整项目认知**(全部 726 技能 + 各干什么),执行前再拉 scoped 补精确参数,省 token 不损认知。裸 `/skills` 保持全量(向后兼容)。
- **`get_skills_summary()` Python helper** — 镜像 `get_skill_schema` 的进程内 300s 缓存模式拉取 lite 认知层,重复调用 0ms 缓存命中、不重复付 token。

### Fixed

- **`TryConsume` 改为先校验后删除,合法 token 不再被误销** — `ConfirmationTokenService.TryConsume` 原 `TryRemove` 在 expiry/skillName/argsHash 三项校验**之前**执行,导致合法、未过期但 args 哈希不匹配(客户端 JSON 键序/空白差异、多带字段)的 token 被直接销毁,用户被迫从头走确认流。改为 `TryGetValue` → 三项全过 → `TryRemove`,失败不删;更符合自身 docstring("Successfully consumed tokens are removed")。过期 token 改由 `CleanupExpired()` 清理。
- **非法 JSON 返 `INVALID_JSON`/`fix_and_retry`,不再误报事务回滚致 agent 死循环** — `SkillRouter.Execute` 的 `JObject.Parse`(在 `ValidateParameters` 内)对非法 body 抛 `JsonReaderException` 时落入通用 `catch(Exception)`,返回 `INTERNAL` / `[Transactional Revert]` / `wait_and_retry`,agent 按 `wait_and_retry` 死循环重发同一坏 body。新增 `catch(JsonException)` 返 `InvalidJson` + `fix_and_retry`(对齐 `DryRun` 既有处理);parse 发生在变异/undo 之前,无需事务回滚。
- **`wait_for_job` 改轮询 `GET /jobs`,不再 `Thread.Sleep` 冻结主线程堵 `/health`** — Python `wait_for_job` 原调阻塞式 `job_wait` 技能,其 `Thread.Sleep(25)` 循环跑在 Unity 主线程,冻结编辑器并堵住 `/health` 与所有并发请求最长 60s(Python 默认;`/health` 也在主线程 `ProcessJob` 处理,故一并被堵)。改用 `poll_job` 轮询 `GET /jobs/{id}`(短读 + 客户端指数退避),主线程在轮询间保持空闲,`/health` 持续响应。保持 `job_wait` 返回形态(`success`/`status`/`reportId`/`report`/…)兼容;`success` 改为仅 `completed` 为真(旧 `job_wait` 恒真,更严格)。

### Changed

- **scoped schema/manifest 按查询缓存** — `?category=` 等 filtered 输出原先每次重建 + 序列化全部技能,是 agent 省 token 的主路径却被反复重算。改用 `ConcurrentDictionary` 按规范化 query(小写化 key+value、排序)缓存(`_filteredOutputCache`),`Refresh`/域重载时清。实测 20 并发 scoped 22ms→7ms(0.8×,原 2.66×);内容字节确定,缓存安全。
- **SKILL.md 三层 schema 取用指引** — 纠正"`/skills/schema` 不支持按 category 过滤"的过时说法(实际 `GetFilteredSchema` 已支持);改为三层模型:`?summary=1` 首取轻量认知 → 全量按需 → scoped 按模块;强调 scoped 是细节补充非认知替代,单独用会致跨模块盲选。体积标注 578KB→618KB。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.0.8`。

## [2.0.7] - 2026-06-27

### Fixed

- **`package.json.meta` 与上游 `unity-mcp` 的 GUID 冲突(issue #41)** — `SkillsForUnity/package.json.meta` 的 GUID `a2f7ae0675bf4fb478a0a1df7a3f6c64` 与本项目 fork 上游 `com.coplaydev.unity-mcp` 的 `package.json.meta` 逐字节相同(fork 时连同 `.meta` 一起带过来未重新分配),两包同装时 Unity 把两包资产判为同一 ownership、触发 6 条 missing script 警告。替换为独立 uuid4 GUID `4a81947acf6349c4b37a44f35a71d7e8`(通过启发式自校验、全仓库无外部引用;本包 Skills 靠反射发现、不依赖 GUID 引用,替换不破坏内部链路)。全量对照上游 883 个 `.meta` GUID,确认本仓库与其零交集。

### Changed

- **`/metacheck` 新增"已知第三方包 GUID 黑名单"检查** — 此前 metacheck 只能靠启发式抓"可猜测的伪 GUID",查不出 fork/衍生场景下与上游逐字节相同的确定性冲突(此类 GUID 格式规范、不命中任何伪随机模式,启发式审计会误判合格)。新增步骤 2b,维护已知冲突 GUID 表并把上游 `unity-mcp` 的 `package.json.meta` GUID 列为首条记录,同时给出"克隆上游一次性收集全部 GUID 扩充黑名单"的方法,堵住该盲区。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.0.7`。

## [2.0.6] - 2026-06-23

### Fixed

- **macOS 下面板部分中文字体显示异常(掉字)** — 主面板/审计/Allowlist 三个 Editor 窗口在 macOS 上会出现个别常用汉字渲染成空白(如 `全局更新` → `全 新`、`全局安装` → `全 安装`、`自定义 Agent` → `自 义 Agent`、`卸载` → `载`),掉的字是字形级、稳定但"随机"(局/更/卸/定 等),与字号/加粗/截断无关。根因是 Unity UI Toolkit 在 macOS 上把 CJK 字形按需光栅化进**编辑器共享的动态字体图集**,图集扩容/重排时个别字形拿到失效 UV → 渲染为空白(同一 `.mini-btn.install` 类下 `项目安装` 正常而 `全局安装` 掉字,可证为字形级而非样式问题)。现改为给三个窗口根节点绑定**自带的 CJK 字体**(`UISkillsFont.Apply` → 运行时 `FontAsset.CreateFontAsset`,独立多图集,不再与编辑器共享图集争用),从机制上消除掉字;字体缺失时安全回退到编辑器默认字体,不影响窗口可用性。

### Added

- **内置 CJK 字体(`Editor/UI/Fonts/UnitySkillsCN-Regular.ttf`)** — 基于 [Maple Mono](https://github.com/subframe7536/maple-font) 的 CN 变体(v7.9,CN 汉字源自 [Resource Han Rounded](https://github.com/CyanoHao/Resource-Han-Rounded))**子集化**而来:仅保留 ASCII/拉丁、常用标点与 UI 符号、以及 `GB2312 ∪ 面板实际用到的全部 CJK`(约 7.5k 字形,~5MB,源字体单权重约 18MB),并移除编程连字、将字体族重命名为 `UnitySkills CJK`(避免与用户已装 Maple 冲突)。
- **开源许可合规** — 字体遵循 **SIL Open Font License 1.1**;`Editor/UI/Fonts/` 内随附完整 `OFL.txt`(含原始版权声明)与 `THIRD-PARTY-NOTICES.md`(注明来源、所做修改与合规说明)。字体(含运行时派生的 FontAsset)仅以 OFL 1.1 分发,不并入项目自身许可;Maple Mono 未声明 Reserved Font Name,故子集化与重命名均符合 OFL 第 3 条。

### Changed

- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.0.6`。

## [2.0.5] - 2026-06-19

### Changed

- **SKILL.md description 中英双语化(触发层本地化)** — 按 Anthropic Agent Skills 官方最佳实践,重写全部 71 个顶层 `SKILL.md`(根入口 + `skills/SKILL.md` index + 69 个模块)的 frontmatter `description`,统一为「第三人称:做什么(what)+ `Use when` 触发场景(when),关键术语自然嵌入句中,适度 pushy 对抗 undertrigger」,并补齐**中文触发从句**(做什么 / 当用户要…时使用)。修复纯英文触发词导致中文输入下模块长期"零触发"的本地化缺口(社区 issue #68086);所有 description 仍 ≤1024 字符硬限制。
- **reference 子文档 frontmatter 补全** — 16 个已有 description 的子文档(`unitask-design/*`、`dotween-design/*`)改写为中英双语「内容速览」型;34 个原本无 frontmatter 的子文档(`addressables-design`、`netcode-design`、`shadergraph-design`、`yooasset-design` 设计文档及 5 个 `*_REFERENCE.md`)新增 `name` / `description` / `type: reference` 三字段。子文档不参与模块触发,description 为"被读取时的内容清单",不套用 `Use when` 模板。
- **取舍说明(相对 2.0.4)** — 2.0.4 走英文精简路线(顶层 discovery 降至 ~6.1k 字符);2.0.5 改为**中文用户触发准确率优先**,顶层 discovery 回升至 ~28.6k 字符(连同子文档 frontmatter 合计 ~42k)。代价可控:单条仍 ≤1024 硬限制,且在渐进披露加载模型下(根入口 → 按需读模块)常驻成本基本持平,增量主要落在"按需读取该模块时",相对其正文 token 为零头,以此换取中文输入的触发准确率。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.0.5`。

## [2.0.4] - 2026-06-18

### Changed

- **Unity 对象 ID 兼容层** — 新增 `UnityObjectIdUtility`，统一封装 `EntityId`、legacy `InstanceID`、对象查找、缺失引用检测与 Selection 读写逻辑；Unity 6000.5 走 EntityId 路径，旧版本保留 InstanceID fallback。
- **Unity 6000.x API 对齐** — 更新 PlayerSettings、Shader、FindObjectsByType、UI Toolkit 背景样式等 API 用法，减少 Unity 6000.5 obsolete warning。
- **SKILL.md description 精简** — 裁剪全部 70 个 `SKILL.md` 的 frontmatter `description`，总字符数从 ~21,059 降至 6,138（77% of 8k 软预算），所有文件均低于 1024 字符硬限制，消除 Codex/Claude 发现器因超限拒载的风险。
- **技能数量修正** — README / README_CN 中技能总数从错误的 750 修正为实际的 726，并对齐 UI / PostProcess / Volume / URP / Decal / Camera / Component 等模块计数。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` / README 当前版本标记同步提升到 `2.0.4`。

### Fixed

- **Unity 6000.5 编译兼容** — 替换业务代码中的 `GetInstanceID`、`EditorUtility.InstanceIDToObject`、`objectReferenceInstanceIDValue`、`Selection.instanceIDs` 等 obsolete API 入口，避免 warning-as-error 下编译失败。
- **批处理模型序列化告警** — 移除 BatchModels 的 Unity 序列化标记，避免 Newtonsoft 持久化模型触发 UAC1009。
- **对象定位与工作流选择恢复** — 批量查询/执行、GameObject 查找、Workflow bookmark 与 snapshot 支持 `entityId`，保留 legacy `instanceId` 兼容路径。
- **父子/子对象 entityId 定位全链路打通** — 补齐 `prefab_instantiate`/`prefab_instantiate_batch` 挂父、Cinemachine mixing/state-driven/sequencer 子相机、SkillRouter 快照兜底的 `entityId` 参数；修复 planning 校验层（`SkillPlanningService`）对父子定位只认 name/path 而拒绝 entityId 的问题，现在 `childEntityId`/`parentEntityId` 从 HTTP 请求到执行全链路一致。
- **兼容性说明（additive field）** — `entityId` 为新增的兼容字段，现有 `instanceId` 字段保留不变，legacy 调用方可继续使用 `instanceId`；新调用方优先使用 `entityId`，定位优先级 `entityId > instanceId > path > name`。二者并存，不作替换。

## [2.0.3] - 2026-06-14

### Added

- **`camera_sceneview_screenshot` 编辑器 Scene View 截图 Skill** — 新增截取**编辑器 Scene View**（开发者编辑视角，可俯瞰含镜头外物体的整个场景，区别于 Game View 的玩家视野）的能力，补齐项目此前仅能截 Game View（`scene_screenshot`）与单个 Game Camera（`camera_screenshot`）、无法截编辑器 Scene View 的缺口。默认走内部反射 API（`UnityEditorInternal.InternalEditorUtility.ReadScreenPixel`）截取含网格 / Gizmo / 选中高亮的完整 Scene View；若编辑器构建不支持该内部 API 则自动回退到 Scene View 相机的离屏纯净渲染，`includeOverlays=false` 亦可强制纯净渲染。反射调用保证零编译风险（跨 Unity 2022.3 / 2023.x / 6000.x 签名稳定），返回 `mode`（`screen_with_overlays` / `offscreen_clean`）与 `note` 字段，让 AI 明确拿到的是哪种截图。

### Changed

- **三种截图 Skill 路由澄清** — `camera/SKILL.md` 与 `scene/SKILL.md` 明确区分三者职责，避免 AI 在等价 skill 间混淆：`scene_screenshot`（Game View 最终合成画面）/ `camera_screenshot`（单个 Game Camera 离屏渲染）/ `camera_sceneview_screenshot`（编辑器 Scene View）。
- **`scene_screenshot` 描述对齐 Game View** — 修正项目自身文档长期将其误标为"Scene View"的矛盾：`Localization.cs`（中英 UI 显示）、`scene/SKILL.md`、`camera/SKILL.md` 全部对齐为 "Game View"。该 skill 实际调用 `ScreenCapture.CaptureScreenshot`，截取的是 Game View 最终合成画面（Play 模式即运行时实时帧），与代码属性原本的描述一致；新增 `gameview` / `playmode` 标签与 `isPlaying` / `note` 返回字段，显式标明"截的是 Play 模式实时帧还是 Edit 模式静态帧"及异步契约。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `2.0.3`。

### Fixed

- **`scene_screenshot` 异步写入缺陷** — 原实现调用 `ScreenCapture.CaptureScreenshot` 后立即 `AssetDatabase.Refresh()` 并返回 `success=true`，但该 API 在**下一帧**才把 PNG 落盘，此时刷新是空操作、调用方立即读取会失败（由 issue #38 提出者正确指出）。改用 `EditorApplication.delayCall` 延迟到文件落盘后的下一帧再 `AssetDatabase.Refresh()`，并在返回的 `note` 中提示"立即读取失败请等 ~200ms 重试"。

## [2.0.2] - 2026-06-05

### Added

- **YAML 直编指导 Advisory Skill** — 新增 `yaml-editing` advisory 模块，覆盖 REST Skills 无法触达的 4 个场景：序列化引用/fileID 修复、`.meta`/GUID 安全操作、ProjectSettings 隐藏字段补丁、`.unity`/`.prefab` 合并冲突辅助；包含 Core Concepts（YAML 锚点、GUID vs fileID、序列化引用格式）与 Hard Rules（改前 git 备份、uuid4 GUID、改后 Unity 重导入校验等），路由至 `/metacheck` 和 `/skillcheck`。

### Changed

- **SKILL.md description 合规治理** — 精简 49 个 REST 模块的 frontmatter description，统一格式为"功能简述 — 操作动词列表。Exact signatures via GET /skills/schema."，最长 264 字符；修复 perception / shadergraph-design / test / validation 四个模块超 1024 字符被 Codex/Claude 拒载的问题，全量 description 总量从 ~41k 降至 ~20k 字符，消除 ~8000 字符软预算压力。
- **`/skillcheck` 新增 Frontmatter 合规子检查（3k）** — 校验所有 SKILL.md 的 `description` ≤1024、`name` ≤64、YAML 闭合、无 UTF-8 BOM，并统计 discovery 总量与 ~8000 软预算比较，作为防止超限拒载的防回归闸门。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `2.0.2`。

## [2.0.1] - 2026-06-02

### Added

- **RectTransform 深度控制 Skill** — 新增 `ui_get_rect_transform` / `ui_set_rect_transform` / `ui_set_rect_transform_batch`，覆盖 anchors、pivot、anchoredPosition3D、sizeDelta、offsetMin/offsetMax、local transform 与 width/height，补齐 UGUI 画布元素的深度布局调整能力。
- **Inspector SerializedProperty 赋值能力** — 新增 `SerializedPropertySkillUtility` 与 `component_get_serialized_properties` / `component_set_serialized_property` / `component_set_serialized_property_batch`，支持 public、`[SerializeField] private`、嵌套字段、数组/List 路径、Enum、Vector、Color、场景对象引用与 Project 资产引用。
- **脚本组件严格复制校验** — 新增 `component_copy_exact`，复制组件后逐项比对源/目标 `SerializedProperty`，字段不一致时返回 mismatch 报告，避免静默丢字段。
- **UnityEvent 精确监听器替换** — 新增 `event_set_listener`，支持按 index 替换 Button `onClick` 等 persistent listener 的 target、method、call state 与静态参数（void/int/float/string/bool/Object）。

### Changed

- **Component / UI / Event 文档同步** — 更新调用侧模块文档，明确 Inspector 字段应优先使用 SerializedProperty 新接口，RectTransform 深度编辑与 UnityEvent 精确替换走新增 skill。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `2.0.1`。

### Fixed

- **深度 Inspector 能力测试覆盖** — 新增 `DeepInspectorSkillTests`，覆盖 RectTransform、SerializedProperty 字段赋值、严格组件复制与 Button onClick listener 替换；已通过 8091 Unity 实例验证。

## [2.0.0] - 2026-05-31

> **重大更新**：本版本是基于 v1.9.4 全量审计（14-agent workflow）的深度一致性修复与效率优化版本，落地了「Skill 模式一致性 + 调用效率 + 辅助 Mode 修复计划」（`temp/skill-mode-consistency-fix-plan.md`）的全部 5 个工作包（WP-A 至 WP-E）。

### ⚠️ BREAKING CHANGES

- **默认操作模式语义对齐 Auto（WP-A + C.1）** — 澄清并强化 Auto 模式的真实行为：Auto 模式下，除 NeverInSemi（`Delete` / `MayEnterPlayMode` / `MayTriggerReload` / `RiskLevel=high`）外的**所有 skill（包括 FullAuto 写操作）直接执行**，不再被阻止。这是 v1.9.0 起的既定设计（代码 `SkillsModeManager.cs:143` 从未改变），本次修正了测试断言、文档描述和代码注释中的失实内容，使"代码-测试-文档"三方完全对齐。
  - **测试修正**：`SkillsModeManagerTests.cs` 中 `CurrentMode_FreshInstall_NoKeys_DefaultsToApproval()` 断言从 `Approval` 改为 `Auto`，与代码实现一致（该测试此前与代码冲突，一旦运行必然失败）。
  - **文档修正**：主 `SKILL.md` 明确"Factory default: fresh install → Auto, upgraded install → Bypass; never Approval"；`agent.md` 修正自相矛盾句"默认 Approval；新装默认 Auto"为"新安装默认 Auto；老安装升级默认 Bypass；从不默认 Approval"。
  - **注释修正**：`SkillsModeManager.cs` 类头注释从"FullAuto blocked by default"改为"FullAuto write skills executes directly; only NeverInSemi is blocked with MODE_FORBIDDEN"，与 `CheckAccess:453-454` 实际逻辑一致。

### Added

#### WP-E · 辅助 Mode：预置 Allowlist 包
- **AllowlistPresets 预置包（WP-E.1）** — 新增 `AllowlistPresets.cs` 静态类，提供三组预置 skill 列表：
  - **组 A · 脚本写**（`ScriptWrite`）：`script_create` / `script_append` / `script_replace` / `script_rename` / `script_move` — 这些 skill 标注了 `MayTriggerReload + RiskLevel="high"`，被 `IsForbiddenInSemi` 判为 NeverInSemi，在 Auto / Approval 下都返回 `MODE_FORBIDDEN`，是唯一"非 Allowlist 不可"的编码刚需。
  - **组 B · Inspector 赋值**（`InspectorSet`）：`component_add` / `component_set_property` / `component_set_property_batch` / `component_set_enabled` — 这些是 FullAuto（`approvalBehavior=grant`），在 Auto 模式本就直接执行，加入 Allowlist 主要让 Approval 模式免去逐次 grant。
  - **组 C · 辅助代码编写**（`CodingAssist`）：组 A + 组 B 的合并列表，供 `AllowlistPickerWindow` 的"勾选辅助代码编写包"按钮一键导入。
  - **收录原则**：只收"加入 Allowlist 才有增量价值"的写操作；纯读 / 查询 skill（SemiAuto，任何模式本就放行）与删除类（forbid，留给用户显式追加）一律不收。
- **AllowlistPickerWindow 快捷选择器增强（WP-E.2）** — 在现有 Allowlist 多选弹窗顶部新增"勾选辅助代码编写包"快捷按钮，点击后一键导入 `AllowlistPresets.CodingAssist` 列表，复用现有高危合并确认弹窗（`AllowlistPickerWindow.cs:233-251`）。用户可继续用现有 Picker 的搜索/分组/全选功能叠加自己需要的 Unity 操作，预置项与追加项统一落 `UnitySkills_AllowlistSkills`。
- **安全文案（WP-E.3）** — 在按钮旁明确提示：加入 Allowlist 的高危项默认无二次确认（`RequireConfirmation` 默认 false），即 `script_create` / `script_delete` 会直接落盘/删文件；建议用户"如需安全网，请在 Server > Settings 开启 Require Confirmation"。

#### WP-D · 调用效率优化（行为改动）
- **/skills/schema 支持 ?category 分批过滤（WP-D.5）** — `SkillRouter.GetFilteredSchema()` 新增，支持 `GET /skills/schema?category=...` 查询参数，按需获取分类 schema，避免拉取完整 ~578KB schema。复用 `GetFilteredManifest` 的过滤逻辑（category / operation / tags / readOnly / q），保持向后兼容（无 query 时仍返回全量）。
- **Python 客户端 schema/manifest 进程内缓存（WP-D.4）** — `unity_skills.py` 新增带失效策略的进程内缓存，以 `/health` 返回的 `version` / `skillCount` 作为失效信号，消除同会话反复全量拉取 ~750 skill 的开销。
- **Domain Reload 重试收敛（WP-D.6）** — 修正客户端双层重试叠加问题（`call` 内置 `_retries=3` + `call_skill_with_retry max_retries=3` 乘积放大至 4~16 次 HTTP）。遇 503 `Compiling`（或 ConnectionError）改为"轮询 `/health` 等编译完成后单次重发"，并收敛为单层重试上限，避免撞限流（`SkillsHttpServer.cs:47-49` 100 req/s）形成正反馈。
- **异步轮询指数退避（WP-D.2）** — `poll_job` 从 0.5s 固定间隔改为指数退避（上限封顶），减少长任务下的大量 poll；统一 `JobWait` 与 `wait_for_job` 的默认超时值（C# 10s / Python 60s → 统一为 30s）。

#### 其他新增
- **PackageManagerHelper 测试包可见性增强** — 新增 `PackageManagerHelper.cs`，确保测试程序集（`UnitySkills.Tests.Editor`）在 Package Manager 中可见，避免测试失败（`testables` 字段自动注入）。
- **Approval 双渠道 UI 区分反馈** — `PendingApprovalBannerController` / `SettingsDrawerController` / `TopbarController` 增强，Panel 和 Dialog 两种审批渠道的 UI 区分反馈机制（Panel 渠道下显示待批列表，Dialog 渠道下隐藏）。
- **GeometryChangedEvent 自愈机制** — 处理 UIToolkit `GeometryChangedEvent` 漏派发问题（Unity 6 ATGTextJobSystem 偶发 bug），避免 UI 控件状态不同步。

### Changed

#### WP-B · 面向用户/AI 文档纠错
- **删除 SKILL.md 残留兜底名单（WP-B.1）** — 主 `SKILL.md` 删除 `_explicitNeverList` 兜底名单（`scene_clear` / `scene_new` / `batch_apply`）的残留描述（该名单已于 v1.9.2 删除，且这些 skill 在当前 750 skill 中根本不存在）；NeverInSemi 数量从 `~40+` 更新为 `~75-79`，改为纯 4 条元数据判定（`Delete` / `MayEnterPlayMode` / `MayTriggerReload` / `RiskLevel=high`）。
- **移除"(默认)"Approval 错标（WP-B.2）** — 各模块 SKILL.md（gameobject / scene / editor / component 等）移除错误的"（默认）Approval"标注，改为仅描述行为（"本模块多为 SkillMode.FullAuto，调用需用户 grant"），避免误导 AI 认为 Approval 是出厂默认。
- **修正 Python 客户端过期 docstring（WP-B.3）** — `unity_skills.py` 中 `approve_permission` / `list_allowlist` 等函数的 docstring 从旧 `GrantedSkills` 语义更新为 Allowlist 语义（"单次有效，不写永久白名单"），与 v1.9.1 起的实际行为一致。

#### WP-D · 调用效率优化（文档改动）
- **SKILL.md schema-first 增加跳过门槛（WP-D.1）** — 主 `SKILL.md` 的"Canonical Schema First"章节增加判定门槛："**参数已确定的常见只读/简单写调用可直接调用**；仅当 skill 名或签名拿不准时才查 schema"，避免无条件首选导致对简单任务也反复查 schema。同时把 `GET /skills?category=<Category>` 从"Canonical Schema"挪出，单列并标注："`?category` 返回的是 **manifest**（轻量，含 mode/approvalBehavior/parameters），不是 schema；`/skills/schema` 才是 schema（现已支持 `?category` 分批，见 WP-D.5）"。
- **BATCH-FIRST 强化（WP-D.3）** — 主 `SKILL.md` 与各模块 `SKILL.md` 把"prefer *_batch"提到 Core Rules 更靠前，并加反例提示："逐个调用 = N 次往返（Approval 下 2N）；2+ 对象务必用 *_batch"。

#### WP-C · 代码注释纠正 + 死代码清理
- **补全 agent.md 架构清单（WP-C.3）** — `agent.md` 架构文件清单补列 `SkillPlanningService.cs`（SkillRouter 内部预演引擎，/plan /dryRun + 参数语义校验，非 skill，无 [UnitySkill]），避免被误认为孤儿死代码。

#### 其他变更
- **主 SKILL.md 一致性更新** — 效率优化、编码索引更新、参数描述修正、添加精确签名部分（与 `SkillDocumentationConsistencyTests` 参数简化处理逻辑对齐）。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `2.0.0`。

### Fixed

- **SkillValidationTests 未知参数路径** — 修正测试中未知参数的访问路径（`skill.UnknownParameters` → 正确路径）。
- **SkillDocumentationConsistencyTests 参数简化** — 添加参数简化处理逻辑，避免文档与代码参数名不一致时误报（如 `timeoutMs` vs `timeout`）。
- **PerceptionSkillsTests / SelectionDrivenSkillTests 临时文件夹创建** — 优化测试中的临时文件夹创建逻辑，确保测试隔离性。

### Removed

- **删除孤儿方法 SkillPlanningService.EnrichDryRun（WP-C.2）** — 全仓 grep `.EnrichDryRun(` 零调用者，删除该方法级死代码。**注意**：`SkillPlanningService` 整体是活代码（`SkillRouter.cs:657/1188/1532` 调用），仅删除孤儿方法。

### Docs

- **skillcheck 校验逻辑更新** — `.claude/commands/skillcheck.md` 移除 `_explicitNeverList` 校验逻辑，NeverInSemi 统计更新为 75-79（与 WP-B.1 对齐）。
- **job_wait 超时参数标注（WP-D.2）** — 主 `SKILL.md` 与 `batch/SKILL.md` 交叉标注 `job_wait` 默认 `timeoutMs` 值（C# 10s / Python 60s → 统一为 30s）。

---

## [1.9.4] - 2026-05-29

### Added
- **Topbar 响应式布局** — `TopbarController` 监听 `GeometryChangedEvent`，根据宽度阈值（380px / 300px）动态切换 `topbar--compact` / `topbar--narrow` CSS 类；Compact 模式隐藏 URL 文字区域，Narrow 模式隐藏整个 URL pill 与状态点，确保窗口缩小时控件不重叠。
- **权限徽章降级渲染（Unity 2022 兼容）** — 检测 Unity 主版本号，Unity 6（6000+）使用原生 emoji 文字徽章，旧版本构建 dot + label 的 fallback DOM 结构，并通过 `perm-mode-badge--approval/auto/bypass` CSS 类驱动彩色指示点，彻底消除旧版本 emoji 渲染异常。

### Changed
- **USS 布局修复** — `#topbar` 加 `min-width: 0`；`.status-dot` / `.server-switch` / `.url-pill__copy` / `.icon-btn` 加 `flex-shrink: 0` 防止被压缩；`.url-pill__text` 及其 Unity 内部输入子类选择器补 `min-width: 0`；`.server-status-text` `min-width` 从 56px 收窄至 46px；新增 `topbar--compact` / `topbar--narrow` 响应式规则集与 `perm-mode-badge--fallback` 样式块。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.9.4`。

## [1.9.3] - 2026-05-27

### Fixed
- **2K / 高 DPI 下 Topbar 控件文字重叠（#34）** —— `.server-status-text` 原本仅设 `min-width: 56px`，未声明 `flex-shrink` 与 `white-space`；2K + 中文场景下 "正在运行" 等较长状态字串的渲染宽度超过 56px 时，Label 盒子被父级 flex 行布局压缩回 56px 但字形不收缩，与右侧 `.perm-mode-badge` 视觉重叠。现给 `.server-status-text` 加 `flex-shrink: 0` + `white-space: nowrap`，强制 Label 盒子撑到字形真实宽度且禁止换行；同步给 `.url-pill` 加 `min-width: 0` + `flex-shrink: 1`，让 URL 输入框优先收缩，把空间让给固定宽度的兄弟元素。仅修改 `UnitySkillsWindow.uss` 4 行，零 C# 改动，对 1080p / 既有用户视觉零回归。

### Changed
- **版本号更新** —— `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.9.3`。

## [1.9.2] - 2026-05-22

### Added
- **`job_progress` skill** —— BatchSkills + AsyncJobService 共享快照接口，统一查询 batch / async job 的进度、剩余条目、错误明细，避免客户端各自轮询多个端点。
- **`/skills` manifest 新增 `approvalBehavior` 字段** —— 每条 skill 返回 `allow` / `grant` / `forbid` 三态（基于当前 currentMode + skill 元数据预测，**忽略 Allowlist/一次性 bypass 状态**），AI 启动一次 `/skills` 拉取即可推断在 Approval 模式下每个 skill 的预期行为，不再需要先发起调用再依赖错误码回探。

### Changed
- **元数据补全 17 项,IsForbiddenInSemi 完全由 metadata 自动判定** —— TestSkills `test_run` / `test_run_by_name` 补 `MayEnterPlayMode`,`test_create_editmode/playmode` 补 `MayTriggerReload+MutatesAssets`;PackageSkills `install_cinemachine/splines` 补 `RiskLevel="high"`;ScriptSkills `rename/move` 补 `MayTriggerReload`;DebugSkills `set_defines` 补 `MayTriggerReload`;WorkflowSkills `bookmark_set/goto` 改 `SemiAuto`;PerceptionSkills `scene_export_report` 改 `SemiAuto`;ProBuilderSkills `delete_faces` 改 `Modify`(不再误标 Delete);CleanerSkills `fix_missing_scripts` 改 `Modify`。完成后 `IsForbiddenInSemi()` 完全由四条元数据规则(Delete / MayEnterPlayMode / MayTriggerReload / RiskLevel=high)自动判定,覆盖 75 个 NeverInSemi skill。
- **文档全量一致性审查** —— Skill 总数 `714 → 750`(PostProcess 10→20 / Volume 9→18 / URP 7→14 / Decal 7→14 / Test 11→13 等 4 个模块计数修正);68 个 SKILL.md 格式统一(空 H2 清理、Style B→A、advisory 标注);17 处 Mode / NeverInSemi 文档失同步修正(editor / scene / project / prefab / test / workflow / bookmark / batch / probuilder / yooasset / netcode 等);35+ 处 Returns 字段漂移修复(validation Python 示例 `unusedAssets / foldersToDelete` → `assets / folders`、component / console / editor / script / test / shader / animator / terrain / material / light / navmesh / gameobject 等模块的返回字段与 Outputs 元数据对齐)。
- **`/health` 增加 `grantedCount = allowlistCount` deprecated 别名** —— 与 `/permission/status` 在 v1.9.1 保留 `granted` 别名一致,客户端继续读旧字段不会破坏。下个 minor 版本统一移除。
- **UI 文案 Bypass "(全自动)" → "(全部直接放行)"** —— 老称谓与现行三档语义不再匹配,改为更精确的描述,避免与 Auto 模式混淆。

### Fixed
- **`shader_create` template 文档幻觉修正** —— 文档中"Templates"表(Unlit / Standard / Transparent)误导 AI 认为可按模板名调用,实际 `template` 参数是 raw 源码字符串。移除假表格,明确路由到 `shader_create_urp` 处理 URP 模板需求。
- **DO NOT 列表反向幽灵修正** —— `cleaner/SKILL.md` 错误声称 `cleaner_fix_missing_scripts` 不存在(实际已实现);现已修正为正确分类。

### Removed
- **`SkillsModeManager._explicitNeverList` 死代码清理** —— 兜底名单 `{scene_clear, scene_new, batch_apply}` 在当前 750 skills 中已**零命中**(全部满足元数据规则),保留只增加未来理解负担。删除后 `IsForbiddenInSemi()` 仅保留四条元数据规则,逻辑更直观。

### Changed (cont.)
- **版本号更新** —— `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.9.2`。

## [1.9.1] - 2026-05-21

### Changed
- **权限系统拆分为两条独立通道** —— 用户管理的 Allowlist（可覆盖 Delete / PlayMode / Reload / RiskLevel=high 等 ModeGate 高危拦截，但**不绕过** ConfirmationToken 二次确认）+ 单次一步执行的 Approval grant（不再持久化）。原"批准后 GrantedSkills 永久自动放行"语义移除：grant 现在每次都要重新走，持久放行交给 Allowlist。
- **`/permission/grant` 改为一步执行** —— Grant 通过的同一次请求里，服务端直接执行原 skill 并在响应里返回 ``{ok: true, executed: true, skill, result}``，AI 不再需要重放原 skill 调用。Panel 渠道下用户点完 Approve 后，AI 同样调一次 `/permission/grant` 拿 `result`。args 字段在 grant 端点可省略，服务端用 entry 缓存的原 args 校验+执行。
- **设置抽屉 "Granted Skills" 重命名为 "Allowlist Skills"** —— 整段（含 `+ Add Skill` / `Clear All` 按钮和列表）包进 Foldout 整体可折叠，节省抽屉空间；已授权 skill 按 Category 二级折叠分组，未注册的归 `(Unknown)` 组。
- **`/permission/status` 字段 `granted` 重命名为 `allowlist`** —— 保留 `granted` 一版本作为 deprecated 别名，下个 minor 版本移除。

### Added
- **Allowlist 多选弹窗（`AllowlistPickerWindow`）** —— UXML + USS 模板驱动，含顶部搜索框（按 name / category 实时过滤）、按 Category 折叠分组、每组 `Select all in group` / `Clear` 一键勾选、底部 `Add Selected (N)` 汇总；提交时若含高危 skill 弹一次合并确认对话框列出所有高危名；全部文案走 `PermissionUiHelpers.L` 双语 i18n。
- **3 个新 REST 端点** —— `GET /permission/allowlist`、`POST /permission/allowlist/add`、`POST /permission/allowlist/remove`（body `{skill}` 或 `{all: true}`），统一 Allowlist 的查询与增删。
- **Python 客户端 3 个辅助函数** —— `list_allowlist()` / `add_to_allowlist(skill)` / `remove_from_allowlist(skill=None, all=False)`，与 REST 端点一一对应。
- **`SkillsModeManager` 新 API** —— `AllowlistSkills` / `IsInAllowlist` / `AddToAllowlist` / `RemoveFromAllowlist` / `ClearAllowlist`；internal `TryGrantAndReturnArgs` / `ConsumeOneShotBypass` / `TryPeekArgsJson` 支持一步执行。

### Fixed
- **修复 12 处伪 GUID 碰撞风险** —— `GameObjectFinder.cs` / `UnitySkills.Editor.asmdef` / `Editor` 目录 / `Icons` 目录 / 4 个 Agent icon / 3 个 UI Controller / `SettingsDrawer.uxml` 的 `.meta` 全部替换为 uuid4 真随机 GUID。原伪 GUID（如 `112233...ff00`、`f1b2c3d4...abcde`、`8a7b6c5d...4c3d`）字符模式可猜测，与第三方包碰撞概率比真随机高数个数量级；同类问题曾在 v1.8.x 造成 `ValidationSkills.cs` 被排除导入引发 CS0103。已 grep 验证零外部引用。
- **`AllowlistPickerWindow` 初次打开显示 "All in allowlist" 假象** —— `GetWindow` 触发 `CreateGUI` 时 `_grouped` 还未加载导致空状态被渲染；修复为在 `CreateGUI` 内先 `LoadCandidates` 再绑定 UI。

### Migrated
- **EditorPrefs 一次性迁移** —— 首次启动时把老 key `UnitySkills_GrantedSkills` 自动迁移到 `UnitySkills_AllowlistSkills`，写入 `UnitySkills_AllowlistMigratedFromGranted = true` 防重复；老 key 保留不删（回滚标记）。用户无感知：原已批准的 skill 现在变成"白名单"，行为一致（仍直接放行）。

### Deprecated
- **`POST /permission/revoke`** —— 仍然工作，但已转发到 `/permission/allowlist/remove`，下个 minor 版本会移除。
- **Python `revoke_permission()`** —— 同上，docstring 已标注，请改用 `remove_from_allowlist()`。
- **`SkillsModeManager` Obsolete 别名** —— `GrantedSkills` / `Revoke(name)` / `RevokeAll()` 仅作过渡兼容保留一版本，请改用 `AllowlistSkills` / `RemoveFromAllowlist` / `ClearAllowlist`。

### Changed (cont.)
- **版本号更新** —— `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.9.1`。

## [1.9.0] - 2026-05-20

### ⚠ BREAKING CHANGES

- **默认模式从"AI 路由策略"升级为服务端门禁** — 原 Semi-Auto / Full-Auto 仅是 AI 路由建议，REST API 不拦截；v1.9.0 起 `SkillsModeManager` 在 `SkillRouter.Execute` 入口做真权限检查。
- **新安装默认 Auto** — AI 直接执行 FullAuto skill（保留低门槛），服务端仅拦 NeverInSemi（`Delete` / `MayEnterPlayMode` / `MayTriggerReload` / `RiskLevel="high"` + 兜底名单 5-10 条）；如需 per-skill 审批，到 `Window > UnitySkills > Server` 切到 Approval。
- **老安装升级保持 Bypass（零感知）** — 通过检测旧版 `UnitySkills_*` EditorPrefs key 识别老安装，默认切到 Bypass，行为与原 Full-Auto 完全一致，无需任何操作。
- **移除对话触发词切换模式机制** — 不再支持 "全自动模式" / "semi-auto" 等对话触发词，必须在 `Window > UnitySkills > Server` 面板切换。

### Added
- **Skill 模式权限系统** — 三档操作模式（Approval / Auto / Bypass），对齐 Claude Code permission modes（`default` / `acceptEdits` / `bypassPermissions`）。
- **Approval 模式双轨审批** — Dialog 渠道（AI 对话同意，默认）/ Panel 渠道（Unity 面板按钮，面板开关启用）；Panel 渠道下 AI 提前调 grant 会返回 `GRANT_PENDING_APPROVAL`。
- **grant token 配对机制** — 服务端发 `grantRequestToken`（绑定 `skill + argsHash + TTL 5min`），AI 必须重放服务端发出的 token 才能授权，防止 AI 自我授权。
- **6 个新 REST 端点** — `/permission/{status,grant,approve,deny,revoke,audit}`，覆盖授权状态查询、grant 重放、面板批准/拒绝、撤销、审计日志查询。
- **审计日志** — `Library/UnitySkillsAudit.jsonl`（per-project，不入 Git），异步 append + 1MB 滚动 + 保留 3 份；记录 `mode_restricted_hit` / `grant` / `call` / `revoke` / `deny` 五类事件，含 `tokenAgeSec` 等观察指标。
- **UnitySkillsWindow 面板** — Server Tab 新增三档单选 + Panel Approval 开关 + 待批列表（含 token / argsSummary / 倒计时） + 已授权列表 + `[Revoke]` / `[Revoke All]` + `[View Audit Log]` 按钮。
- **新错误码** — `MODE_RESTRICTED`（Approval 下 FullAuto skill 未授权）/ `MODE_FORBIDDEN`（NeverInSemi 在 Approval/Auto 下被拒）/ `GRANT_PENDING_APPROVAL`（Panel 渠道等待用户点 Approve）。
- **AI Config 卡片 per-scope 卸载** — Skill Installer 卡片的 Uninstall 按钮根据安装状态动态变形：未安装 → 灰态占位；仅一处安装 → 直接卸载该 scope（按钮自带 scope 标签）；两处都装 → 单按钮带 `▾` 展开 GenericMenu，分别选择 Project / Global。点击瞬间读取最新状态，避免 stale 捕获。
- **审计日志窗口删除能力** — `Window > UnitySkills > Audit Log` 每行末尾新增悬停才显形的 `✕` 单条删除按钮，工具栏新增 `🗑 Clear All` 红色危险按钮整体清空；删除动作本身会写 `audit_deleted` / `audit_cleared` 追踪事件到日志，保留 trust anchor 属性。底层 `SkillsAuditLog.DeleteEntry(ts, type)` 与 `ClearAll()` 用 `.tmp` + `File.Replace` 做原子重写，并发安全。
- **UnitySkillsWindow UI Toolkit 重构** — 原 1157 行单文件 IMGUI 拆为 UI Toolkit 多 Tab 架构：`UnitySkillsWindow.uxml/uss` + 4 个 Tab（Server / AIConfig / Skills / History）+ 各 `*TabController.cs`；新增 `Topbar`（mode 徽章 + 模式切换菜单）/ `SettingsDrawer`（抽屉式权限与端口设置）/ `Footer` 三个常驻 controller，UI 样式全部走 USS 主题文件。
- **AI Config 卡片重写为数据驱动 + Agent 图标** — `_agentConfigs` 元数据列表统一描述 4 个内置 Agent（Claude Code / Codex / Antigravity / Cursor），新增 Agent 仅需追加一条；UI 卡片含品牌图标（`Editor/UI/Icons/<id>.png`）、安装状态徽章和 Custom Agent 自定义路径卡片。

### Changed
- **`[UnitySkill]` attribute** — 新增 `Mode` 字段（`SkillMode.SemiAuto` / `SkillMode.FullAuto`，默认 FullAuto）；121 个明确低风险 skill 显式标注为 SemiAuto。
- **`GET /health` 响应** — 新增 `currentMode` / `panelApprovalRequired` / `pendingCount` / `grantedCount` 四个字段，AI 启动时一次拉到全部权限状态。
- **`GET /skills` 响应** — 每条 skill 增加 `mode` 字段（`"semi"` | `"full"`），不再返回 `never_in_semi`（改由自动判定）。
- **NeverInSemi 自动判定** — 不再手标，由元数据自动归类：`Operation.HasFlag(Delete)` / `MayEnterPlayMode` / `MayTriggerReload` / `RiskLevel="high"`，加 `_explicitNeverList` 兜底（`scene_clear` / `scene_new` / `batch_apply` 等 5-10 个）；覆盖 40+ skill。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.9.0`。

### Migration Guide
- **老用户**：升级到 v1.9.0 后默认保持 Bypass，无需任何操作，行为与原 Full-Auto 完全一致；如需启用权限审批，到 `Window > UnitySkills > Server` 面板切到 Approval / Auto。
- **新用户**：默认 Auto，AI 可以直接调 FullAuto skill；只有 NeverInSemi（Delete / 可能进入 PlayMode / 可能触发 Reload / `RiskLevel="high"`）会被服务端拦截。需要 per-skill 手动审批时切到 Approval。
- **自动化 / CI 场景**：在 `Window > UnitySkills > Server` 切到 Bypass，行为与 v1.8.x 全自动一致。
- **敏感项目**：开启 Approval + Panel Approval Required，所有 grant token 必须在 Unity 面板手动批准。

## [1.8.4] - 2026-05-17

### ⚠ BREAKING CHANGES

- **`test_list` / `test_list_categories` 行为变更（PR #33 by @Eggnisi）** — 测试发现从「源码扫描」改为「Unity Test Runner 原生异步发现」。首次调用（缓存缺失）不再同步返回测试列表，而是返回 `success=true` + `pendingDiscovery=true` + `discoveryJobId` / `discoveryStatus` / `discoveryMode` + `message`，并自动启动后台 discovery；调用方判断 `pendingDiscovery==true` 后轮询 `test_discover_get_result(jobId)`，然后重试 `test_list` / `test_list_categories`。`discoveryMode` 字面量从 `source_scan_with_file_dependencies` 改为 `unity_test_runner_async_cache`。<br>**注**：未使用 `success=false + error` 是因为 `SkillRouter` 会把这种形态转成统一错误响应（`SkillErrorResponse`）并丢掉 `discoveryJobId` 等字段——`pendingDiscovery` 标志位绕开了该转换。
- **`AsyncJobService.BuildTestFilter` 不再设置 `assemblyNames`** — 删除非 Unity 原生的程序集名推断逻辑（`TestSkills.ResolveGroupAssemblyNames` 同步移除）。filter 在缓存未命中时 fallback 为 `new[] { filter }` 作为 testName，并向 job.warnings 写入一条诊断说明。

### Added
- **PR #33 by @Eggnisi: Unity Test Runner 异步发现链** — 新增 `test_discover_start` / `test_discover_get_result` 两个 skill，包装 Unity `TestRunnerApi.RetrieveTestList` 异步发现。结果存入 BatchPersistence，`test_list` / `test_list_categories` / `test_run`（filter 解析）统一读取缓存。感谢 @Eggnisi 贡献此 PR。
- **`StartTestDiscovery` 并发守卫与旧 discovery 清理** — 进入 `StartTestDiscovery` 时若已有同 testMode 的 `running` 状态 discovery，则直接复用而非新建；启动新 discovery 前主动清理同 testMode 的 `completed/failed` 旧 job（`PruneOldDiscoveries`），避免 100 条 job 容量挤兑 `test/smoke/compile` 等重要 job。
- **`/release` skill 触发器** — 在 `skills` 目录下增加 release 工作流触发标识，便于通过 slash command 调用 beta → main 同步与 Release Note 生成流程。

### Changed
- **`test_list` / `test_list_categories` Description 字段** — 在 `[UnitySkill(...)]` 属性中明确写出「首次调用返回 `pendingDiscovery=true` + `discoveryJobId`」契约。
- **discovery 结果排序统一在 `GetDiscoveredTests` 出口处** — 移除 `StartTestDiscovery` 异步回调内重复的 `OrderBy`，避免双重排序，对反序列化路径同样统一处理。
- **filter fallback 诊断** — `AsyncJobService.BuildTestFilter` 在缓存未命中走 fallback（直接以 filter 作 testName）时，向当前 job.warnings 写入诊断说明，便于调用方在 `job_status` 中查看为什么测试匹配跨多个程序集。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.8.4`。

### Removed
- **`TestSkills.CollectTests` / `TestSkills.CollectCategories`** — 旧源码扫描路径残留的 dead code（无任何调用方）。
- **`TestSkills` dead using `Newtonsoft.Json`** — 文件内仅使用 `Newtonsoft.Json.Linq`（已有 using），主命名空间无 `JsonConvert` 调用。
- **`.scratch/apply_fixes.py`** — 一次性脚本残留，清理。

## [1.8.3] - 2026-05-06

### Fixed
- **修复 .meta 伪 GUID 与第三方包冲突导致编译失败** — 包内 46 个 `.meta` 文件历史上使用了顺序模式伪 GUID（如 `a1b2c3d4e5f6...`、`0123456789abcdef...`），在用户项目中与 `com.posthog.unity` 等第三方包发生 GUID 碰撞，导致 Unity 把 `ValidationSkills.cs` / `AnimatorSkills.cs` / `LightSkills.cs` / `UISkills.cs` 等文件 ownership 判给其他包并排除我们的导入，进而触发 `error CS0103: The name 'ValidationSkills' does not exist in the current context`（`BatchSkills.cs:481-482`、`PerceptionSkills.cs:593,608`）。本次修复将所有 46 个伪 GUID 全部重新生成为 uuid4 真随机 GUID，影响范围已用 grep 全库验证（0 处外部引用），不会破坏任何 prefab/asset 序列化关系。
- **修复 `#define` 符号不一致** — `asmdef` 与 `package.json` 历史上重复定义 `versionDefines`，且符号名不一致：`com.unity.splines` 在 asmdef 中是 `SPLINES_2`（`[2.0,3.0)`），在 package.json 中是 `HAS_SPLINES`（任意版本），但代码中只使用 `HAS_SPLINES` —— 当 Unity Package Manager 解析顺序异常或 package.json 被忽略时整段 `#if HAS_SPLINES` 块会失效。本次统一为 asmdef 中的 `SPLINES_2`，并删除 package.json 中冗余的 `versionDefines`（CINEMACHINE_2/3、NETCODE_GAMEOBJECTS 在两处定义完全一致，已被 asmdef 完整覆盖；splines 范围由任意版本严格收紧到 2.x，因为代码使用 `CinemachineSplineDolly` 等 splines 2.x API，对 1.x/3.x 不兼容）。
- **改进 `cinemachine_set_spline` 在不兼容 splines 版本下的错误消息** — 原消息为 `Splines 包未安装`，对实际安装了 splines 1.x 或 3.x 的用户具有误导性。新消息明确说明需要 `com.unity.splines` 2.x（`[2.0,3.0)`），并列出三种可能原因（未安装 / 装的是 1.x / 装的是 3.x）。

### Changed
- **`[InitializeOnLoad]` 静态构造增加 try/catch 异常防护** — 7 个 Editor 启动入口（`AsyncJobService` / `BatchJobService` / `RegistryService` / `SkillsHttpServer` 静态构造，以及 `DOTweenPresenceDetector.Synchronize` / `PackageManagerHelper.Initialize` / `YooAssetSkills.RestoreRuntimeValidationJobsAfterReload` 三个 `[InitializeOnLoadMethod]`）原先未做异常防护，受限环境（CI、容器、远程开发机权限不足）下 IO 失败 / 文件权限 / 端口占用等异常会引发 `TypeInitializationException`，症状与 GUID 冲突相似（包看起来装了但完全不工作）。本次全部包 `try/catch`，异常通过 `Debug.LogError` 输出，不会拉崩 Editor。`RegistryService` 还为 `InstanceId` / `ProjectName` / `ProjectPath` 设置 fallback 值，确保即使初始化失败下游仍能拿到非 null 字段。
- **通用类型名命名空间隔离** — `FindHelper`（`internal`）和 `ObjectSnapshot`（`public [Serializable]`）原属 `namespace UnitySkills`，与第三方包共存时存在命名歧义风险。本次迁至 `namespace UnitySkills.Internal`，作为父命名空间的子命名空间（C# 作用域规则使其内部仍可直接引用 `UnitySkills` 中的 `ComponentData` / `SnapshotType` 等类型，无需添加 using）；18 个 `FindHelper` 消费方文件 + `WorkflowManager.cs` + `WorkflowModels.cs` 自身已统一注入 `using UnitySkills.Internal;`。
- **`Localization` 类重命名为 `SkillsLocalization`** — 原名与 `UnityEngine.Localization` 命名空间存在视觉混淆风险。`UnitySkillsWindow.cs`（85 处）+ `Localization.cs`（2 处）+ `SkillDocumentationConsistencyTests.cs`（3 处）共 90 处词边界正则替换，`GetLocalizationDictionary` / `EnglishAndChineseLocalization` 等子串未受影响。文件名 `Localization.cs` 保留以避免动 `.meta` GUID。
- **`package.json` 清理** — 删除冗余的 `versionDefines` 数组（asmdef 已包含完整定义）；删除 `_fingerprint` 字段（Asset Store 缓存导出时的偶然产物，开源 GitHub 包不应保留）。
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `1.8.3`。

### Added
- **`/metacheck` 自定义命令** — 新增 `.claude/commands/metacheck.md`，用启发式扫描全仓库 `.meta` 文件检测伪 GUID（连续 hex / 交错递增 / 重复字符 / abcdef 字面），支持 `--fix` 自动重生成模式，作为本类回归的长期防护。

## [1.8.2] - 2026-05-02

### Added
- **`progressGranularity` 参数** — `batch_execute` 新增 `progressGranularity`（默认 10），每处理 N 个 item 向 `progressEvents` 写入一条进度事件（含毫秒时间戳、百分比、stage、描述），解决小批次 progress 从 0 直跳 100 无法观测的问题。
- **`GET /jobs/{id}/progress` 端点** — 新增细粒度进度事件增量拉取端点，支持 `?offset=N` 参数，客户端每次传入上次 `totalCount` 即可只获取新增事件，响应含 `terminal` 标记，适合 100–200ms 高频轮询。
- **Python `get_job_progress(job_id, offset)` 辅助** — `unity_skills.py` 新增对应封装，支持增量轮询进度事件。

### Changed
- **`GET /jobs/{id}` 支持 `recentCount` 参数** — 可指定返回最近 N 条进度事件（1–200，默认 10），通过 `recentProgress` 字段返回。
- **`batch/SKILL.md` 文档补全** — 补充 `progressGranularity` 参数说明、`job_status` 的 `recentCount` 参数、`job_progress` 端点完整文档（增量轮询用法与响应字段）。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.8.2`。

## [1.8.1] - 2026-05-02

### Added
- **统一错误响应体系** — 新增 `SkillErrorCode` 枚举（21 个稳定错误码）与 `SkillErrorResponse` 统一构造器，所有 REST 错误响应统一返回 `status / errorCode / error / skill / details / suggestedFixes / relatedSkills / retryStrategy / retryAfterSeconds` 形状，AI Agent 可基于 errorCode 做确定性自愈，无需 NLP 解析自由文本；`unknownParams` 错误自动生成可重放的 SuggestedFix。
- **高风险技能二次确认** — 新增 `ConfirmationTokenService`（内存字典 + 5 分钟 TTL，token 与 `(skillName, argsHash)` 绑定，SHA256 哈希防 token-then-modify-args 攻击）。`SkillRouter.Execute` 新增 confirmation gate：当 `RiskLevel="high"` 或 `Operation` 含 `Delete` 时，首次调用返回 `CONFIRMATION_REQUIRED` + 新 token + 内嵌 dryRun 预览；二次调用带 `_confirm` 校验通过后执行；`_confirm` 加入 `_reservedBodyParameters`，不传给 skill 自身。`UnitySkillsWindow` Server 标签页 Settings 卡片新增 toggle，默认关闭——全自动场景完全无感。
- **`GET /jobs` 长轮询端点** — 新增三个绕开 skill router 的轻量端点：`GET /jobs`（最近 N 个 BatchJobRecord 列表）、`GET /jobs/{id}`（完整快照 + recentProgress + terminal 标记）、`GET /jobs/{id}/logs`（结构化日志，带 stage/level/code），专为高频进度轮询设计（200–500ms 拉一次即可获得平滑进度）。Windows HttpListener 的 chunked + flush 行为不可靠，按事先约定走轮询降级而非 SSE。
- **`unity_diagnose` 聚合诊断 Skill** — 新增 `DiagnoseSkills` 模块（1 个 Skill），单调用返回 console errors + 编译状态 + 最近 5 个 workflow tasks + 服务器统计 + 最近 10 个 jobs，AI 排错时取代 `console_get_logs / workflow_* / health` 等 4–5 个 Skill 的串联调用。
- **Python 客户端长轮询/诊断辅助** — `unity_skills.py` 新增 `get_job(id)` / `list_jobs(limit)`（走轻量 `GET /jobs` 端点）、`poll_job(id, interval, timeout, on_progress)` 长轮询便捷封装、`diagnose(...)` 聚合诊断快捷调用。

### Changed
- **`/skills/recommend` 检索增强** — `includeSchema=true` 返回完整参数定义、outputs / tags / riskLevel；score 归一化为 `confidence: high(≥10) / medium(≥5) / low`，AI 可直接按置信度分支。
- **`ResolveSkillNotFound` 智能 did-you-mean** — 用 Levenshtein 距离给出最近 5 个建议，取代原 "前 20 个名字" 的浪费输出。
- **manifest / schema 响应体瘦身** — `SkillRouter` manifest / schema 取消 `Formatting.Indented`，响应体减小约 30%。
- **HTTP 异常透出** — `SkillsHttpServer` 把 swallowed catch 改成显式异常 + 日志，便于排错。
- **类型缓存与过期清理** — `SkillsCommon` 类型查找缓存逻辑优化，新增过期运行时清理；改进测试文件夹检测策略。
- **`ConfirmationTokenService.IsHighRisk` 可见性收紧** — 改为 `internal`，因 `SkillRouter.SkillInfo` 是 `internal`，`public` 方法的参数不能引用更窄可见性的类型（CS0051）。
- **GameObject 文档扩展** — `gameobject_set_transform` 文档补充 World / Local / RectTransform 三套参数空间（`localPos*`、`anchoredPos*`、`anchor*`、`pivot*`、`sizeDelta*`、`width / height`），并明确 RectTransform 专属参数在普通 Transform 上会被忽略；`gameobject_create_batch / delete_batch / duplicate_batch / rename_batch` 补齐 `items` 参数表格行。
- **多个模块文档微调** — `asset / component / debug / history / light / material / yooasset` 的 SKILL.md 同步最新 Skill 行为说明。
- **Skill 计数文档同步** — 文档总数从 `713` 修正为 `714`（新增 `unity_diagnose` +1，与 Unity 端反射计数一致）；README / agent.md / SKILL.md 模块表加入 Diagnose 行；为 Volume / PostProcess / Decal / URP 4 个 SRP 模块加 `†` 标注，说明 URP 未安装时这 4 个模块以同名 stub 返回 `NoURP()`，rg 源码计数（747）≠ 运行时计数（714）的差额由 `#if !URP / #else` 双分支编译解释。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.8.1`。

## [1.8.0] - 2026-04-24

### Added
- **YooAsset 资源管理模块** — 新增 40 个 Skill（`yooasset_*`），覆盖资源包构建、加载、释放、更新、文件系统查询和 PlayMode 模拟，基于反射实现零编译依赖（`#if YOO_ASSET`）。
- **Netcode 网络模块** — 新增 33 个 Skill（`netcode_*`），覆盖 NetworkManager 创建/配置、NetworkObject/NetworkTransform 管理、Prefab 列表、Scene Management、Transport 配置和运行时启停（`#if NETCODE_GAMEOBJECTS`）。
- **Shader Graph 模块** — 新增 23 个 Skill（`shadergraph_*`），支持创建/查看/编辑 Shader Graph 和 Sub Graph 资产，含节点增删、连线、属性管理等操作。配套 `ShaderGraphReflectionHelper`（2697 行）和 `ShaderGraphNodeRegistry` 实现类型安全的反射编辑。
- **DOTween 动画模块** — 新增 21 个 Skill（`dotween_*`），覆盖 DOTween 状态查询、Settings 配置、Tween/Sequence 脚本生成，以及 DOTween Pro Animation 组件的完整 CRUD。配套 `DOTweenReflectionHelper` 和 `DOTweenPresenceDetector` 实现纯反射调用。
- **PostProcess 后处理模块** — 新增 20 个 Skill（`postprocess_*`），提供 SRP 后处理效果的统一管理，含 Bloom、DoF、Tonemapping、Vignette、Color Adjustments 快捷配置。
- **Volume 体积系统模块** — 新增 18 个 Skill（`volume_*`），支持 VolumeProfile 创建/管理、VolumeComponent 增删改查、批量参数设置。
- **Graphics 图形设置模块** — 新增 11 个 Skill（`graphics_*`），涵盖质量设置、渲染管线资产管理、Always Included Shaders 和 Shader Stripping 配置，从 Project 模块独立为专用模块。
- **URP 管线模块** — 新增 14 个 Skill（`urp_*`），覆盖 URP 资产配置、Renderer 数据和 Renderer Feature 的增删改查与启停。
- **Decal 贴花模块** — 新增 14 个 Skill（`decal_*`），支持 URP Decal Projector 的创建/配置/查找/批量操作，含 Renderer Feature 自动注入。
- **RenderPipelineSkillsCommon** — 新增 SRP 共享基础设施（877 行），为 URP、Volume、PostProcess、Decal、Graphics 模块提供统一的渲染管线感知和配置操作辅助方法。
- **6 个新 Advisory 模块** — `addressables-design`（8 文档）、`netcode-design`（9 文档）、`shadergraph-design`（5 文档）、`unitask-design`（8 文档）、`dotween-design`（8 文档）、`yooasset-design`（8 文档），Advisory 总数从 13 增至 19。
- **asmdef 扩展** — 新增 `Unity.Netcode.Runtime`、`YooAsset`、`Unity.RenderPipelines.*.Runtime` 程序集引用；新增 `NETCODE_GAMEOBJECTS`、`YOO_ASSET`、`SRP_CORE`、`URP`、`HDRP` 版本定义。
- **SkillCategory 扩展** — `UnitySkillAttribute` 新增 Netcode、YooAsset、DOTween、Graphics、Volume、URP、Decal、PostProcess、ShaderGraph 共 9 个分类枚举。
- **SkillRouter ShaderGraph 路由** — 新增 `shadergraph`/`subgraph`/`着色图`/`子图` 意图关键词映射。

### Changed
- **路径检查统一** — 多个模块（AssetImportSkills、AssetSkills、CleanerSkills、SkillPlanningService、GameObjectFinder）统一使用 `SkillsCommon.PathExists()` 替代重复的 `File.Exists + Directory.Exists` 组合。
- **类型查找缓存** — `SkillsCommon.FindTypeByName()` 提供跨程序集类型查找与缓存（含 null miss 缓存），`PerceptionSkills.FindTypeInAssemblies` 委托至此实现。
- **Quality 设置迁移** — `project_get_quality_settings` 和 `project_set_quality_level` 从 ProjectSkills 迁移至 GraphicsSkills，与渲染管线设置统一管理。
- **LightmapSettings API 适配** — `LightGetLightmapSettings` 改用 `Lightmapping.lightingSettings` 读取 `lightmapMaxSize`/`lightmapPadding`，兼容新版 LightingSettings 工作流。
- **spritePackingTag 兼容处理** — AssetImportSkills 对已废弃的 `spritePackingTag` 加 `#if !UNITY_2023_1_OR_NEWER` 条件编译保护。
- **CinemachineSkills 编译清理** — `FindCinemachineType` 方法增加 `#if CINEMACHINE_2 || CINEMACHINE_3` 条件编译，消除未安装 Cinemachine 时的编译警告。
- **UnitySkillsWindow 优化** — Validate 按钮文本本地化，移除未使用的 `_showSkillConfig` 和 `_autoStartServer` 字段。
- **SkillPlanningService 简化** — `ResolveGameObject` 使用默认参数合并两个重载；路径存在性检查统一委托 `SkillsCommon.PathExists`。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.8.0`。

### Docs
- **9 个新功能模块文档** — decal、dotween、graphics、netcode、postprocess、shadergraph、urp、volume、yooasset 各含完整 SKILL.md。
- **6 个新 Advisory 文档集** — 共 ~46 个 markdown 文档，覆盖 Addressables、Netcode、ShaderGraph、UniTask、DOTween、YooAsset 的设计要点与常见陷阱。
- **architecture / patterns advisory 扩展** — architecture 新增"执行顺序与入口保护"章节；patterns 新增"Decision Lab"对比决策方法。
- **SKILL.md 统计同步** — 模块文档目录从 53 更新为 68（49 功能 + 19 Advisory）。

## [1.7.3] - 2026-04-20

### Added
- **Cinemachine 能力全面提升** — 新增 11 个 Skill（`cinemachine_set_priority`、`cinemachine_set_blend`、`cinemachine_set_brain`、`cinemachine_create_sequencer`、`cinemachine_sequencer_add_instruction`、`cinemachine_create_freelook`、`cinemachine_configure_camera_manager`、`cinemachine_configure_body`、`cinemachine_configure_aim`、`cinemachine_configure_extension`、`cinemachine_configure_impulse_source`），Cinemachine 技能总数从 23 增至 34，覆盖 Priority/Blend/Brain 配置、Sequencer/FreeLook 创建、Body/Aim 阶段统一配置、Extension 与 ImpulseSource 调节。
- **纹理导入器扩展** — 新增 `texture_get_import_settings`、`texture_find_assets`、`texture_get_info`、`texture_find_by_size`、`texture_set_type`、`texture_set_platform_settings`、`texture_get_platform_settings`、`texture_set_sprite_settings`、`sprite_set_import_settings`，支持按尺寸查找纹理、逐平台覆盖设置、Sprite 专属配置等。
- **音频导入器与场景技能扩展** — 新增 `audio_get_import_settings`、`audio_find_clips`、`audio_get_clip_info`、`audio_add_source`、`audio_get_source_info`、`audio_set_source_properties`、`audio_find_sources_in_scene`、`audio_create_mixer`，覆盖音频资产查询、AudioSource 场景管理和 AudioMixer 创建。
- **模型导入器扩展** — 新增 `model_get_import_settings`、`model_find_assets`、`model_get_mesh_info`、`model_get_materials_info`、`model_get_animations_info`、`model_get_rig_info`、`model_set_animation_clips`、`model_set_rig`，覆盖网格统计、材质/动画/骨骼信息查询、动画片段分割与绑定模式切换。
- **资产标签管理** — 新增 `asset_set_labels` 和 `asset_get_labels`，支持对资产设置和读取标签。

### Changed
- **CinemachineAdapter 架构抽象** — 新增 `CinemachineAdapter.cs` 适配层（562 行），将 Cinemachine 2.x 与 3.x 的 API 差异集中到适配器，`CinemachineSkills` 的业务方法不再包含条件编译，提高可维护性和可读性。
- **GameObjectFinder 增强** — 新增 `SafePathExists` 验证方法和 `EnsureDirectoryExists` 辅助方法，统一资产路径校验逻辑。
- **SkillPlanningService 批量分析重构** — 引入 `BatchAnalyzeContext` 上下文对象，将批量操作的 item 解析、错误收集、计划构建统一封装，消除多个 `Analyze*Batch` 方法间的重复代码。
- **UIToolkitSkills / GameObjectSkills 代码质量提升** — 提取公共 helper、消除重复模式、补全日志记录。

### Docs
- **importer 模块文档大幅扩展** — `importer/SKILL.md` 从基础导入设置扩展到涵盖查询、运行时、平台覆盖、动画/骨骼等完整能力矩阵。
- **cinemachine 模块文档补全** — `cinemachine/SKILL.md` 新增全部 11 个 Skill 的参数文档。
- **asset 模块文档更新** — `asset/SKILL.md` 新增 `asset_set_labels`、`asset_get_labels` 文档。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.3`。

## [1.7.2] - 2026-04-18

### Changed
- **SkillRouter 热路径性能优化** — `SkillInfo` 注册时预计算并缓存 `ParameterNames` 与 `AllowedParameterSet`，`Execute`/`DryRun` 的未知参数校验不再每次分配新的数组/`HashSet`；`GetSchema()` 新增结果缓存 `_cachedSchema`，避免每次调用都重新序列化 manifest,多次访问时开销降为常量。
- **BatchExecutor 反射结果缓存** — `BatchExecutor` 新增 `ConcurrentDictionary<Type, bool>` 缓存 `HasErrorMember` 反射结果，大规模批量操作时避免对每个 item 重复执行 `GetProperty("error")` / `GetField("error")`。
- **AsyncJobService 代码去重** — 将 Test 作业 filter 构建逻辑抽取为私有 `BuildTestFilter` 方法，消除 `StartTestRun` 与 Job reconnect 分支之间的重复块，修改测试过滤规则时只需改一处。
- **BatchJobService 日志写入统一** — 取消 Job 的日志写入改用现有 `AddLog()` 辅助方法，使所有日志落盘逻辑走同一条路径，便于后续统一格式或注入遥测。
- **SkillsHttpServer 清理冗余字段** — 移除 `RequestJob.ResponseDispatched` 与所有对它的赋值。该字段自引入以来从未被读取，删除后 request pool 状态更精简。

### Fixed
- **SKILL.md 幽灵引用修复** — skillcheck 审计发现 11 个模块（cinemachine/cleaner/optimization/profiler/project/scriptableobject/smart/terrain/timeline/batch 及 Routing 区块）的 `DO NOT` 章节指向了不存在的"纠正后 Skill"，AI 被误导后仍会调用 404 路由。统一替换为真实 Skill 名（例如 `optimize_compress_textures`→`optimize_compress_texture`、`terrain_set_heights`→`terrain_set_heights_batch`、`smart_query`→`smart_scene_query` 等）。
- **SKILL.md 反向错误修复** — `shader/SKILL.md` 将 `shader_get_properties` 错误标为"不存在"，实际该 Skill 存在且返回 shader 属性定义；`navmesh/SKILL.md` 同样把实际存在的 `navmesh_set_agent`、`navmesh_add_obstacle` 标记为幻觉。改为说明它们与 `component_*` 的分工关系，避免 AI 绕开真实 Skill。

### Docs
- **batch 模块补齐 Skill 文档** — `batch/SKILL.md` 新增 `batch_query_assets`（6 个参数含 `searchFilter`/`folder`/`typeFilter`/`namePattern`/`labelFilter`/`maxResults`）与 `batch_retry_failed`（`reportId`/`runAsync`/`chunkSize`）的完整表格，此前这两个 Skill 仅存在于 C# 但未在文档中暴露。
- **agent.md 目录结构同步** — `Editor/Skills/` 由 55 更新为 61 个 C# 文件；`unity-skills~/skills/` 由 54 更新为 53 个模块文档（40 functional + 13 advisory），匹配 v1.7.0 以来的 importer/workflow 目录合并结果。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.2`。

## [1.7.1] - 2026-04-17

### Added
- **机器可读 Skill Schema 端点** — 新增 `GET /skills/schema`，返回带 `schemaVersion`、参数定义、Skill 元数据和保留请求参数的结构化清单，便于 AI Agent 在低 token 场景下精确获取能力边界。
- **Python `get_skill_schema()`** — Python 客户端新增 `get_skill_schema()`，可直接读取服务端 canonical skill schema。
- **`test_smoke_skills` 回归验证 Skill** — Test 模块新增 `test_smoke_skills`，对安全只读技能直接执行、对其余技能走 dry-run，用于快速 smoke test 与发布前回归检查。

### Changed
- **Cinemachine 属性设置增强** — `cinemachine_set_vcam_property` 现支持 `fov`、`nearClip`、`farClip`、`orthoSize` 等镜头简写参数，设置常见 Lens 属性时更直接。
- **Skill 参数校验升级** — SkillRouter 现在会识别未知参数、返回允许参数列表，并对常见误传参数给出建议与提示；语义校验结果也会更完整地体现在 execute / dry-run / plan 响应中。
- **异步测试作业基础设施增强** — 异步测试与 smoke job 的作业管理能力得到扩展，提升测试型 Skill 的可观测性与复用性。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.1`。

### Fixed
- **Undo / Redo 事务边界修复** — 修正 REST 调用场景下的撤销分组与事务边界处理，改善 `editor_undo`、`editor_redo` 以及 workflow 撤销/重做的一致性。
- **只读组件查询副作用修复** — `ComponentSkills` 在读取 `Renderer` 材质属性时改用安全路径，避免查询型操作意外触发材质实例化。
- **场景感知与辅助行为稳定性提升** — 改进 `PerceptionSkills` 场景根对象枚举方式，并修复 `CanvasGroup` Undo 注册、书签空选择处理，以及 XR 场景报告对 ActionBased locomotion/turn provider 的识别。

### Docs
- **模块文档与参考资料增强** — 多个 `SKILL.md` 补充了更精确的签名、参数说明和调用示例，并为 importer、ProBuilder、UI、UI Toolkit、XR 等模块拆分出独立 reference 文档。
- **统计信息同步** — 文档统计已同步为 `41` 个功能模块、`543` 个 REST Skills，且 Test 模块更新为 `11` 个 Skills。

## [1.7.0] - 2026-04-15

### Added
- **统一异步 Job 基础设施 `AsyncJobService`** — 新增统一 Job 模型，接入 Test Runner、包管理和脚本变更监测。Job 状态、进度事件和日志通过 `BatchPersistence` 持久化到 `Library/UnitySkills/batch_state.json`，Domain Reload 后未完成作业会以 `reconnecting` 状态恢复。
- **批量处理服务 `BatchJobService`** — 新增批量作业执行器，负责 Batch 预览结果的排队、分块执行、取消、报告生成，以及与 `WorkflowManager` 的会话级联动。
- **Batch Skill 模块** — 新增 21 个 Batch/Job 相关 Skill，包括 `batch_query_*`、`batch_preview_*`、`batch_execute`、`batch_report_*`、`batch_retry_failed` 以及 `job_status`/`job_logs`/`job_list`/`job_wait`/`job_cancel`，形成查询、预览、执行、报告、重试与状态查询闭环。
- **作业模型与持久化** — 新增 `BatchModels.cs` 与 `BatchPersistence.cs`，定义批量作业、报告、预览、日志、进度事件等数据结构，并提供 JSON 持久化与 reload 后恢复逻辑。
- **Skill 规划服务 `SkillPlanningService`** — 新增通用规划引擎，为 Skill 提供参数校验、语义检查、变更预测、风险提示和 `serverAvailability` 预判；HTTP 层新增 `POST /skill/{name}?mode=dryRun` 与 `POST /skill/{name}?mode=plan`。
- **`workflow_plan` 聚合规划 Skill** — Workflow 模块新增 `workflow_plan`，可对多步 Skill 调用做聚合规划，输出总风险、依赖关系、分步计划与警告信息。

### Changed
- **Perception Skill 全面升级** — 新增 7 个场景感知 Skill（`scene_analyze`, `scene_health_check`, `scene_contract_validate`, `scene_component_stats`, `scene_find_hotspots`, `scene_diff`, `project_stack_detect`），现有 Skill 补充 `scene_export_report`、`scene_dependency_analyze`、`scene_context` 等能力，Perception 模块从 11 个 Skill 扩展到 18 个。
- **SkillRouter 增强** — `UnitySkillAttribute` 新增 `MutatesScene`、`MutatesAssets`、`MayTriggerReload`、`RiskLevel`、`RequiresPackages` 元数据；SkillRouter 初始化时构建输出索引并扩展中英文意图同义词表；新增 `DryRun`/`Plan` 请求模式。
- **技能文档规范化** — 14 个 SKILL.md 模块文档新增标准参数表（参数名/类型/必选/默认值/描述）和 Canonical Signature 块，所有 Skill 参数与 C# 方法签名完全一致。
- **Skill 数量提升** — 总计从 513 提升到 542（+29 个），功能模块从 40 个增加到 41 个（新增 Batch），Advisory 模块从 14 个调整为 13 个（移除 XR advisory，其内容合并到 xr/SKILL.md）。

### Fixed
- **Batch 作业恢复与状态一致性** — `BatchPersistence` 会在 Domain Reload 后将未终态 Job 规范化为 `reconnecting`，避免批量任务在重载后丢失执行状态。
- **Test Runner 回调泄漏** — `AsyncJobService` 在 Job 完成后显式 `UnregisterCallbacksFromObject`，防止回调对象泄漏导致内存占用持续增长。
- **Skill 路由中文匹配** — `SkillRouter` 新增中文同义词表，确保"创建方块"等中文自然语言能正确路由到 `gameobject_create`。

## [1.6.9] - 2026-04-03

### Added
- **依赖链查询端点 `/skills/chain`** — 新增 GET `/skills/chain?output=instanceId` 端点，基于 `Outputs` 元数据构建的反向索引，快速查找能产出指定字段的 Skill 链，支持 AI Agent 自动编排多步工作流。
- **Dry-Run 模式** — POST `/skill/{name}?dryRun=true` 仅验证参数合法性而不实际执行技能，返回参数解析结果和缺失必选参数提示，便于 Agent 预检查调用可行性。
- **意图解析增强** — `SkillRouter` 新增中英文同义词映射表（70+ 条目）、操作类型提取（Create/Delete/Query/Modify/Execute/Analyze）和分类关键词匹配，`/skills/recommend` 端点支持中文子串匹配（如"创建方块"直接匹配到 `gameobject_create`）。
- **输出索引** — `SkillRouter` 初始化时构建 `output field → producing skills` 反向索引，支撑依赖链查询和意图推荐的输出字段匹配。

### Fixed
- **Domain Reload 后服务器偶发不重启** — 新增 `ProcessJobQueue` 安全网机制：每 5 秒检测服务器是否应运行但未运行，自动触发恢复，不再完全依赖 `EditorApplication.delayCall`（该 API 在某些 Unity 版本/状态下可能不触发）。同时将端口释放等待从 500ms 增加到 2000ms，`delayCall` 恢复增加 1 秒延迟缓冲端口释放。
- **连续失败计数过于激进** — `MaxConsecutiveFailures` 从 5 提升到 10；新增 5 分钟时间衰减机制，距上次失败超过 5 分钟自动重置计数器，防止历史失败累积导致服务器永久放弃自动重启。
- **必选参数判定优化** — `SkillRouter` 新增 `IsParameterRequired()` 方法替代简单的 `!p.HasDefaultValue` 判断，正确处理值类型可空参数的必选性识别。

### Changed
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.6.9`。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.0`。

## [1.6.8] - 2026-04-03

### Fixed
- **`gameobject_create` parentName 参数失效** — 文档声称 `gameobject_create` 支持 `parentName` 参数，但 C# 实现中缺失该参数，SkillRouter 静默忽略导致子物体被创建在场景根层级而非预期父物体下。现已在方法签名中添加 `parentName`/`parentInstanceId`/`parentPath` 三种父物体标识参数，创建后自动 `SetParent`，坐标改为 `localPosition` 语义。
- **`gameobject_create_batch` 缺少 parent 支持** — `BatchCreateItem` 同步添加 `parentName`/`parentInstanceId`/`parentPath` 字段，批量创建时支持为每个物体指定父物体。
- **`prefab_instantiate` 缺少 parent 支持** — 新增 `parentName`/`parentInstanceId`/`parentPath` 参数，实例化后自动设置父物体，返回值新增 `path` 字段。
- **`prefab_instantiate_batch` 缺少 parent 支持** — `BatchInstantiateItem` 同步添加 parent 相关字段。

### Changed
- **文档与实现一致性修复** — `gameobject/SKILL.md` 补充 `parentInstanceId`/`parentPath` 参数说明，`prefab/SKILL.md` 同步更新参数表和 batch item 属性列表。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.6.8`。

## [1.6.7] - 2026-04-02

### Added
- **Intent-Level Skill Metadata (v1.7 Attributes)** — 全部 513 个 Skill 的 `[UnitySkill]` 特性新增 6 个结构化元数据字段：`Category`（SkillCategory 枚举）、`Operation`（SkillOperation Flags 枚举: Query/Create/Modify/Delete/Execute/Analyze）、`Tags`（语义标签数组）、`Outputs`（返回字段声明）、`RequiresInput`（前置依赖声明）、`ReadOnly`（纯查询标记）。
- **`/skills` 过滤查询 API** — GET `/skills` 端点支持 query string 过滤：`category`、`operation`、`tags`、`readOnly`、`q`（文本搜索 name+description+tags），多条件 AND 组合。无参数调用完全向后兼容。
- **`/skills/recommend` 意图推荐端点** — 新增 GET `/skills/recommend?intent=create+cube&topN=10`，基于关键词评分排序（name=3分, tags=2分, description=1分），返回 top-N 匹配 Skill 及 relevance score。
- **Metadata Validation 工具** — 编辑器 Skills 标签页新增 **Validate** 按钮，检查 6 条元数据规则（Category/Operation/Tags/Outputs 完整性、ReadOnly 与 TracksWorkflow 矛盾检测、Delete/Modify 操作的 RequiresInput 检查），结果输出到 Console。
- **Python 客户端增强** — `get_skills()` 新增 `category`/`operation`/`tags`/`read_only`/`q` 过滤参数；新增 `find_skills(intent, top_n)` 调用服务端推荐；新增 `get_skill_chain(target_output)` 查找产出特定字段的 Skill 链。

### Changed
- **SkillsHttpServer QueryString 管道** — `RequestJob` 新增 `QueryString` 字段，HTTP 请求的查询字符串从 `ListenLoop` 完整传递到 `ProcessJob`，支撑过滤和推荐端点。
- **SkillRouter Manifest 增强** — `/skills` manifest 新增 `categories` 和 `operationTypes` 顶层字段；每个 Skill 条目新增 `category`、`operation`、`tags`、`outputs`、`requiresInput`、`readOnly` 字段。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.6.7`。

## [1.6.6] - 2026-03-26

### Fixed
- **CM3 `PrioritySettings` 编译兼容修复** — `CinemachineSkills.cs` 不再依赖 `PrioritySettings` 的隐式 `int` 转换；`cinemachine_create_vcam` 和 `cinemachine_set_active` 统一改为使用 `Priority.Value` 这一最低公共 API，修复 Unity 2022.3 项目在安装部分 `Cinemachine 3.0.x` 预览版本时出现的 `CS0029` / `CS0030` 编译错误。
- **`cinemachine_set_active` 返回值修正** — CM3 分支返回消息现在输出实际优先级数值，而不是 `PrioritySettings` 结构体对象文本。

### Changed
- **CM3 支持边界说明** — 文档明确当前 `Cinemachine 3` 兼容策略以 `3.0.0-pre.5+ / stable 3.x` 的共同 API 为基线；更早的 `3.0.0-pre.1/.2` 因核心相机 API 仍在演化，不纳入当前兼容范围。
- **Package Manager 元数据补全** — `SkillsForUnity/package.json` 新增 `changelogUrl`，Unity Package Manager 的 `Changelog` 按钮现在会跳转到仓库中的 `CHANGELOG.md`。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和安装文档同步提升到 `1.6.6`。

## [1.6.5] - 2026-03-20

### Added
- **SKILL.md 双模式系统 (Semi-Auto / Full-Auto)**：全新操作模式机制，默认半自动模式仅激活 ~80 个高频 Skills（script/perception/scene/editor/asset/workflow/debug/console + 14 advisory），大幅降低 Token 消耗；用户说"全自动模式"/"full auto" 时激活全部 513 Skills。
- **防幻觉 Guardrails**：全部 39 个功能模块 SKILL.md 新增 `## Guardrails` 段落，包含 Mode 声明、DO NOT 幻觉列表（常见不存在的 Skill 名称和参数错误）、Routing 路由提示（引导 AI 使用正确模块）。
- **Skill 命名约定**：模块索引新增 `Skill Naming Convention` 段落，列出全部有效 module 前缀，AI 可据此判断 Skill 是否存在。
- **Advisory 模块触发词优化**：14 个 advisory 模块的 YAML description 全面升级，增加自然语言触发词（"怎么组织代码"、"太慢了"、"用什么模式好" 等）和问题描述型触发词，中英文覆盖更智能。
- **`component_set_property` 新增 `assetPath` 参数**：支持将 Project Asset（ScriptableObject、Prefab、Material、Texture、AudioClip 等）赋值给组件的 Object 引用字段。通过 `AssetDatabase.LoadAssetAtPath` 加载资产，支持精确类型匹配与降级兼容。`component_set_property_batch` 同步支持。
- **`prefab_set_property` (1 skill)**：新增 Prefab 资产属性编辑技能，通过 `SerializedObject` + `SerializedProperty` 直接编辑 Prefab Asset 文件的组件字段（无需实例化到场景）。支持：
  - 基本类型（int/float/bool/string/enum）、Vector2/3/4、Color、Rect、Bounds、LayerMask
  - Asset 引用赋值（通过 `assetReferencePath` 参数）
  - Prefab 内子对象编辑（通过 `gameObjectName` 参数）
  - 属性名自动回退（`propertyName` → `m_PropertyName` → `_propertyName` → `m_propertyName`）

### Changed
- **SKILL.md 模块索引新增 Mode 列**：38 个功能模块标注 SA (Semi-Auto) 或 FA (Full-Auto)，14 个 advisory 模块明确标注"双模式均可用"。
- **主入口 SKILL.md**：YAML description 新增 16 个中英文触发词（含模式切换词）；Core Rule #2 添加 `[Full-Auto]` 前缀；workflow/gameobject 示例标注为 Full-Auto 示例。
- **全部模块 SKILL.md 触发词标准化**：54 个 SKILL.md 文件均包含标准 `Triggers:` 格式的中英文双语触发词。
- **REST Skills 总数**：512 → 513（+1 prefab_set_property）。
- **Prefab 模块**：10 → 11 skills。

### Docs (SKILL.md 文档质量审计与修复 — 26 文件)
- **Batch Skill 返回值补全**：为 9 个模块共 25 个 batch 技能补全 `**Returns**` 结构文档：gameobject（9）、component（3）、light（2）、material（4）、asset（3）、importer（3）、prefab（1）、ui（1）、script（1）。
- **三元定位参数补全**：修复 `gameobject_set_transform`、`gameobject_set_parent`、`gameobject_set_active` 缺失的 `instanceId`/`path` 参数文档；`gameobject_set_parent` 参数名从 `name`/`parentName` 修正为 C# 实际签名 `childName`/`childInstanceId`/`childPath`/`parentName`/`parentInstanceId`/`parentPath`（6 参数完整文档）。
- **Object Targeting 统一说明**：为 gameobject、component、light、material 四个功能模块顶部新增"Object Targeting"注释段落，说明所有单对象 Skill 支持 `name`/`instanceId`/`path` 三元定位。
- **Batch 参数文档增强**：`gameobject_set_parent_batch` 补全 6 参数说明 + 三种定位方式示例；`light_set_properties_batch` 补全全部可用参数（identifier + `r`/`g`/`b`/`intensity`/`range`/`shadows`）+ 混合示例。
- **`component_set_property` 类型示例**：新增 5 种 `value` 参数类型的用法示例（float/bool、Vector3 JSON、Color JSON、Enum 字符串），消除类型模糊导致的幻觉风险。
- **Component 技能可见性**：`component_set_enabled` 和 `component_copy` 从 "Additional Skills" 部分提升到 Skills Overview 表格，提高 AI 发现率。
- **Advisory 模块 Mode 自声明**：13 个 advisory 模块（architecture/adr/performance/asmdef/blueprints/script-roles/scene-contracts/testability/patterns/async/inspector/scriptdesign/project-scout）新增 `**Mode**: Both (Semi-Auto + Full-Auto) — advisory only, no REST skills` 声明，实现 52/52 模块 SKILL.md 全覆盖 Mode 标记（8 SA + 31 FA + 13 Both）。
- **缺失 Guardrails 补建**：为 patterns、async、inspector 三个 advisory 模块新建 `## Guardrails` 段落（含 Mode 声明和反模式指导），实现全部 advisory 模块 Guardrails 100% 覆盖。
- **Cinemachine deprecated 增强**：`cinemachine_add_component` 弃用标记从行内括号改为醒目 blockquote `> **DEPRECATED**`，并补全 `instanceId`/`path`/`componentType` 参数文档。
- **`cinemachine_set_targets` 参数补全**：新增 `instanceId` 和 `path` 参数说明，支持精确定位 VCam。
- **Terrain layerIndex 说明**：`terrain_paint_texture` 的 `layerIndex` 参数补充"0-based"说明和 `terrain_get_info` 查询引导。

## [1.6.4] - 2026-03-15

### Added
- **XRSkills (22 skills)**：新增 `XRSkills.cs` XR Interaction Toolkit 技能模块 + `XRReflectionHelper.cs` 反射辅助类，通过纯反射实现 XRI 2.x（Unity 2022）/ 3.x（Unity 6）跨版本兼容，无需编译期依赖 XRI 程序集。包含：
  - **Setup & Validation (5 skills)**：`xr_check_setup`（全面检查 XR 项目配置）、`xr_setup_rig`（创建完整 XR Origin Rig 含 Camera/Left/Right Controller 层级）、`xr_setup_interaction_manager`（添加 XRInteractionManager）、`xr_setup_event_system`（替换 StandaloneInputModule 为 XRUIInputModule）、`xr_get_scene_report`（XR 场景诊断报告）
  - **Interactor Skills (4 skills)**：`xr_add_ray_interactor`（射线交互器 + LineRenderer）、`xr_add_direct_interactor`（近距离抓取 + SphereCollider trigger）、`xr_add_socket_interactor`（插座交互器）、`xr_list_interactors`（列出所有交互器）
  - **Interactable Skills (4 skills)**：`xr_add_grab_interactable`（可抓取物体 + Rigidbody + Collider + movementType 配置）、`xr_add_simple_interactable`（简单交互）、`xr_configure_interactable`（配置交互属性）、`xr_list_interactables`（列出所有交互物体）
  - **Locomotion Skills (5 skills)**：`xr_setup_teleportation`（传送提供者）、`xr_add_teleport_area`（传送区域）、`xr_add_teleport_anchor`（传送锚点 + 可视化指示器）、`xr_setup_continuous_move`（连续移动）、`xr_setup_turn_provider`（Snap/Continuous 转向）
  - **Advanced Skills (4 skills)**：`xr_setup_ui_canvas`（Canvas XR 兼容化 + TrackedDeviceGraphicRaycaster）、`xr_configure_haptics`（触觉反馈）、`xr_add_interaction_event`（交互事件绑定）、`xr_configure_interaction_layers`（交互层配置）
- **XR Advisory 模块**：新增 `skills/xr/SKILL.md` 文档，包含 6 个 XR 开发工作流指南（Rig 搭建、抓取交互、传送系统、连续移动、XR UI、交互事件与反馈）、组件依赖关系图、movementType 选择指南、版本兼容性说明。
- **XRReflectionHelper 反射辅助**：25+ XR 类型的版本映射表（XRI 3.x 子命名空间 → 2.x 根命名空间 fallback），缓存类型解析，版本自动检测（通过命名空间探测区分 2.x/3.x）。

### Changed
- **asmdef versionDefines 扩展**：`UnitySkills.Editor.asmdef` 新增 `XRI`（`com.unity.xr.interaction.toolkit [2.0,4.0)`）和 `XR_CORE_UTILS`（`com.unity.xr.core-utils [2.0,4.0)`）条件编译符号。不添加 XRI 程序集引用（纯反射）。
- **REST Skills 总数**：490 → 512（+22 XR skills）。
- **Skills 文件数**：39 → 40 个 `*Skills.cs` 文件。
- **Advisory 模块数**：13 → 14（+1 XR advisory）。

## [1.6.3] - 2026-03-14

### Added
- **ProBuilderSkills (22 skills)**：新增 `ProBuilderSkills.cs` 模块，通过条件编译 `#if PROBUILDER` 支持可选包 `com.unity.probuilder`（5.x–7.x）。未安装时所有 skill 返回友好错误提示。包含：
  - `probuilder_create_shape`：创建参数化 ProBuilder 形状（Cube/Sphere/Cylinder/Cone/Torus/Prism/Arch/Pipe/Stairs/Door/Plane），支持位置、尺寸、旋转设置，支持 `parent` 参数指定父对象
  - `probuilder_extrude_faces`：面拉伸（IndividualFaces/FaceNormal/VertexNormal 三种模式）
  - `probuilder_subdivide`：细分整个网格或指定面
  - `probuilder_bevel_edges`：边倒角，支持顶点索引对指定
  - `probuilder_delete_faces`：按索引删除面
  - `probuilder_merge_faces`：合并多个面为单个面
  - `probuilder_set_face_material`：按面设置材质（支持 materialPath 或 submeshIndex）
  - `probuilder_flip_normals`：翻转面法线方向
  - `probuilder_get_info`：获取网格完整信息（顶点/面/边/三角形数、形状类型、材质列表、submesh 分布）
  - `probuilder_center_pivot`：居中枢轴或设置到指定世界坐标
  - `probuilder_create_batch`：批量创建多个 ProBuilder 形状
  - `probuilder_move_vertices`：按增量移动顶点（用于斜坡/坡道等造型）
  - `probuilder_set_vertices`：设置顶点绝对位置
  - `probuilder_get_vertices`：查询顶点位置信息
  - `probuilder_combine_meshes`：合并多个 ProBuilder 网格
  - `probuilder_set_material`：设置整个网格的材质（支持颜色快捷方式）

- **UISkills 新增 10 skills**（16 → 26）：基于 UGUI 源码实现完整控件层级结构：
  - `ui_create_dropdown`：创建 Dropdown（含 Template/ScrollRect/Viewport/Content/Item 完整子层级和 Toggle 选项），支持逗号分隔的选项列表
  - `ui_create_scrollview`：创建 ScrollRect 滚动视图（含 Viewport + RectMask2D + Content），支持方向和 MovementType 配置
  - `ui_create_rawimage`：创建 RawImage 元素（用于 Texture2D/RenderTexture 显示）
  - `ui_create_scrollbar`：创建独立 Scrollbar（含 Sliding Area + Handle），支持四方向和离散步数
  - `ui_set_image`：设置 Image 高级属性（Type: Simple/Sliced/Tiled/Filled、FillMethod: Radial360/Horizontal/Vertical 等、fillAmount、preserveAspect）
  - `ui_add_layout_element`：添加/配置 LayoutElement（minWidth/preferredWidth/flexibleWidth 等完整布局约束）
  - `ui_add_canvas_group`：添加/配置 CanvasGroup（alpha/interactable/blocksRaycasts/ignoreParentGroups）
  - `ui_add_mask`：添加 Mask（模板缓冲）或 RectMask2D（矩形裁剪）
  - `ui_add_outline`：添加 Shadow 或 Outline 视觉效果（颜色、距离、graphicAlpha）
  - `ui_configure_selectable`：配置 Selectable 属性（Transition 模式、ColorBlock 四态颜色、Navigation 模式）

- **UIToolkitSkills 新增 10 skills + 4 模板**（15 → 25）：基于 UI Toolkit 源码示例实现 UXML/USS 程序化操作：
  - `uitk_add_element`：向 UXML 添加元素（Label/Button/Toggle/Slider/TextField 等），支持 name/text/classes/style/binding-path
  - `uitk_remove_element`：从 UXML 按 name 删除元素
  - `uitk_modify_element`：修改 UXML 元素属性（text/classes/style/name/binding-path/自定义属性）
  - `uitk_clone_element`：复制 UXML 元素（含子元素）
  - `uitk_add_uss_rule`：向 USS 文件添加或更新样式规则（自动检测已有选择器并替换）
  - `uitk_remove_uss_rule`：从 USS 删除指定选择器的规则
  - `uitk_list_uss_variables`：提取 USS 中所有 CSS 自定义属性定义和 `var()` 引用
  - `uitk_create_editor_window`：生成 EditorWindow C# 脚本模板（CreateGUI + UXML/USS 绑定 + MenuItem）
  - `uitk_create_runtime_ui`：生成运行时 MonoBehaviour 脚本（UIDocument 查询 + 事件注册/注销模式）
  - `uitk_inspect_document`：检查场景中 UIDocument 的 VisualElement 实时层级树（类型/名称/类列表/子节点）
  - 新增 4 个 `uitk_create_from_template` 模板：`tab-view`（标签页切换）、`toolbar`（顶部工具栏）、`card`（卡片组件）、`notification`（通知/Toast）

- **`ui_create_batch` 扩展**：批量创建支持新增的 `dropdown`、`scrollview`、`rawimage`、`scrollbar` 类型。

### Changed
- **asmdef 引用扩展**：`UnitySkills.Editor.asmdef` 新增 `Unity.ProBuilder` + `Unity.ProBuilder.Editor` 引用及 `PROBUILDER` versionDefines（`[5.0,7.0)`），与 Cinemachine 条件编译模式一致。
- **REST Skills 总数**：448 → 490（+10 ProBuilder + 6 ProBuilder新增 + 6 ProBuilder高级 + 10 UGUI + 10 UIToolkit）。
- **Skills 文件数**：38 → 39 个 `*Skills.cs` 文件。

## [1.6.2] - 2026-03-13

### Added
- **13 个 advisory 设计模块**：在 `unity-skills/skills/` 下新增架构、脚本职责、异步策略、ADR、Inspector 设计、性能审视、可测试性等建议型模块，用于辅助 AI 在真正写脚本前先做设计判断。
- **Workflow 会话恢复上下文补充**：工作流状态额外暴露 `currentTaskDescription` 等信息，便于 Python 客户端在超时或短暂断连后恢复上下文。
- **脚本编写后的编译反馈提示**：脚本相关 Skill/文档补充“保存后等待 Unity 自动编译，再主动检查错误日志并继续迭代修复”的引导。

### Changed
- **Unity 维护基线**：后续新增功能、回归验证与文档基线统一收敛到 `Unity 2022.3+`，当前重点适配目标为 `2022.3+ / Unity 6`。
- **Skill 模板目录迁移**：根目录 `unity-skills/` 已移入 UPM 包内 `SkillsForUnity/unity-skills~/`（波浪线隐藏目录），通过 `?path=SkillsForUnity` 安装时自动随包分发，无需完整克隆仓库。
- **默认请求超时统一**：Unity 服务端、Python 客户端和用户文档统一以 **15 分钟** 作为默认超时。
- **脚本生成建议增强**：创建脚本相关提示中，明确要求 AI 主动考虑耦合性、性能、可维护性与 Inspector 体验，而不是只生成可运行代码。
- **文档与安装说明同步**：更新 `README.md`、`README_EN.md`、`docs/SETUP_GUIDE.md`、`unity-skills/SKILL.md`、`.github` 文档等，补充 `447` 个 REST Skills、`13` 个 advisory 模块、完整 `unity-skills/` 模板目录与编译期短暂不可达说明。

### Fixed
- **`DebugSkills.cs` 编译错误**：修复 `debug_get_logs` 读取日志时 `LogEntryInfo` 对象初始化器使用了不合法的 `file, line` 简写，改为显式成员赋值，解决 `CS0747 Invalid initializer member declarator` 编译失败。
- **Workflow 历史可靠性**：为历史数据增加 `schemaVersion` 与迁移处理；补全 `.tmp` 崩溃恢复；恢复历史、撤销、重做时重新校验资产路径，避免信任旧磁盘记录导致的不一致或越界访问。
- **Workflow 快照去重性能**：`SnapshotObject()` 的去重由线性扫描改为 `HashSet<string>` 索引，避免批量操作时退化为 `O(n^2)`。
- **Workflow 追踪技能维护方式**：去掉 `SkillRouter` 中硬编码的工作流追踪列表，改为基于 `[UnitySkill(TracksWorkflow = true)]` 自动发现，降低后续漏配风险。
- **多场景对象查找**：`GameObjectFinder` 不再只搜索活跃场景，改为遍历所有已加载场景，修复多场景编辑下查找失败的问题。
- **REST 服务稳态与恢复**：`SkillsHttpServer` 增强监听线程准入限流、请求对象池、watchdog/keep-alive 恢复链路，并在脚本编译、Define 变更、资源重导入、包操作等导致短暂不可达时返回明确“稍后重试”提示。
- **重复限流逻辑**：移除主线程侧重复限流，统一由监听阶段执行请求准入，避免双重限流带来的行为不一致。
- **Python 客户端稳定性**：统一默认超时为 `900` 秒，复用 `requests.Session`，增强注册表损坏诊断、CLI 数值解析、可重试传输错误识别，以及 `WorkflowContext` 在超时/断连后的状态恢复。
- **文件 I/O 一致性**：统一更多文件读写为 UTF-8，补齐 `SafePath` 校验顺序，修复部分 Skill 先访问文件再校验路径的问题。
- **批处理与依赖分析性能**：清理批处理实现分裂，更多路径统一走 `BatchExecutor`；`CleanerSkills` 先建立依赖缓存再分析，移除重复 `GetDependencies()` 带来的性能瓶颈。
- **文档与注释编码问题**：修复一批中文文本与注释的编码异常，避免 AI 读取和用户查看时出现乱码。
- **Perception 模块 Skill 计数**：修正文档中 Scene/Asset/Audio/Texture/Model/Perception 六个模块的 Skill 数量偏差，补齐 `scene/SKILL.md` 遗漏的 `scene_find_objects`、`perception/SKILL.md` 遗漏的 `scene_tag_layer_stats` 和 `scene_performance_hints`，以及 `skills/SKILL.md` 缺失的 `uitoolkit` 行。
- **AnimatorSkills 值类型拷贝 Bug**：`AnimatorControllerParameter` 是值类型，`FirstOrDefault` 返回副本导致修改默认值不生效，改用 `Array.FindIndex` 直接修改原数组元素。
- **PrefabSkills 变体创建**：`prefab_create_variant` 使用 `SaveAsPrefabAsset` 创建的是普通 Prefab 而非 Variant，改用 `SaveAsPrefabAssetAndConnect` 正确创建变体。
- **MaterialSkills 内存泄漏**：`material_create` 未指定 `savePath` 时 `new Material()` 无人引用且无法被 GC 回收，现返回 `instanceId` 和警告信息供调用方后续引用或销毁。
- **ConsoleSkills 线程安全**：`_logs` 列表在 `OnLogMessage` 回调（可能来自后台线程）与 Skill 方法（主线程）之间无同步，添加 `lock` 保护所有读写操作。
- **PhysicsSkills 方向向量未归一化**：`physics_raycast`/`physics_raycast_all`/`physics_spherecast`/`physics_boxcast` 的 `direction` 参数未归一化导致 `maxDistance` 语义不正确，添加归一化和零向量检查。
- **BatchExecutor error 误计为成功**：`processor` 返回含 `error` 字段的对象不会触发 catch 而被计入 `successCount`，添加反射检测 error 字段逻辑，正确计入 `failCount`。
- **TextureSkills/ModelSkills 注释编码乱码**：修复 GBK 编码被误读为 UTF-8 导致的注释乱码（`TextureSkills.cs` L66、`ModelSkills.cs` L81）。
- **Python `call_skill_with_retry` 重试语义**：`max_retries=3` + `range(max_retries)` 实际只有 3 次尝试，与参数名"最多重试 3 次"语义不一致，改为 `range(1 + max_retries)` 确保总尝试次数为 `1 + max_retries`。
- **SKILL.md 文档补全**：补全 28 个模块约 166 个 Skill 的缺失文档条目，删除 `debug/SKILL.md` 中的幽灵条目 `debug_log`。
- **对象池无上限增长**：`SkillsHttpServer` 的 `ConcurrentBag<RequestJob>` 无上限归还，超过 `MaxPendingRequests` 时 Dispose 而非归还；超时 job 也无条件归还池中。
- **SkillRouter verbose 参数泄漏**：框架参数 `verbose` 读取后未从 `args` 移除，导致后续参数绑定可能出现类型不匹配错误。
- **SkillRouter manifest 缓存竞态**：`_cachedManifest` 读写无同步保护，并发调用可能导致重复构建或读到半初始化值，添加 double-check locking。
- **RegistryService 原子写入恢复**：`AtomicReadModifyWrite` 在崩溃后 `.tmp` 文件残留但主文件为空时无恢复机制，新增启动时 `.tmp` 备份恢复检查。
- **WorkflowManager UndoSession 去重逻辑**：`sessionTasks` 倒序收集快照导致去重保留中间状态而非原始状态，改为正序（`OrderBy`）收集以确保去重保留最旧快照。
- **FindObjectsOfType 过时 API**：全部 36 处 `Object.FindObjectsOfType<T>()` 替换为 `FindHelper.FindAll<T>()`，在 Unity 6+ 自动使用 `FindObjectsByType` 以消除性能警告。
- **NavMeshSkills TracksWorkflow 误标**：`navmesh_set_area_cost` 影响全局状态无法 SnapshotObject，移除 `TracksWorkflow` 并添加 `areaIndex`/`cost` 参数校验。
- **AnimatorSkills switch 缺 default**：`AnimatorControllerParameterType` switch 添加 `default: break;` 避免潜在的未处理枚举值。
- **代码去重 GetGameObjectPath**：`CleanerSkills` 和 `EditorSkills` 中重复的私有 `GetGameObjectPath` 方法改为调用 `GameObjectFinder.GetPath()`。
- **代码去重 ConvertValue**：`ScriptableObjectSkills` 删除私有 `ConvertValue`，改为调用 `ComponentSkills.ConvertValue`（已提升为 `internal`）。
- **ValidationSkills 优化**：删除未使用的 `rootObjects` 变量；`FindUnusedAssets` 由 O(n²) 嵌套依赖查询改为预构建依赖索引 O(n)。
- **ComponentSkills 缓存无上限**：`_typeCache`/`_memberCache` 超过 500 条时自动清空，防止长时间运行后内存增长。
- **ComponentSkills 兼容性**：`string.Contains(StringComparison)` 需要 .NET Standard 2.1+，改为 `IndexOf` 兼容旧运行时。
- **ComponentSkills 死代码**：删除未使用的 `GetTypeConversionHint` 方法。
- **LightSkills shadow switch 缺 default**：光源阴影类型 switch 添加 default 分支返回警告；批量操作中 `light.range` 添加光源类型检查。
- **ScriptSkills 参数校验**：`script_find_in_file` 添加 `pattern` 必填检查和 `Directory.Exists` 检查。
- **ShaderSkills 健壮性**：`shader_create` 添加 `shaderName` 必填检查；`File.Exists` 添加空字符串保护；提取 `FindShaderByNameOrPath` 消除重复查找逻辑。
- **EventSkills 异常保护**：`event_invoke` 的反射调用添加 try-catch 防止未处理异常。
- **SmartSkills 性能**：`GetTypeByName` 的类型字典从方法内部提升为类级 `static readonly` 字段；`fieldName` 添加空字符串检查。
- **CameraSkills 资源泄漏**：`camera_screenshot` 添加 try/finally 保证 RenderTexture/Texture2D 释放；保存/恢复原始 `targetTexture`；`camera_create` 的 AudioListener 改为可选参数。
- **PackageSkills 语义修正**：安装返回状态改为 `installing` 明确异步语义；`package_search` 描述明确为搜索已安装包。
- **ProjectSkills 结构化返回**：`project_get_packages` 返回解析后的 JSON 对象而非原始文本；`project_add_tag` 添加参数校验。
- **TestSkills 资源管理**：测试完成回调中清理过期条目（1小时）；Domain Reload 时清理 `_api` 和 `_runningTests`；`test_cancel` 返回说明限制的 note。
- **ProfilerSkills 返回值修正**：`GetStatFloat` 返回 `float?` nullable 代替 `-1f` 哨兵值；`profiler_get_stats` 添加 `success = true`。
- **OptimizationSkills 健壮性**：`optimize_textures` 添加 `limit` 参数；LOD 距离解析改用 `TryParse`；`FindDuplicateMaterials` 添加近似比较说明。
- **NavMeshSkills navmesh_clear**：返回中添加不可撤销警告。
- **ScriptableObjectSkills 类型查找**：`FindScriptableObjectType` 改用 `OrdinalIgnoreCase` 大小写不敏感匹配。
- **CleanerSkills 路径格式**：`cleaner_find_large_assets` 将绝对 OS 路径转换为 Unity 相对路径。
- **Python 线程安全**：`_auto_workflow_enabled` 和 `_current_workflow_active` 全局标志添加 `threading.Lock` 保护。

### Server Resilience (Domain Reload 韧性强化)
- **Domain Reload 重启意图丢失修复**：3 次重试全部失败后 `OnBeforeAssemblyReload` 将 `PREF_SERVER_SHOULD_RUN` 覆写为 `false` 导致服务器永久死亡。修复为：仅在 `_isRunning=true` 时写入 true、`Start()` 失败时不清除重启意图，保留跨 Reload 的恢复机会。
- **跨 Reload 连续失败追踪**：新增 `PREF_CONSECUTIVE_FAILURES` 持久化计数器，每轮重试（3 次 + 指数退避）全部失败时 +1，累计达到 5 次后放弃自动重启并输出明确日志提示用户手动启动，防止无限重试循环。成功启动或用户显式停止/退出编辑器时清零。
- **Watchdog Keep-Alive 线程监控**：Watchdog 在 listener 线程健康时额外检测 Keep-Alive 线程存活状态，死亡则自动重启新线程，避免 Unity 失焦后静默失去后台唤醒能力。
- **504 超时响应增强**：新增 `diagnostics` 对象（domainReloadPending / queuedRequests / listenerAlive / keepAliveAlive）、`manualAction` 操作指引、动态 `retryAfterSeconds`（Reload 期间 5s，否则 10s），帮助 AI Agent 自主判断重试策略。
- **503 编译中响应增强**：新增 `diagnostics` 对象（isCompiling / isUpdating / domainReloadPending）、`manualAction` 操作指引、动态 `retryAfterSeconds`（Reload 期间 8s，否则 5s）。
- **`/health` 端点结构化诊断**：新增 `threads`（listenerAlive / keepAliveAlive）、`compilation`（isCompiling / isUpdating / domainReloadPending）、`queueStats`（queued / totalReceived）三组诊断字段，所有旧字段保持向后兼容。
- **SkillRouter 集中注入 `serverAvailability`**：新增 `SerializeSuccessResponse()` 辅助方法，编译进行中时自动向所有 Skill 的成功响应注入 `serverAvailability` 提示（已有该字段的响应不重复注入），无需每个 Skill 单独处理。

### Enhanced
- **新增 `script_dependency_graph` Skill**：给定入口脚本，BFS 双向扩展 N 跳依赖闭包，返回结构化 JSON（脚本列表含 fields/unityCallbacks、边列表、Kahn 拓扑排序的 suggestedReadOrder），帮助 AI 仅加载必要脚本上下文而非全量源码。（REST Skills 447 → 448）
- **`scene_context` 增强**：新增 `includeCodeDeps` 参数，开启后一次调用即可获取场景结构 + 代码级依赖 JSON，解决此前需分别调用 `scene_context` 和 `scene_export_report` 的割裂问题。
- **`RxGetComponent` 正则补强**：扩展为 `(?:Get|Add)Component<T>` 以覆盖 `AddComponent<T>()` 这一明确的依赖声明，减少代码依赖图遗漏边。

## [1.6.1] - 2026-03-11

### Fixed
- **Unity 2021.3 / 2022.3 early patch compatibility**: `PanelSettings.referenceSpritePixelsPerUnit` does not exist in Unity 2021.3 ~ 2022.3 early patches (e.g. 2022.3.17). Changed to reflection-based access to avoid CS1061 compile errors across all Unity versions.
- **Server recovery hardening**: Reduced keep-alive wake interval default to 10s, reduced watchdog interval to 5s, added proactive listener health recovery, and exposed recovery state in `/health` for easier diagnosis.
- **Safer script-domain disruption hints**: Added `serverAvailability` feedback for script edits, test script creation, forced recompilation, define changes, script-related asset reimport/import/move/delete, and package install/remove flows.
- **Path validation coverage**: Added missing file-name/path validation for controller, mixer, and physics material creation; blocked `asset_import` directory misuse from turning into a 500; tightened cleaner preview/usage checks to stay inside `Assets/` or `Packages/`.
- **Package startup stability**: Disabled automatic package auto-install on editor startup by default to avoid unexpected package-triggered recompilation and transient server drops.
- **Workflow/history safety**: Re-validated restored history asset paths and cleaned up script/timeline/test/package helper edge cases discovered during the stability audit.

### Changed
- **Default request timeout**: Changed the configurable server request timeout default from 60 minutes to 15 minutes.
- **Python helper version**: Aligned `unity_skills.py` client version metadata to `1.6.1`.

## [1.6.0] - 2026-03-06

### Added
- **UI Toolkit Module**: New `UIToolkitSkills.cs` with 15 `uitk_*` skills covering UXML/USS file operations, UIDocument scene management, PanelSettings full property create/get/set (27+ properties including Unity 6 World Space/collider support), UXML structure inspection, 6 built-in templates (menu/hud/dialog/settings/inventory/list), and batch file creation.

### Changed
- **Skills count**: Total skills increased from 431 to 446.

## [1.5.5] - 2026-03-05

### Changed
- **API Standardized**: Unified GameObject parameters to `name`, `instanceId`, and `path` across all core modules including `Prefab`, `Editor`, `Event`, `Camera`, `Timeline`, `UI`, `Component`, and `Cinemachine`. Standardized inconsistent names like `gameObjectName`, `objectName`, `directorObjectName`, and `parentName` to prevent AI hallucinations and parameter mismatch errors.
- **Enhanced Routing**: Added support for `instanceId` and `path` selection to multiple previously restricted skills (e.g., `timeline_play`, `component_copy`, `prefab_unpack`), enabling precise targeting in complex scenes.
- **Component Copy Upgrade**: `component_copy` now supports comprehensive routing via `sourceName/sourceInstanceId/sourcePath` and `targetName/targetInstanceId/targetPath`.
- **Doc Alignment**: Updated all `SKILL.md` manifest files and `SETUP_GUIDE.md` to reflect unified parameter naming conventions.

### Fixed
- **CS1737 Compiler Error**: Resolved "Optional parameters must appear after all required parameters" by ensuring all trailing parameters in modified skill signatures have appropriate default values (`null` or `0`). This makes the API more robust and AI-friendly.

## [1.5.4] - 2026-03-03

### Changed

- 版本号升级至 v1.5.4
- 合并来自silwings1986的PR-add Cursor install in AI Config

## [1.5.3] - 2026-03-01

### Security

- **`script_read` 路径遍历漏洞** — 新增 `Validate.SafePath()` 校验，阻止通过 `scriptPath` 参数读取 Assets/Packages 目录外的任意系统文件（如 `C:\Windows\System32\drivers\etc\hosts`）。（`ScriptSkills.cs`）
- **`shader_read` 路径遍历漏洞** — 同上，新增 `Validate.SafePath()` 校验。（`ShaderSkills.cs`）
- **`script_find_in_file` folder 路径遍历漏洞** — `folder` 参数新增 `Validate.SafePath()` 校验，阻止扫描 Assets 目录以外的文件系统路径。（`ScriptSkills.cs`）
- **`scene_screenshot` filename 路径穿越** — `filename` 参数改用 `Path.GetFileName()` 剥离所有路径前缀，确保截图始终保存至 `Assets/Screenshots/` 目录内，传入 `../../test.png` 等路径均安全截断为文件名。（`SceneSkills.cs`）

### Fixed

- **`debug_get_errors` / `debug_get_logs` 始终返回空列表** — 修复 `LogEntry.mode` bitmask 值与 Unity 内部枚举（UnityCsReference）不一致导致所有日志被过滤的根本问题。原 `errorMask = 1|2|16|32|64|128 = 243` 不含 `ScriptingError = 256`（bit 8），导致 `Debug.LogError()` 产生的日志 `256 & 243 = 0` 被全部跳过；`logMask = 4` 不含 `ScriptingLog = 1024`，`Debug.Log()` 同理。修正后：`ErrorModeMask = 1|2|16|64|256|2048|131072`，`LogModeMask = 4|1024`，`WarningModeMask = 128|512|4096`，完整覆盖所有 Unity 日志类型。感谢 **@RubingHan** 发现并报告此问题。（`DebugSkills.cs`）
- **`debug_*` 反射性能与稳定性** — 反射成员改为静态字段缓存（首次调用后复用），新增 `BindingFlags.NonPublic` 确保跨 Unity 版本兼容，提取 `ReadLogEntries()` 共享方法消除代码重复，用 `try/finally` 包裹 `EndGettingEntries()` 修复原资源泄漏风险，反射失败时清空缓存字段允许下次重试。（`DebugSkills.cs`）
- **`PhysicMaterial` Unity 6 向后兼容** — `physics_create_material` / `physics_set_material` 用 `#if UNITY_6000_0_OR_NEWER` 区分 `PhysicsMaterial`（Unity 6+）和 `PhysicMaterial`（Unity 2021.3–2022），确保双版本编译通过。（`PhysicsSkills.cs`）
- **`Assembly` 命名空间歧义编译错误** — 修复同时 `using UnityEditor.Compilation` 和 `using System.Reflection` 导致的 `CS0104` 歧义错误，改为 `System.Reflection.Assembly.GetAssembly()` 完整限定调用。（`DebugSkills.cs`）
- **`shader_delete` 无法通过 Workflow 撤销** — 删除前新增 `WorkflowManager.SnapshotObject()` 调用，使 `workflow_undo_task` 能正确追踪并恢复被删除的 Shader 文件，与 `script_delete` 行为对齐。（`ShaderSkills.cs`）
- **`WorkflowManager.LoadHistory` 崩溃时数据丢失** — 主文件不存在但 `.tmp` 文件存在时（进程崩溃典型场景），自动将 `.tmp` 提升为主文件再读取，防止历史数据因原子写入未完成而丢失。（`WorkflowManager.cs`）
- **`DebugSkills.ReadLogEntries` 空消息 NullReferenceException** — `LogEntry.message` / `file` 字段反射取值改为 `?? ""`，避免 Console 中存在空消息条目时抛出 NRE。`DebugGetStackTrace` 同步修复。（`DebugSkills.cs`）
- **`console_export` 必须先 `console_start_capture`** — 当 capture 缓冲区为空且未在捕获模式时，改为直接从 Unity Console 历史（`LogEntries` 反射）读取并导出，不再返回空文件。（`ConsoleSkills.cs`）
- **`console_get_stats` 始终返回全零** — 同上，非 capture 模式下改为从 Unity Console 实时统计各类型日志数量，结果附带 `source` 字段区分来源（`"capture"` / `"console"`）。（`ConsoleSkills.cs`）

### Improved（Skill 描述优化 — AI 自动触发质量提升）

- **日志读取 Skill 交叉引用** — `debug_get_errors` 和 `debug_get_logs` 描述中注明与 `console_get_logs` 的关系，避免 AI 在全自动模式下选错工具
- **`scene_find_objects` vs `gameobject_find`** — 描述中注明简单查询 vs 高级查询（regex/layer/path）的区别
- **`hierarchy_describe` vs `scene_get_hierarchy`** — 描述中注明文本树 vs JSON 结构的区别
- **`prefab_apply` / `prefab_apply_overrides`** — 互相注明两者等价，消除 AI 的选择困惑
- **`prefab_unpack`** — 明确说明 `completely` 参数含义（仅解包最外层 vs 完全递归解包）
- **`editor_undo` / `editor_redo`** — 注明单步限制，引导多步操作使用 `history_undo(steps=N)` / `history_redo(steps=N)`
- **`editor_play` / `editor_stop`** — 新增 Play 模式数据丢失警告，防止 AI 在 Play 模式下修改场景后直接 Stop 导致数据丢失
- **`workflow_task_end` / `workflow_snapshot_object` / `workflow_snapshot_created`** — 明确前置条件（需先调用 `workflow_task_start`），防止 AI 跳过必要初始化步骤
- **`smart_scene_layout` / `smart_align_to_ground` / `smart_distribute` / `smart_replace_objects`** — 注明需要先在 Hierarchy 中选中对象，防止 AI 直接调用返回"无选中对象"错误
- **`test_run`** — 明确异步行为（立即返回 jobId），引导 AI 随后调用 `test_get_result(jobId)` 轮询结果
- **`test_get_result`** — 注明需要 `test_run` 返回的 `jobId`，防止 AI 无参数调用
- **`scene_context`** — 补充适用场景说明（编码或复杂场景作业前的初始上下文收集）
- **`scene_screenshot`** — 描述从"scene view"更正为"game view"，与实际行为一致

### Notes

- `console_get_logs` 返回空列表属于**设计行为**，不是 bug：该 skill 基于 `Application.logMessageReceived` 事件回调，只捕获订阅后产生的新日志。**使用前必须先调用 `console_start_capture`**，之后触发的日志才会被记录。如需读取 Console 中已有的历史日志，请使用 `debug_get_errors`、`debug_get_logs` 或 `console_get_stats`（均可直接读取 Unity Editor LogEntries，无需预先启动）。

## [1.5.2] - 2026-02-25

### Fixed
- **JetBrains.Annotations 反射崩溃** — 修复在含有 JetBrains Rider 注解（`[NotNull]`/`[CanBeNull]` 等）的项目中，插件扫描技能方法时 CLR 尝试加载 `JetBrains.Annotations.dll`（Version=4242.42.42.42）失败导致的 `FileNotFoundException`。现在遇到程序集加载异常时会跳过该方法继续扫描，不影响正常功能。（`SkillRouter.cs`、`UnitySkillsWindow.cs`）
- **反射 GetCustomAttribute 崩溃风险** — 修复与 JetBrains 崩溃同类的三处反射调用：`AllowMultiple()`（`ComponentSkills.cs:551`）、`GetRequiredByComponents()` 内的 LINQ 查询（`ComponentSkills.cs:556`）、`GetCustomAttribute<ObsoleteAttribute>()`（`CinemachineSkills.cs:166`）。CLR 解析特性时若触发程序集加载失败，现在均以 try-catch 安全降级，不影响正常功能。
- **路径遍历安全漏洞** — 两处文件操作路径参数缺少校验：`scriptableobject_import_json` 的 `jsonFilePath` 参数新增 `Validate.SafePath()` 校验，阻止 `../../etc/passwd` 等路径逃逸（`ScriptableObjectSkills.cs:209`）；`script_create` 的 `scriptName` 参数新增路径分隔符检查（`/`、`\`、`..`），防止经由 `Path.Combine` 逃逸出 Assets 目录（`ScriptSkills.cs:24`）。

### Security
- **`scriptableobject_import_json` jsonFilePath 路径遍历** — `jsonFilePath` 参数现在通过 `Validate.SafePath()` 限制在 Assets/Packages 目录内（`ScriptableObjectSkills.cs`）
- **`script_create` scriptName 目录逃逸** — `scriptName` 含路径分隔符时立即返回错误，不再经由 `Path.Combine` 拼接到文件系统（`ScriptSkills.cs`）

## [1.5.1] - 2026-02-15

### ⭐ Highlight

- **全模块 10+ Skill 覆盖** — 13 个模块从不足 10 个 Skill 扩展到 10+，新增 57 个 Skill，总计约 430 个。所有模块（SampleSkills 除外）均达到 10+ Skill 覆盖。

### Added

- **服务器启动自检 (Self-Test)** — 启动后自动请求 `localhost` 和 `127.0.0.1` 的 `/health` 端点，验证可达性并在 Console 输出结果，帮助用户快速定位连接问题
- **端口占用扫描** — 自检时扫描 8090-8100 范围内其他被占用的端口，以警告形式提示用户

#### 新增 Skill（57 个）

- **ProfilerSkills** (+9): `profiler_get_memory`, `profiler_get_runtime_memory`, `profiler_get_texture_memory`, `profiler_get_mesh_memory`, `profiler_get_material_memory`, `profiler_get_audio_memory`, `profiler_get_object_count`, `profiler_get_rendering_stats`, `profiler_get_asset_bundle_stats`
- **OptimizationSkills** (+8): `optimize_analyze_scene`, `optimize_find_large_assets`, `optimize_set_static_flags`, `optimize_get_static_flags`, `optimize_audio_compression`, `optimize_find_duplicate_materials`, `optimize_analyze_overdraw`, `optimize_set_lod_group`
- **AudioSkills** (+7): `audio_find_clips`, `audio_get_clip_info`, `audio_add_source`, `audio_get_source_info`, `audio_set_source_properties`, `audio_find_sources_in_scene`, `audio_create_mixer`
- **ModelSkills** (+7): `model_find_assets`, `model_get_mesh_info`, `model_get_materials_info`, `model_get_animations_info`, `model_set_animation_clips`, `model_get_rig_info`, `model_set_rig`
- **TextureSkills** (+7): `texture_find_assets`, `texture_get_info`, `texture_set_type`, `texture_set_platform_settings`, `texture_get_platform_settings`, `texture_set_sprite_settings`, `texture_find_by_size`
- **LightSkills** (+3): `light_add_probe_group`, `light_add_reflection_probe`, `light_get_lightmap_settings`
- **PackageSkills** (+3): `package_search`, `package_get_dependencies`, `package_get_versions`
- **ValidationSkills** (+3): `validate_missing_references`, `validate_mesh_collider_convex`, `validate_shader_errors`
- **ShaderSkills** (+5): `shader_check_errors`, `shader_get_keywords`, `shader_get_variant_count`, `shader_create_urp`, `shader_set_global_keyword`
- **AnimatorSkills** (+2): `animator_add_state`, `animator_add_transition`
- **ComponentSkills** (+2): `component_copy`, `component_set_enabled`
- **PerceptionSkills** (+2): `scene_tag_layer_stats`, `scene_performance_hints`
- **PrefabSkills** (+2): `prefab_create_variant`, `prefab_find_instances`
- **SceneSkills** (+1): `scene_find_objects`

### Improved
- **`profiler_get_runtime_memory`** — 从单对象查询改为按内存占用排序的 Top N 列表，对 AI 更实用
- **`scene_tag_layer_stats`** — 新增未标记对象计数和空定义层检测
- **`scene_performance_hints`** — 增强为结构化输出（priority/category/issue/suggestion/fixSkill），新增 LOD、重复材质、粒子系统检查

### Fixed
- **IPv4 可达性修复** — `HttpListener` 同时绑定 `localhost` 和 `127.0.0.1`，修复部分 Windows 系统上 `localhost` 仅解析到 IPv6 `::1` 导致 `127.0.0.1` 无法连接的问题
- **截图文件缺少扩展名** — `SceneScreenshot` 当 `filename` 参数不含扩展名时自动补 `.png` 后缀，修复生成的截图文件无法在 Unity 中预览的问题 (`SceneSkills.cs:111`)
- **本地化补全** — 为 `Localization.cs` 的 `_chinese` 字典补充约 140 条缺失的中文翻译，英文/中文 471 个 key 完全匹配
- **SkillRouter 更新** — `_workflowTrackedSkills` 新增 17 个写操作 Skill 的追踪
- **超长任务断连修复** — 修复超过 3 分钟的任务因三层超时叠加（Python 30s / C# 60s / Skill 执行 3min+）导致必然断连的问题：
  - 请求超时改为用户可配置（默认 60 分钟），Unity 设置面板新增"请求超时"输入框
  - `/health` 端点暴露 `requestTimeoutMinutes`，Python 客户端初始化时自动同步超时配置
  - 生成的 AI 代理代码同步使用服务器超时配置，替代硬编码 30 秒
- **Domain Reload 断连修复** — 修复 Unity 6 上脚本编译后服务器恢复失败的问题：
  - `OnBeforeAssemblyReload` 主动关闭 HttpListener 并等待线程退出，确保端口立即释放
  - 持久化运行端口（`PREF_LAST_PORT`），Reload 后优先恢复到同一端口，避免 Auto 模式端口漂移
  - `CheckAndRestoreServer` 增加秒级延迟重试（1s/2s/4s），替代无效的 `delayCall`（~16ms）
  - preferred port 被占用时自动降级到端口扫描，而非直接失败
  - Python 客户端重试增强：3 次重试 + 渐进式退避（2s/4s/6s），总窗口 ~12 秒
  - 注册表过期阈值从 60 秒提升到 120 秒，避免大项目 Reload 期间实例被误清理
- **Self-Test /health 返回 500 修复** — `WaitAndRespond()` 在 ThreadPool 线程上访问 `RequestTimeoutMs` 时触发 `EditorPrefs.GetInt()`（主线程限定 API），抛出 `UnityException` 被 catch 捕获返回 500。改为 `Start()` 时缓存超时值到静态字段，避免非主线程调用 Unity API
- **清理 AudioSkills.cs.bak** — 移除误提交的备份文件，消除 Unity immutable package 中缺少 .meta 文件的警告
- **`script_create` 参数名兼容** — 同时支持 `scriptName` 和 `name` 参数，当两者都为空时返回明确错误而非生成 `.cs` 空文件名。`script_create_batch` 同步支持
- **`light_add_probe_group` 增强** — 新增 `gridX/gridY/gridZ`（每轴探针数）和 `spacingX/spacingY/spacingZ`（间距）参数，支持一步创建网格布局的光照探针组；已有组件时支持重新设置探针位置

#### Unity 6 兼容性修复（6 项）
- **`console_set_collapse` / `console_set_clear_on_play` 修复** — Unity 6 移除了 `ConsoleWindow.s_ConsoleFlags` 静态字段，改为多级回退策略：`SetConsoleFlag` 方法 → `s_ConsoleFlags` 字段 → `LogEntries` API → `EditorPrefs` 兜底（`ConsoleSkills.cs`）
- **`cinemachine_set_active` IComparable 修复** — CM3 的 `Priority` 属性不支持 LINQ `Max()` 泛型比较，改用 `foreach` 手动迭代并显式 `(int)` 转换（`CinemachineSkills.cs:538`）
- **`audio_create_mixer` 创建失败修复** — Unity 6 中 `ScriptableObject.CreateInstance(AudioMixerController)` 触发 `ExtensionOfNativeClass` 异常导致返回失败，重构为优先使用 `CreateMixerControllerAtPath` 内部工厂方法 + `ScriptableObject.CreateInstance` 回退。注："Mixer is not initialized" 日志为 Unity 6 内部已知问题，Unity 自身菜单创建 AudioMixer 也会产生，不影响功能（`AudioSkills.cs:280`）
- **`event_add_listener` 目标组件查找修复** — `GetComponent("GameObject")` 返回 null（GameObject 不是 Component），新增特殊处理：当 `targetComponentName` 为 `"GameObject"` 时直接使用 GO 作为目标 Object；同时增加 `set_XXX` 属性 setter 方法查找支持（`EventSkills.cs:90`）
- **`smart_reference_bind` 字段查找修复** — 增加 Unity 序列化命名约定回退查找（`m_XXX`、`_xxx`）和 `PropertyInfo` 回退，修复 Unity 6 中部分组件字段名不匹配的问题（`SmartSkills.cs:159`）
- **Splines 版本适配** — 新增 `SplinesVersionUnity6 = "2.8.3"` 常量和 `GetRecommendedSplinesVersion()` 方法，Unity 6 自动使用 2.8.3、Unity 2022 使用 2.8.0；CM3 安装依赖同步更新（`PackageManagerHelper.cs`）
- **`component_set_enabled` Renderer/Collider 支持** — 原代码仅检查 `Behaviour` 类型，导致 `MeshRenderer`（继承 `Renderer`）和 `Collider` 等组件无法启用/禁用，新增 `Renderer` 和 `Collider` 类型分支（`ComponentSkills.cs:911`）
- **`optimize_find_duplicate_materials` _Color 属性异常修复** — `mat.color` 直接访问 `_Color` 属性，TextMeshPro 等 shader 无此属性时抛出异常，改为 `HasProperty` 检查并回退到 `_BaseColor`（`OptimizationSkills.cs:237`）

### Added
- **`package_install_splines` 技能** — 新增 Splines 包版本化安装技能，自动检测 Unity 版本选择正确的 Splines 版本（Unity 6: 2.8.3, Unity 2022: 2.8.0），支持升级已安装的旧版本（`PackageSkills.cs`）

## [1.5.0] - 2026-02-13

### ⭐ Highlight

- **`scene_export_report`** — 一键导出完整场景报告（Markdown），包含：精简层级树（内置组件仅列名称，用户脚本标 `*`）、用户脚本字段清单（含实际值和引用目标路径）、**深度 C# 代码级依赖分析**（10 种模式：`GetComponent<T>`/`FindObjectOfType<T>`/`SendMessage`/字段类型引用/单例访问/静态成员调用/`new T()`实例化/泛型类型参数/继承与接口实现/`typeof`·`is`·`as`类型检查）、合并依赖图与风险评级。覆盖项目中所有用户 C# 类（MonoBehaviour、ScriptableObject、Editor、普通类）。生成的文件可直接作为 AI 持久化上下文。调用示例：`call_skill('scene_export_report', savePath='Assets/Docs/SceneReport.md')`

### Improved
- **`scene_export_report` 依赖分析质量提升** (5 项修复):
  1. Dependency Graph 表格新增 `Source` 列，区分 `scene`（序列化引用）和 `code`（源码分析），AI 不再混淆场景对象与类名
  2. 代码扫描前剔除 `//` 单行注释和 `/* */` 块注释，消除注释中的虚假依赖
  3. `StaticAccess` 正则收紧为双侧 PascalCase（`[A-Z]\w+\.\s*[A-Z]\w*`），不再误报 `Debug.Log`、`Mathf.Clamp` 等
  4. `RxInheritance` 从 `Match` 改为 `Matches`，支持单文件多类（partial class、嵌套类）
  5. 新增方法级粒度：`From` 列显示 `ClassName.MethodName`，定位依赖发生的具体方法

### Fixed (全项目审计 — 36 项缺陷修复)

#### 🔴 严重 (14 项)
- **P-1** `CinemachineSkills.cs` — `componentType` 为 null 时 `.Equals()` 空引用崩溃，添加 null 检查
- **P-2** `SmartSkills.cs` — 非 Component 对象强转 `(comp as Component).gameObject` 崩溃，改为安全转换并跳过
- **B-1** `ScriptSkills.cs:147` — 用户输入正则无超时限制导致 ReDoS 风险，添加 `TimeSpan.FromSeconds(1)` 超时
- **B-2** `GameObjectSkills.cs:265` — 同上 ReDoS 风险，`new Regex(name)` 添加超时参数
- **B-3** `PrefabSkills.cs:40-41,80` — `InstantiatePrefab` 返回 null 未检查导致后续空引用，添加 null 守卫
- **B-4** `SceneSkills.cs:99` — `GetComponents<Component>()` 返回含 null 元素（缺失脚本），`.Select(c => c.GetType())` 崩溃，添加 `.Where(c => c != null)` 过滤
- **B-9** `LightSkills.cs:27-30` — 无效 lightType 时返回错误但已创建的 GameObject 泄漏，添加 `DestroyImmediate(go)` 清理
- **B-10** `ComponentSkills.cs:574` — `ConvertValue` 对值类型返回 null 导致拆箱异常，改为 `Activator.CreateInstance(targetType)` 返回默认值
- **B-11** `TerrainSkills.cs:238` — `radiusPixels=0` 时除零异常，添加 `Mathf.Max(1, ...)` 下限
- **I-1** `SkillsHttpServer.cs` — `Stop()` 未 Join 后台线程导致线程泄漏，添加 `Thread.Join(2000)` 和引用清理
- **I-5** `SkillsHttpServer.cs` — skill name 未校验可注入 `/` `..` 等路径字符，添加输入验证
- **I-6** `SkillRouter.cs` — `BeginTask` 注册的 Undo hooks 在异常时未通过 `EndTask` 清理，在 catch 块中添加 `EndTask()` 调用
- **P-4** `unity_skills.py:118-127` — 端口扫描全部失败时静默回退到 8090，改为抛出 `ConnectionError` 明确报错
- **P-7** `unity_skills.py:421-425` — `WorkflowContext.__enter__` 中 `call_skill` 失败后 `_current_workflow_active` 仍为 True，重排赋值顺序并添加异常处理

#### 🟡 中等 (15 项)
- **P-3** `SmartSkills.cs:213-222` — Transform 分支是 Component 分支的子集（死代码），删除冗余分支
- **P-5** `Localization.cs:40` — `Get()` 直接读 `_current` 字段绕过 `Current` 属性的懒初始化，改为使用 `Current` 属性
- **B-5** `SceneSkills.cs:110` — `SceneScreenshot` 忽略 width/height 参数，改用 `superSize` 计算并在返回值中包含尺寸
- **B-6** `AnimatorSkills.cs:67-83` — `controller.parameters` 返回数组副本，修改后未写回，添加 `controller.parameters = parameters` 回写
- **B-7** `ComponentSkills.cs:738` — `easein` 和 `easeout` 使用相同的 `EaseInOut` 曲线，改为各自独立的加速/减速曲线
- **B-8** `MaterialSkills.cs:763` — Float 类型属性调用 `GetPropertyRangeLimits()` 返回无意义值，分离 Float 和 Range 两个 case
- **B-12** `UISkills.cs:249` — `item.type` 为 null 时 `.ToLower()` 崩溃，添加 null 合并 `(item.type ?? "")`
- **B-13** `ScriptSkills.cs:70-72` — 未提供 namespace 时 `{NAMESPACE}` 占位符残留在生成的脚本中，添加默认值替换
- **I-3** `WorkflowManager.cs` — `SaveHistory()` 直接写目标文件，崩溃时数据丢失，改为先写 `.tmp` 再原子替换
- **I-7** `SkillsHttpServer.cs` — 速率限制使用 `double` 精度时间戳存在浮点漂移，改为 `long` Ticks 整数比较
- **I-8** `WorkflowManager.cs` — 批量操作无快照上限导致内存无限增长，添加 500 条上限和日志提示
- **I-9** `RegistryService.cs` — 清理过期条目仅检查时间戳，进程已死但时间未过期的条目残留，添加 `IsProcessAlive()` 检查
- **I-10** `GameObjectFinder.cs` — 编辑器非播放模式下 `Time.frameCount` 不递增导致缓存永不失效，改为请求级 bool 标志
- **P-8** `AudioSkills.cs:145-177` — `StartAssetEditing()` 期间调用 `SaveAndReimport()` 导致导入管线冲突，移除 batch 方法的 setup/teardown
- **P-11** `unity_skills.py:520` — CLI 数值解析 `isdigit()` 预检对 `"1.2.3"` `"--5"` 等边界值误判，改为直接 try/except 转换

#### 🟢 轻微 (7 项)
- **P-9** `ValidationSkills.cs:192-211` — 空文件夹删除未按深度排序，父文件夹先删导致子文件夹残留，改为按路径长度降序删除
- **P-10** `WorkflowSkills.cs:121-138` — `HistoryUndo/Redo` 未校验 steps 参数，负数导致无限循环，添加 `steps < 1` 守卫
- **P-12** `PhysicsSkills.cs:78-89` — `PhysicsSetGravity` Undo 记录使用 `RecordObject` 而非 `Undo.RecordObject`，变量命名优化避免混淆
- **B-14** `ComponentSkills.cs:167` — `SnapshotObject` 内部已有 `_currentTask == null` 守卫，确认无需额外修改
- **I-2** `SkillsHttpServer.cs` — `ManualResetEventSlim` 已通过 ownership transfer 模式正确管理，确认无泄漏
- **I-4** `RegistryService.cs` — tmp 文件删除已在文件锁保护范围内，确认无竞态条件
- **P-6** `unity_skills.py:457-462` — `get_skills()`/`health()` 使用 `requests.get` 而非 Session 对象，属设计选择非缺陷

### Added
- **依赖边扫描重构**: 提取 `CollectDependencyEdges()` 共享方法，供 `scene_export_report` 和 `scene_dependency_analyze` 复用，消除重复代码
- **场景快照 Skill**: 新增 `scene_context`，一次调用生成结构化 JSON 场景快照（层级、组件、脚本字段值、跨对象引用、UI 布局），支持 `rootPath` 子树导出、`maxObjects`/`maxDepth` 截断策略，让 AI 无需追问即可理解场景并编写代码（`PerceptionSkills.cs`）
- **依赖分析 Skill**: 新增 `scene_dependency_analyze`，分析场景对象间的引用依赖关系，生成反向依赖索引和风险评级（safe/low/medium/high），支持导出 Markdown 报告作为 AI 持久化上下文，防止 AI 操作误伤关键依赖对象（`PerceptionSkills.cs`）
- **BatchExecutor 泛型框架**: 新增 `BatchExecutor.Execute<T>()` 通用批处理框架，支持 JSON 反序列化、逐项执行、错误隔离、setup/teardown 钩子（`BatchExecutor.cs`）
- **SkillsLogger 统一日志**: 新增 `SkillsLogger` 类，支持 Off/Error/Warning/Info/Agent/Verbose 日志级别，替代散落的 `Debug.Log` 调用（`SkillsLogger.cs`）
- **参数校验扩展**: `Validate` 类新增 `InRange()`、`RequiredJsonArray()`、`SafePath()` 方法，形成完整的参数校验工具链（`GameObjectFinder.cs`）
- **单元测试框架**: 新增 `Tests/Editor/` 目录，包含 3 个测试套件共 67 个测试用例：
  - `BatchExecutorTests.cs` — 17 个测试覆盖批处理成功/失败/setup/teardown 生命周期
  - `RegistryServiceTests.cs` — 16 个测试覆盖哈希确定性和边界条件
  - `ValidateTests.cs` — 34 个测试覆盖 Required/InRange/SafePath 校验
- **场景空间查询 Skill**: 新增 `scene_spatial_query`，支持按坐标/对象名查找半径内的对象，可按组件类型过滤（`PerceptionSkills.cs`）
- **场景材质概览 Skill**: 新增 `scene_materials`，按 Shader 分组展示场景中所有材质的使用情况，可选输出 Shader 属性列表（`PerceptionSkills.cs`）

### Security
- **SHA256 哈希**: RegistryService 实例 ID 从 MD5 迁移到 SHA256（`RegistryService.cs`）
- **TOCTOU 文件锁**: 注册表文件读写添加文件锁防止竞态条件（`RegistryService.cs`）
- **POST Body 大小限制**: HTTP 服务器拒绝超过 10MB 的请求体，返回 413 状态码（`SkillsHttpServer.cs`）
- **ManualResetEventSlim 泄漏修复**: try/finally 模式确保信号量在 ThreadPool 入队失败时仍被释放，包括超大请求拒绝路径（`SkillsHttpServer.cs`）
- **路径遍历防护**: 19 个文件操作方法补齐 `Validate.SafePath()` 校验，涵盖 Script/Shader/Material/ScriptableObject/Prefab/Scene/Asset/Cleaner/Validation/Animator 共 11 个 Skill 文件

### Changed

#### 架构重构
- **BatchExecutor 接入**: 25 个 batch 方法迁移到 `BatchExecutor.Execute<T>()` 框架，消除约 1500 行重复的反序列化/错误收集/结果汇总代码，涉及 GameObjectSkills/ComponentSkills/MaterialSkills/LightSkills/PrefabSkills/UISkills/AudioSkills/ModelSkills/TextureSkills/AssetSkills/ScriptSkills 共 11 个文件
- **WorkflowManager Undo/Redo 提取**: 重构撤销/重做逻辑为独立方法，提升可维护性（`WorkflowManager.cs`）
- **Agent 表驱动注册**: SkillRouter 的 Agent 配置改为表驱动模式，新增 Agent 类型无需修改分发逻辑（`SkillRouter.cs`）
- **SkillRouter 消除双重序列化**: 替换 `JObject.FromObject(result)` 为反射检测错误字段，避免不必要的 JSON 中间转换（`SkillRouter.cs`）

#### 代码质量
- **GameObjectFinder 全面迁移**: 50+ 处原始 `GameObject.Find` 调用迁移到 `GameObjectFinder.FindOrError`，提供错误提示含相似名称建议，涉及 PrefabSkills/EventSkills/TimelineSkills/CameraSkills/EditorSkills/UISkills/WorkflowSkills/ComponentSkills/SampleSkills/CinemachineSkills 共 10 个文件
- **CinemachineSkills 全面升级**: 所有 Skill 方法支持 name/instanceId/path 三种查找方式，与其他 Skills 保持一致（`CinemachineSkills.cs`）
- **统一返回值格式**: 10 个方法补齐 `success = true/false` 字段（`SampleSkills.cs`、`OptimizationSkills.cs`、`ValidationSkills.cs`）
- **区域无关数值解析**: ComponentSkills 和 ScriptableObjectSkills 中 7 处 `float.Parse`/`double.Parse` 添加 `CultureInfo.InvariantCulture`，修复非英文区域的小数点解析问题
- **静默异常修复**: 多处空 catch 块添加日志记录，便于调试定位问题
- **文件重命名**: `NextGenSkills.cs` → `PerceptionSkills.cs`，文件名与类名保持一致
- **SampleSkills 标注**: 明确标记为便捷别名，4 处 `GameObject.Find` 迁移到 `GameObjectFinder.FindOrError`
- **PerceptionSkills 全面改进**: `script_analyze` 扩展支持 ScriptableObject 和用户自定义类，返回新增 `kind` 字段；`hierarchy_describe` 组件 emoji 提示从 5 种扩展到 13 种（新增 Animator/AudioSource/ParticleSystem/Collider/Rigidbody/SkinnedMeshRenderer/SpriteRenderer/UI）；`IsUnityCallback` HashSet 提升为 `static readonly` 并扩充回调列表（`PerceptionSkills.cs`）

#### 基础设施
- **PhysicsSetGravity Undo 支持**: 通过 `DynamicsManager.asset` 注册 Undo，重力修改可撤销（`PhysicsSkills.cs`）
- **双重检查锁**: 单例和懒初始化改用双重检查锁模式（`SkillsHttpServer.cs`）
- **超时常量化**: 散落的超时魔数提取为命名常量（`SkillsHttpServer.cs`）
- **版本集中化**: 版本号集中管理，避免多处硬编码不一致
- **Python 客户端异常安全**: `unity_skills.py` workflow 相关代码使用 try/finally 确保 `_current_workflow_active` 状态正确重置

### Performance
- **GameObjectFinder 帧级缓存**: 同一帧内重复查找同名 GameObject 直接命中缓存，避免冗余遍历（`GameObjectFinder.cs`）
- **反射成员缓存**: ComponentSkills 新增 `_memberCache` 字典和 `FindMember()` 辅助方法，属性/字段查找结果被缓存，批量操作性能显著提升（`ComponentSkills.cs`）
- **scene_summarize 单次遍历**: 消除 3 次额外 `FindObjectsOfType`（Light/Camera/Canvas），改为在组件遍历中内联统计，大场景性能提升显著（`PerceptionSkills.cs`）

### Docs
- README.md 技能数量修正
- agent.md 添加 Git 分支同步规则和 agent_config.json 手动安装说明

---

## [1.4.4] - 2026-02-11

### Added
- 统一错误响应格式：自动检测并转换 Skill 返回的错误对象
- 参数验证工具类：`Validate.Required()` 和 `Validate.SafePath()`
- 请求追踪 ID：每个请求分配唯一 X-Request-Id
- Agent 标识：支持 X-Agent-Id header 识别调用的 AI 工具
- 日志级别控制：支持 Off/Error/Warning/Info/Agent/Verbose
- SkillsLogger 类：统一日志管理
- 服务端自动工作流记录：修改类 Skill 自动记录历史

### Changed
- Python 客户端：使用 UTF-8 编码发送 JSON，内置重试逻辑
- Skill Manifest：添加缓存机制减少开销
- GameObjectFinder：使用场景根遍历优化性能

### Security
- 文件路径安全校验：防止路径遍历攻击，限制在 Assets/Packages 目录

---

## [1.4.3] - 2026-02-09

### 📝 文档规范化
- **Skill 文档全面优化**: 所有 36 个模块的 SKILL.md 文件现已符合统一规范
  - 添加完整的 YAML frontmatter（name + description）
  - description 格式统一为：`"{功能描述}. Use when {使用场景}. Triggers: {关键词}."`
  - 拆分合并的 `### skill_a / skill_b` 条目为独立条目
- **Skill 数量修正**: README.md 中的数字从 279 修正为实际的 277
- **清理测试文件**: 删除验证过程中产生的临时脚本文件

---

## [1.4.2] - 2026-02-09

### 🆕 Package Manager Skills
- **新增 `PackageManagerHelper.cs`**: 封装 Unity Package Manager API，支持包的安装、移除、刷新等操作。
- **新增 `PackageSkills.cs`**: AI 可调用的包管理技能：
  - `package_list` - 列出已安装包
  - `package_check` - 检查包是否已安装
  - `package_install` - 安装指定包
  - `package_remove` - 移除包
  - `package_refresh` - 刷新包列表缓存
  - `package_install_cinemachine` - 安装 Cinemachine（支持版本 2 或 3）
  - `package_get_cinemachine_status` - 获取 Cinemachine 安装状态

### 🎬 Cinemachine 自动安装
- **全自动安装**: 移除手动安装 UI，改为编辑器启动时自动安装
  - Unity 6+: 自动安装 CM 3.1.3 + Splines 2.8.0
  - Unity 2022 及以下: 自动安装 CM 2.10.5
- **重试机制**: Package Manager 繁忙时自动重试（最多 5 次，间隔 3 秒）

### 🔧 CM2/CM3 兼容性
- **条件编译**: 通过 `CINEMACHINE_2` / `CINEMACHINE_3` 宏区分版本
- **API 适配**: 修复 `CinemachineBrain.UpdateMethod` vs `m_UpdateMethod` 等 API 差异
- **双版本测试**: 在 Unity 2022 (CM2) 和 Unity 6 (CM3) 上验证所有 Cinemachine Skills

### 📝 Workflow 支持完善
- **SmartSkills**: `smart_scene_layout`, `smart_reference_bind` 添加 Workflow 支持
- **EventSkills**: `event_add_listener`, `event_remove_listener` 添加 Workflow 支持
- **ValidationSkills**: `validate_fix_missing_scripts` 添加 Workflow 支持
- 所有使用 Undo 的模块现已完整支持 Workflow 撤销/重做

---

## [1.4.1] - 2026-02-05

*> This PR upgrades the project to support Cinemachine 3.x (Unity.Cinemachine namespace), which is standard in Unity 6.*
*> Credit: [PieAIStudio](https://github.com/PieAIStudio)*

### 🚀 Cinemachine 3.x Upgrade
- **Namespace Migration**: Refactored `CinemachineSkills.cs` to use the new `Unity.Cinemachine` namespace and API (replacing `CinemachineCamera`, etc.).
- **Dependency Update**:
    - Updated `com.unity.cinemachine` to **3.1.3**.
    - Added `com.unity.splines` **2.8.0** as a hard dependency (required for CM 3.x).
    - Updated `UnitySkills.Editor.asmdef` to reference `Unity.Cinemachine` and `Unity.Splines`.
- **Advanced Features**:
    - Full support for **Manager Cameras**: `MixingCamera`, `ClearShot`, `StateDrivenCamera`.
    - Support for **Spline Dolly** (`cinemachine_set_spline`) and **Target Group** (`cinemachine_create_target_group`).
    - Fixed infinite recursion issues in JSON serialization for deep inspection.

---

## [1.4.0] - 2026-02-04

### 🌟 New Features / 新特性 (Major Update since v1.3.0)

- **Persistent Workflow History / 持久化工作流历史**:
    - Introduced "Time Machine" persistent operation history. / 引入了持久化的 AI 操作历史记录。
    - Support for tagging tasks (`workflow_task_start`), snapshots (`workflow_snapshot_object`), and full rollback (`workflow_revert_task`). / 支持任务标签、对象快照及可视化回滚。
    - History persists across Editor restarts and Domain Reloads. / 历史记录在编辑器重启和重载后仍然保留。
    - Added **History Tab** in UnitySkills Window. / 在插件窗口新增“历史”标签页。

- **High-Level Scene Perception / 高级场景感知**:
    - `scene_summarize`, `hierarchy_describe`, `script_analyze`: Deeply perceive scene structure and API. / 深度感知场景结构与 API。

- **Consolidated Skill Modules / 模块功能补完**:
    - **Cinemachine / Timeline / NavMesh / Physics / Event / Profiler**: Full documentation and exposure of these critical modules. / 补全并正式开放这些核心模块的功能与文档。

- **Operations & System**:
    - Customizable Skill Installation path. / 支持自定义安装路径。
    - Terrain editing and Asset redundancy detection (Cleaner). / 新增地形编辑与资源清理。

### 🐞 Bug Fixes / 问题修复
- **Unicode & Encoding**: Fully fixed Chinese character support in both Python client and Unity server. / 彻底修复中文字符支持及乱码问题。
- **Dependencies**: Added `com.unity.splines` (2.8.0) as a hard dependency to support advanced Cinemachine features. / 新增 Splines (2.8.0) 为硬依赖以支持 Cinemachine 高级功能。

---

## [1.3.0] - 2026-01-27

### 🌟 New Features / 新特性
- **Multi-Instance Support**: Auto-port discovery (8090-8100) and Global Registry.
- **Transactional Safety**: Atomic Undo/Redo for skill operations.
- **Batching**: Broad implementation of `*_batch` variants for improved performance.
- **Documentation**: Standardized SKILL.md format and token optimization.

### 📝 Documentation Improvements / 文档优化

- **SKILL.md Token Optimization / SKILL.md Token 优化**:
    - Restructured main SKILL.md for AI consumption with batch-first approach. / 重构主 SKILL.md，采用批量优先方式便于 AI 使用。
    - Unified table format across all skill modules. / 统一所有技能模块的表格格式。
    - Added complete parameter lists and enum values. / 添加完整的参数列表和枚举值。
    - Removed redundant content and duplicate entries. / 移除冗余内容和重复条目。
    - All sub-module SKILL.md files optimized with batch-first rule. / 所有子模块 SKILL.md 文件按批量优先规则优化。

---

## [1.2.0] - 2026-01-24

### 🌟 New Features / 新特性

- **Editor Context Skill (`editor_get_context`) / 编辑器上下文获取**:
    - Get currently selected GameObjects from Hierarchy with instanceId, path, components. / 获取 Hierarchy 选中物体。
    - Get currently selected assets from Project window with GUID, path, type. / 获取 Project 窗口选中资源。
    - Get active scene info, focused window, editor state in one call. / 一次调用获取完整编辑器状态。
    - **AI can now operate directly on selection without searching!** / AI 可直接操作选中对象无需搜索！

- **Texture Import Settings (3 skills) / 纹理导入设置**:
    - `texture_get_settings`: Get current texture import settings. / 获取纹理导入设置。
    - `texture_set_settings`: Set texture type, size, filter mode, compression, etc. / 设置纹理类型、尺寸、过滤模式等。
    - `texture_set_settings_batch`: Batch process multiple textures. / 批量处理多张纹理。

- **Audio Import Settings (3 skills) / 音频导入设置**:
    - `audio_get_settings`: Get current audio import settings. / 获取音频导入设置。
    - `audio_set_settings`: Set load type, compression format, quality, etc. / 设置加载类型、压缩格式、质量等。
    - `audio_set_settings_batch`: Batch process multiple audio files. / 批量处理多个音频。

- **Model Import Settings (3 skills) / 模型导入设置**:
    - `model_get_settings`: Get current model import settings. / 获取模型导入设置。
    - `model_set_settings`: Set mesh compression, animation type, materials, etc. / 设置网格压缩、动画类型、材质等。
    - `model_set_settings_batch`: Batch process multiple 3D models. / 批量处理多个模型。

### 📦 New Skill Modules / 新增模块

| Module | Skills | Files |
|--------|--------|-------|
| **Editor** | +1 | `EditorSkills.cs` |
| **Texture** | 3 | `TextureSkills.cs` (NEW) |
| **Audio** | 3 | `AudioSkills.cs` (NEW) |
| **Model** | 3 | `ModelSkills.cs` (NEW) |
| **GameObject** | +3 | `gameobject_duplicate_batch`, `gameobject_rename`, `gameobject_rename_batch` |
| **Light** | +2 | `light_set_enabled_batch`, `light_set_properties_batch` |

### 📝 Documentation Improvements / 文档优化

- All SKILL.md now include **Returns** structure for each skill / 所有技能文档现在包含返回结构说明
- Added ⚠️ batch operation warnings to prevent N-calls loops / 添加批量操作警告避免循环调用
- Added `instanceId` support documentation / 添加 instanceId 支持说明
- Fixed duplicate content in prefab SKILL.md / 修复 prefab 文档重复内容

---

## [1.1.0] - 2026-01-23


### 🚀 Major Update: Production Readiness / 生产级就绪
This release transforms UnitySkills from a basic toolset into a production-grade orchestration platform.
本次更新将 UnitySkills 从基础工具集升级为生产级编排平台。

### 🌟 New Features / 新特性
- **Multi-Instance Support (多实例支持)**:
    - Auto-discovery of available ports (8090-8100). / 自动发现可用端口。
    - Global Registry service for finding instances by ID. / 全局注册表服务。
    - `python unity_skills.py --list-instances` CLI support.
- **Transactional Safety (Atomic Undo) / 原子化撤销**:
    - All operations now run within isolated Undo Groups. / 所有操作在隔离的 Undo 组中运行。
    - **Auto-Revert**: If any part of a skill fails, the *entire* operation is rolled back. / 失败自动全量回滚。
- **Batch Operations (批处理)**:
    - Added `*_batch` variants for all major skills (GameObject, Component, Asset, UI). / 全技能支持批处理。
    - 100x performance improvement for large scene generation. / 大规模生成性能提升 100 倍。
- **One-Click Installer for Codex (Codex 一键安装)**:
    - Added direct support for OpenAI Codex in the Skill Installer. / 安装器新增 Codex 支持。
- **Token Optimization (Token 优化)**:
    - **Summary Mode**: Large result sets are automatically truncated (`verbose=false`) to save tokens. / 结果自动截断。
    - **Context Compression**: `SKILL.md` rewritten for 40% reduction in System Prompt size. / 上下文压缩。

### 🛠 Improvements / 改进
- **UI Update**: UnitySkills Window now displays Instance ID and dynamic Port. / 面板显示实例 ID 和端口。
- **Client Library**: `UnitySkills` python class refactored for object-oriented connection management. / Python 客户端重构。

---

## [1.0.0] - 2025-01-22

### 🚀 Initial Product Release
This version represents the first stable release of UnitySkills, consolidating all experimental features into a robust automation suite.

### ✨ Key Features
- **100+ Professional Skills**: Modular automation tools across 14+ categories.
- **Antigravity Native Support**: Direct integration with Antigravity via `/unity-skills` slash command workflows.
- **One-Click Installer**: Integrated C# installer for Claude, Antigravity, and Gemini CLI.
- **REST API Core**: Producer-consumer architecture for thread-safe Unity Editor control.

### 🤖 Supported IDEs & Agents
- **Antigravity**: Full slash command and workflow support.
- **Claude Code**: Direct skill invocation and intent recognition.
- **Gemini CLI**: experimental.skills compatibility.

### 📦 Skill Modules Overview
- **GameObject (7)**: Hierarchy and primitive manipulation.
- **Component (5)**: Property劫持 and dynamic configuration.
- **Scene (6)**: High-level management and HD screenshots.
- **Material (17)**: Advanced shaders and HDR control.
- **UI (10)**: Canvas and element automation.
- **Animator (8)**: Controller and state management.
- **Asset/Prefab (12)**: Management and instantiation.
- **System (35+)**: Console, Script, Shader, Editor, Validation, etc.
