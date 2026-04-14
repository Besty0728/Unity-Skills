# 批处理与异步 Job 基础设施

## 2026-04-14

### 批处理统一模型
- 新增 `BatchTargetQuery` 作为统一筛选输入，优先覆盖场景对象治理类批处理。
- 批处理统一走 `preview -> confirmToken -> execute -> report` 四段式，不允许新能力直接无预览批改。
- preview 结果持久化到 `Library/UnitySkills/batch_state.json`，支持 token 过期清理。

### 异步执行模型
- 批处理执行统一走 `BatchJobService`，支持 `queued/running/reconnecting/completed/failed/cancelled`。
- Job 按 chunk 执行，执行中产生日志、进度、报告项。
- Domain Reload 后把运行中任务标成 `reconnecting` 并自动恢复运行时上下文。

### 报告与回滚
- 每个 batch job 完成后都生成结构化 report，包含 totals、items、failureGroups。
- 批处理执行期间自动绑定 `WorkflowManager` session，用 sessionId 作为 workflowId 返回。
- 当前回滚粒度是任务/会话级，不支持单 item 选择性回滚。

### 首批接入范围
- 通用：`batch_query_gameobjects`、`batch_query_components`、`batch_preview_*`、`batch_execute`、`batch_report_*`
- Job：`job_status`、`job_logs`、`job_list`、`job_wait`、`job_cancel`
- 治理：`batch_fix_missing_scripts`、`batch_standardize_naming`、`batch_set_render_layer`、`batch_replace_material`、`batch_validate_scene_objects`、`batch_cleanup_temp_objects`

### 场景理解层接口策略
- 感知层新增聚合入口 `scene_analyze`，同时保留并扩展细分入口：`scene_health_check`、`scene_contract_validate`、`project_stack_detect`、`scene_component_stats`、`scene_find_hotspots`。
- 现有 `scene_summarize`、`validate_scene`、`project_get_info` 等能力继续保留，作为底层原子能力复用，不做破坏式迁移。
- 约定校验首轮使用内置默认 contract（`Systems` / `Managers` / `Gameplay` / `UIRoot`），允许可选 JSON 数组覆盖，但不引入额外配置资产。
- 场景理解层首轮严格只读，只输出 `summary / stats / findings / recommendations / suggestedNextSkills`，不直接执行修复。

### 统一异步 Job 平台
- 新增平台级 `AsyncJobService`，与既有 `BatchJobService` 共用同一份 job 持久化；batch 继续走原执行器，test/package/script 统一挂到平台 job 存储与 `job_*` 查询接口。
- `job_*` 已从“batch 专用”提升为“平台级异步任务接口”，返回通用字段，并附带 `details/resultData` 承载测试统计、包状态、脚本编译反馈等能力特定摘要。
- Job 生命周期扩展到 `queued/running/waiting_external/waiting_domain_reload/reconnecting/completed/failed/cancelled`；Domain Reload 后所有非终态 job 统一规范为 `reconnecting` 再按各能力恢复。
- 恢复策略分层：
  - batch：恢复运行时上下文并继续 chunk 执行
  - package/script：通过轮询 Unity 当前状态恢复查询与最终收敛
  - test：依赖 `TestRunnerApi` 回调；若重载打断追踪，明确标记为恢复失败而不是静默挂起
- 脚本变更类 skill（create/append/replace/rename/move/delete）已改为返回 `jobId`，编译诊断通过统一 job 汇总，`script_get_compile_feedback` 保留为只读辅助接口。
