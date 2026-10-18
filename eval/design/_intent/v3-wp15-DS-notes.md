# WP15 DS1 / DS3 设计说明（wp15-oc-b）

作者：Betsy（wp15-oc-b）。dev / standard，caseVersion 1，requires `{pipeline: "URP", mode: "bypass", dedicated: true, packages: []}`，`needs_focus = True`（teardown 删除 A/Scripts 会触发重编译）。文件：`SkillsForUnity/Evaluation~/pack/tasks/{DS1,DS3}/`、`eval/pack/private/{DS1,DS3}.py`、`eval/pack/solutions/{DS1,DS3}.py`、离线 `eval/tests/runner/fake_dev_ds.py` + `test_pack_dev_ds.py`。

## 与其他 dev 题一致的约定（照 wp11 的 T1）
- 类名 = `<Kind>_<种子词>_<runId>`（同一类型不会跨 run 残留）；提示用 `{cls}` 占位，脚本内容由 Agent 自己写，提示只规定类名、字段名/类型和行为。
- `SCENARIO(ctx)` 返回 `{"executionMode": "play", "timeoutSeconds": N, "steps": [...]}`：只有 `wait_seconds` + `read gameobject_get_info`，最后一个 `assert editor_get_state.isPlaying == true`；行为错误不会中断 scenario，`verify_runtime` 按提示里写明的容差窗口给每次采样评分（fraction），`scenario.passed`（无错误日志、日志完整）为 false 时运行时断言记 0。
- solutions 用 `script_create(content=...)` → `ctx.wait_job(jobId)` → `ctx.wait_idle()`；字段值用 `component_set_property` / `component_set_serialized_property`（引用用 `assetPath` 或 `referencePath` + `objectType="Transform"`，不用 `referenceInstanceId`，6000.4+ 上 instanceId 为 0）。
- 代码文件断言是"新增 .cs ⊆ 题目要求的脚本"（prohibition，critical），不要求脚本必须存在（存在性由 D1 判）。

## DS1 巡逻路径（30 分钟）
夹具：`R/<Post>`（随机位置、非零 yaw）> `<Guard>`（Capsule，世界位置 = WP_1 的世界位置）；`R/<Route>`（随机位置、非零 yaw）> `WP_1..WP_4`（矩形，边长 3–5）。规格 `patrol_spec(seed)`：speed ∈ {2, 2.5, 3}，pause ∈ {0.5, 0.75, 1}（公开/私有各一份逐字相同副本，测试钉住 200 个种子）。
行为（R2）：进入 Play 立即（不停顿）从当前位置以 speed 直线前往第 2 个路点，到点停 pauseSeconds，循环；用世界坐标。
采样：第一圈每段中点与每次停顿中点 + 第二圈第一段中点与第一次停顿中点，共 10 个时刻（由 C0 的路点世界坐标与规格算出，SCENARIO 与 verify_runtime 用同一函数）；窗口 ±0.3 秒、距离 0.1（提示 R4 写明）。
断言：D1 脚本存在/编译/类已加载 1.0；D2 组件字段 route/speed/pauseSeconds 1.25；D3 唯一且在 Guard 上 0.5；D4 无其他代码文件 0.5 critical；D5 Route/路点/Post/Guard 变换不变 0.75；D6 G1/G2 0.5 critical；D7 G4（非 .cs）0.25；D8 答案 0.5；D9 运行时采样 fraction 4.75。
negatives：local_waypoints {D9}、local_position {D9}、no_pause {D9}、no_loop {D9}、defaults_only {D2,D9}、on_post {D3,D9}、extra_script {D4}、rename_route {D5}、wrong_report {D8}、outside_root {D6}、direct_file {D7}、broken_script {D1,D2,D3,D9}；alternatives：coroutine_patrol（协程 + WaitForSeconds）、save_run_scene。

## DS3 可配置波次（35 分钟）
夹具：`R/<Arena>` > SpawnerA/SpawnerB/ContainerA/ContainerB（空物体）；预制体 `A/Prefabs/<Drone>_<k>.prefab`（Sphere）。规格 `wave_spec(seed)`：A count ∈ {3,4}、interval ∈ {0.8,1.0,1.2}；B count ∈ {5,6,7}、interval ∈ {0.5,0.6,0.7}（代码默认值 2 / 2.0 与之都不同）。
行为（R3）：每隔 interval 秒 Instantiate(prefab, container)，第一次在 interval 秒后，达到 count 停止；数值来自引用的资产。两个 Spawner 各引用一个资产 → 一次 Play 同时验证两种配置（"切换引用后行为改变"的等价形式，verify 没有写客户端，无法在两次运行之间切换引用）。
采样：每个 Spawner 各自相邻生成时刻的中点（含最后一次之后）+ 全部结束后 1 秒；窗口 ±0.15 秒 < interval/2，所以在自己的中点上窗口不跨生成时刻（测试钉住）；另一个 Spawner 的中点可能恰好落在本方生成时刻上，那一次采样允许两个值，不影响判别。
断言：D1 脚本 1.0；D2 资产类型 0.5；D3 资产值 1.0；D4 两个 Spawner 的接线（config/prefab/container 引用、唯一）1.5；D5 无其他代码 0.5 critical；D6 预制体/容器/Spawner/Arena 不变 0.5；D7 G1/G2 0.5 critical；D8 G4 0.25；D9 答案 0.5；D10 运行时各采样的数量窗口 fraction 2.5；D11 结束后数量恰好且子物体名以预制体名开头 1.25。
negatives：runtime_instance {D10,D11}、defaults_unset {D3,D10,D11}、spawn_at_zero {D10}、no_limit {D10,D11}、wrong_container {D10,D11}、swapped_configs {D4,D10,D11}、extra_script {D5}、rename_container {D6}、wrong_report {D9}、outside_root {D7}、direct_file {D8}、broken_config {D1,D2,D3,D4,D9,D10,D11}；alternatives：invoke_repeating、save_run_scene。

## 离线模型
`fake_dev_ds.DevFakeEditor` 在 `run_scenario` 里维护虚拟时钟（wait_seconds 推进），`play_start`/`simulate` 按组件字段值驱动行为模型：巡逻模型按路点/速度/停顿算位置（`_set_world`），生成模型按资产的 count/interval 在容器下造 `<prefab>(Clone)`。negative 的 C# 变体带 `// usk-fake: <variant>` 注释，假编辑器据此选错误行为（真实编辑器忽略注释）。语法错误用花括号不平衡制造，假编辑器的 `_compile` 能识别。

## 离线无法验证的点
- 真实 Play 下 `wait_seconds` 用墙钟而脚本用 `Time.deltaTime`：进入 Play 的头几帧可能卡顿（着色器、GC），采样窗口 ±0.3 / ±0.15 秒能吸收普通抖动，但首帧超过 0.33 秒的卡顿会让早期采样偏移；若实机出现，考虑把第一个采样推迟或放宽首个窗口。
- `gameobject_get_info.childCount/children` 在 Play 模式下读取实例化出的对象（名字带 "(Clone)"）——按插件实现推断。
- `component_set_serialized_property` 的 `referencePath` + `objectType="Transform"` 写 Transform 引用、`assetPath` 写 SO/预制体引用、`parse_ref` 读回 "<id>:<类型>:<名>"（SO 的类型名为类名，预制体为 UnityEngine.GameObject）——沿用 T1 的用法，实机门禁确认。
- 参考脚本的 `Vector3.MoveTowards` 到点判定与 `WaitForSeconds` 量化误差 ≤ 一帧。
- 假编辑器不会执行 C#：只证明验收逻辑对"规格行为"与"标记变体"的判别，不证明参考脚本本身能编译——实机门禁必看。

## 预估
DS1：15–25 分钟，通过率 50–75%（主要丢分：局部坐标、起步停顿、未设置场景值）。DS3：20–30 分钟，通过率 45–70%（主要丢分：资产值未设、首次生成时刻、运行时新建配置）。
