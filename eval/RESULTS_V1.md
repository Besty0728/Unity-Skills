# UnitySkills 模型评测 v1：历史结果

> 历史参考页，数据截至 2026-10-17，不再更新。v1 评测中的所有模型将在 v3.0 测试集上全量重测，届时以本页的成绩、用量与时间表为对照基线。

## 1. 说明

| 项 | 内容 |
|---|---|
| 范围 | 14 题题库 × 19 个模型配置 / 5 个智能体主机；8 个批次、602 个登记槽位、635 次模型尝试 |
| 时间 | 2026-10-11 至 2026-10-16（Asia/Tokyo） |
| 题库 | S01 S03 S04 S06 S08 D01 T1 T2 T3 T4 T5 T7 T9 T10（6 道基础题 + 8 道综合题），caseVersion 1；题面与断言见 [case-freeze-v1.json](reports/case-freeze-v1.json)，题目说明见 TESTING_GUIDE |
| 被测 SDK | UnitySkills v2.9.0，提交 `58d3339d`（含 §7 的已知缺陷 P1–P14） |
| 环境 | 专用工程 UnitySkillsEval2022：Unity 2022.3.62f2、URP 14.0.12；实例 UnitySkillsEval2022_B95ADA34，端口 8091；mode=bypass、tier=maximum、surface=full、dryRunPolicy=off；冻结文档树 SHA-256 以 `5bbb9d35` 开头。每次尝试使用独立场景与资产目录，事后清理 |
| 智能体 | Codex CLI（0.161.0）、Claude Code（2.1.296）、Antigravity CLI、Kimi Code CLI、Cursor CLI |
| 状态 | `pass` 全部断言通过；`partial` 得分大于 0 但有断言失败；`fail` 得 0 分；`invalid` 基础设施、证据或合规失败，不计入能力结果；`contaminated` 触及其他实例端口或用户 / 评测目录。每个槽位至多一次 invalid 替补。**有效 = pass + partial + fail**；每个槽位至多 1 条有效结果（已核对无重复） |
| 成绩口径 | 原始任务分，满分 10；复核 / 裁定另列，不改写原始分 |
| 配置名对照 | Astra 6 = `gpt-6-astra`，Sol 6.1 = `gpt-6.1-sol`，Terra 5.6 = `gpt-5.6-terra`，Luna 6 = `gpt-6-luna`；Antigravity 的 CLI 目录 ID 为 `gemini-3.8-flash-medium` / `gemini-3.8-flash-high`；Kimi 为 `kimi-code/k3` |

**定位**：以上均为历史结果，不会补跑，也不据此做模型排名或档位推荐。v1 题库已饱和：P0 Codex 组 14 题中 10 题为天花板（复核后 12/12 通过；原始为 9 题），其余 4 题（S03、S08、T1、T7）仅弱区分，没有强区分题。v3.0 测试集另立题库与批次。

**数据来源**：成绩、槽位与失败原因由各 runtime 的 `ledger.jsonl`（`kind=model`，剔除 `smoke` 与 `p0-codex-calibration`，按 `attemptId` 去重，共 635 条）及对应 `report.json` 重算；用量与耗时取自 TEST_SUMMARY，已与账本逐组核对（见 §8）；批次定义与复核见 TEST_RESULTS_CN、[p0-calibration](reports/p0-calibration.md)、[failure-attribution](reports/failure-attribution.md)。原始产物在本机 `eval/runs/`（git 忽略，约 69 GB）。

**归档**：原始详细报告（本页引用的 TEST_SUMMARY、TEST_RESULTS_CN、TEST_GEMINI_ARGON、TESTING_GUIDE 等）不随仓库提交，已归档在维护者本机：`~/UnitySkillsEvalArchive/v1-20261017/eval-v1-archive.tar.gz`。

## 2. 批次表

| 批次 | 主机 | 配置 | 登记槽位 | 日期（2026） | 尝试 | 有效结果摘要 |
|---|---|---|---:|---|---:|---|
| Codex P0 基线 | Codex CLI | Astra 6 / Sol 6.1 / Terra 5.6 high、Luna 6 max；每任务 3 次 | 168 | 10-11 ~ 10-12 | 168 | 168/168 有效：162 pass / 6 partial；复核后 164 / 4 |
| Codex 思考强度扩展 | Codex CLI | Sol 6.1、Astra 6 × xhigh / max；每任务 3 次 | 168 | 10-12 ~ 10-14 | 178 | 168/168 有效：167 pass / 1 partial；10 次 invalid 已替补 |
| Claude Code | Claude Code | Fable 5.1 / Opus 5.5 / Sonnet 5.5 high；每任务 1 次 | 42 | 10-12 ~ 10-13 | 42 | 42/42 有效：41 pass / 1 partial |
| Gemini 4 Argon | Claude Code | Gemini 4 Argon xhigh；每任务 1 次 | 14 | 10-16 | 16 | 14/14 有效：13 pass / 1 partial；2 条 invalid（见注 1） |
| Antigravity | Antigravity CLI | Gemini 3.8 Flash medium / high；每任务 3 次 | 84 | 10-13 ~ 10-14 | 104 | 原始有效 53/84（49 pass / 4 partial）；复核视图 76/84（70 / 6），8 个槽位无结果 |
| Kimi K3 | Kimi Code CLI | Kimi K3 max；每任务 3 次 | 42 | 10-13 ~ 10-14 | 43 | 42/42 有效：35 pass / 7 partial |
| Cursor Claude | Cursor CLI | Haiku / Sonnet / Opus 5.5，默认强度；每任务 1 次 | 42 | 10-15 | 42 | 35/42 有效（均 pass）；7 个槽位无有效结果 |
| Cursor Grok | Cursor CLI | Grok 4.7 high；每任务 3 次（见注 2） | 42 | 10-15 ~ 10-16 | 42 | 30/42 有效：29 pass / 1 partial；12 个槽位无有效结果 |
| **合计** | 5 个主机 | 19 个配置 | **602** | 10-11 ~ 10-16 | **635** | **552 个槽位有效（531 pass / 21 partial / 0 fail）；50 个槽位无有效结果** |

注：
1. Gemini 4 Argon 的账本共 16 条记录 = 15 次模型调用（S08 在旧的 300 s 上限处中断的原记录 + 补跑）+ 1 条 T9 模型启动前的 REST 502 记录（无模型调用）。TEST_GEMINI_ARGON 以 15 次调用 / 1 invalid 计。
2. Grok 的 42 个槽位含原始条件 9 个 + high-Fast 条件 33 个；Cursor Opus 的 S01 保留原登记 300k / medium / fast=false（invalid，CLI 显示 `Claude Opus 5.5 1M`），其余 13 个槽位为 native-default 条件。
3. 原计划的 Claude 126 槽位正式批次（3 个模型 × 14 题 × 3 次）已延期、未执行；Claude Code 的 42 槽位批次是另行登记的单次重复补充，不取代该计划，因此 Claude 每格仅 1 次。

## 3. 成绩表

原始任务分满分 10。有效最高 / 最低分只统计有效尝试；TEST_SUMMARY 的最低分包含 invalid 与 contaminated，因此有 7 行不同：Sol xhigh / max 1.0 → 10.0，Astra xhigh 3.5 → 10.0，Astra max 4.5 → 9.5，Gemini 3.8 Flash medium / high 1.0 → 9.5，Haiku（Cursor）5.5 → 10.0。Codex 扩展中的 1.0 / 3.5 / 4.5 全部来自 invalid 尝试（无消息的 `model_mismatch`、`token_usage_unavailable`），不是答错。

| 配置 | 主机 | 槽位 | 尝试 | 有效 | pass | partial | fail | invalid | contaminated | 有效最高分 | 有效最低分 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| gpt-6-astra / high | Codex CLI | 42 | 42 | 42 | 42 | 0 | 0 | 0 | 0 | 10.0 | 10.0 |
| gpt-6.1-sol / high | Codex CLI | 42 | 42 | 42 | 42 | 0 | 0 | 0 | 0 | 10.0 | 10.0 |
| gpt-5.6-terra / high | Codex CLI | 42 | 42 | 42 | 40 | 2 | 0 | 0 | 0 | 10.0 | 8.5 |
| gpt-6-luna / max | Codex CLI | 42 | 42 | 42 | 38 | 4 | 0 | 0 | 0 | 10.0 | 8.5 |
| gpt-6.1-sol / xhigh | Codex CLI | 42 | 43 | 42 | 42 | 0 | 0 | 1 | 0 | 10.0 | 10.0 |
| gpt-6.1-sol / max | Codex CLI | 42 | 44 | 42 | 42 | 0 | 0 | 2 | 0 | 10.0 | 10.0 |
| gpt-6-astra / xhigh | Codex CLI | 42 | 45 | 42 | 42 | 0 | 0 | 3 | 0 | 10.0 | 10.0 |
| gpt-6-astra / max | Codex CLI | 42 | 46 | 42 | 41 | 1 | 0 | 4 | 0 | 10.0 | 9.5 |
| Claude Fable 5.1 / high | Claude Code | 14 | 14 | 14 | 14 | 0 | 0 | 0 | 0 | 10.0 | 10.0 |
| Claude Opus 5.5 / high | Claude Code | 14 | 14 | 14 | 13 | 1 | 0 | 0 | 0 | 10.0 | 9.5 |
| Claude Sonnet 5.5 / high | Claude Code | 14 | 14 | 14 | 14 | 0 | 0 | 0 | 0 | 10.0 | 10.0 |
| Gemini 4 Argon / xhigh | Claude Code | 14 | 16 | 14 | 13 | 1 | 0 | 2 | 0 | 10.0 | 1.5 |
| Gemini 3.8 Flash / medium | Antigravity CLI | 42 | 51 | 29 | 27 | 2 | 0 | 22 | 0 | 10.0 | 9.5 |
| Gemini 3.8 Flash / high | Antigravity CLI | 42 | 53 | 24 | 22 | 2 | 0 | 27 | 2 | 10.0 | 9.5 |
| Kimi K3 / max | Kimi Code CLI | 42 | 43 | 42 | 35 | 7 | 0 | 1 | 0 | 10.0 | 8.5 |
| Claude Haiku 5.5 / 默认 | Cursor CLI | 14 | 14 | 11 | 11 | 0 | 0 | 2 | 1 | 10.0 | 10.0 |
| Claude Sonnet 5.5 / 默认 | Cursor CLI | 14 | 14 | 12 | 12 | 0 | 0 | 2 | 0 | 10.0 | 10.0 |
| Claude Opus 5.5 / 默认 | Cursor CLI | 14 | 14 | 12 | 12 | 0 | 0 | 2 | 0 | 10.0 | 10.0 |
| Grok 4.7 / high | Cursor CLI | 42 | 42 | 30 | 29 | 1 | 0 | 10 | 2 | 10.0 | 1.5 |
| **合计** | — | **602** | **635** | **552** | **531** | **21** | **0** | **78** | **5** | **10.0** | **1.5** |

Cursor 三行的强度为 CLI 默认值（登记值 Haiku high、Sonnet high、Opus medium，实际未验证）；Grok 行合并原始条件与 high-Fast 条件。

### 复核与裁定视图（与原始分数分列）

| 范围 | 原始 | 复核 / 裁定后 | 说明 |
|---|---|---|---|
| Codex P0 基线 | 162 pass / 6 partial | 164 pass / 4 partial | 两项已批准的 T2 验证器误判修正：`c854bf101f47`（Luna max，T2 rep1，D3：只读到第一个空 TMP 标签）、`091aeec4ce3f`（Terra high，T2 rep2，D12：无路径的回滚删除事件被误判越界）。只改派生副本，原始分数与账本不变；T2 因此由弱区分变为天花板题。详见 [p0-calibration](reports/p0-calibration.md)、final-t2-review |
| Codex 扩展 | 167 pass / 1 partial / 10 invalid | 同左 | `f694a1044ea9`（Astra max，T2 rep2，D12：回滚删除）另有单独复核（pass / 10.0），文档注明“尚不代表统一复核”，不在上面的两项之内；本页汇总均为原始口径，不含该复核 |
| Antigravity | 原始有效 53/84：49 pass / 4 partial（另 49 invalid、2 contaminated） | 复核视图 76/84：70 pass / 6 partial；8 个槽位无结果 | 复核视图 = 53 条原始有效 + 23 条经事实复核的原始 invalid（22 条为宿主工具 `manage_task` 越界、1 条为缓存用量更正；21 条任务分 10.0，2 条 9.5）。medium 39（36 / 3）、high 37（34 / 3）；`f5ab4ec9500e`（T1 high rep0）、`f5cd475881d7`（T7 medium rep1）保留 9.5 分的扣分。原始状态与用量不变；无法验证有效提示词隔离，不可与 Codex 直接比较 |
| 其余批次 | — | 原始状态均保持 | Claude Code 复核后仍为 41 pass / 1 partial；Kimi、Cursor、Gemini 4 Argon 没有改变状态的复核 |

## 4. 用量表

Token 为 TEST_SUMMARY 的合并展示（processed，不拆分缓存）；“≥”表示已知小计，完整消耗未知。总量含 invalid 尝试已记录的用量；平均每槽位 = 总量 ÷ 登记槽位，是近似值（分子含 invalid 与替补尝试，分母不是有效槽位数）。

| 配置 | 主机 | Token 总量 | 槽位 | 平均每槽位（约） |
|---|---|---:|---:|---:|
| gpt-6-astra / high | Codex CLI | 10,191,215 | 42 | 242,648 |
| gpt-6.1-sol / high | Codex CLI | 11,747,345 | 42 | 279,699 |
| gpt-5.6-terra / high | Codex CLI | 16,752,997 | 42 | 398,881 |
| gpt-6-luna / max | Codex CLI | 21,502,515 | 42 | 511,965 |
| gpt-6.1-sol / xhigh | Codex CLI | ≥ 14,218,714 | 42 | ≥ 338,541 |
| gpt-6.1-sol / max | Codex CLI | ≥ 15,249,367 | 42 | ≥ 363,080 |
| gpt-6-astra / xhigh | Codex CLI | ≥ 13,600,606 | 42 | ≥ 323,824 |
| gpt-6-astra / max | Codex CLI | ≥ 15,040,206 | 42 | ≥ 358,100 |
| Claude Fable 5.1 / high | Claude Code | 2,569,105 | 14 | 183,508 |
| Claude Opus 5.5 / high | Claude Code | 3,781,802 | 14 | 270,129 |
| Claude Sonnet 5.5 / high | Claude Code | 2,575,477 | 14 | 183,963 |
| Gemini 4 Argon / xhigh | Claude Code | ≥ 1,764,097 | 14 | ≥ 126,007 |
| Gemini 3.8 Flash / medium | Antigravity CLI | ≥ 62,998,257† | 42 | ≥ 1,499,959† |
| Gemini 3.8 Flash / high | Antigravity CLI | ≥ 140,824,599† | 42 | ≥ 3,352,967† |
| Kimi K3 / max | Kimi Code CLI | ≥ 11,860,134 | 42 | ≥ 282,384 |
| Claude Haiku 5.5 / 默认 | Cursor CLI | ≥ 6,973,420 | 14 | ≥ 498,101 |
| Claude Sonnet 5.5 / 默认 | Cursor CLI | ≥ 4,278,155 | 14 | ≥ 305,583 |
| Claude Opus 5.5 / 默认 | Cursor CLI | ≥ 4,433,714 | 14 | ≥ 316,694 |
| Grok 4.7 / high | Cursor CLI | ≥ 28,717,726 | 42 | ≥ 683,755 |
| **合计（口径不一，仅作规模参考）** | — | **≥ 389,079,451** | **602** | — |

† **Antigravity 的用量是 CLI 主机自身的会话汇总**（含缓存读取，约占 9 成），不是归因到模型服务商的用量，不可与其他主机比较。

各主机口径：Codex（`turn.completed.usage`）与 Claude Code（终态 `modelUsage`，含 Gemini 4 Argon）取自 CLI 返回的模型用量；Kimi 为自有主 wire 的增量；Cursor 为 CLI 终端 `result.usage` 小计，范围未经资格确认。“≥”的来源：Codex 四个扩展行各有 1–2 次 invalid 尝试没有用量记录（共 5 次）；Argon 的 S08 中断原记录缺终态用量；Antigravity 有 3 次尝试缺用量；Kimi 有 1 次额度中止的尝试用量未知；Cursor 全部为终端小计。美元费用多数未知或仅有目录价估算，本页不比较。

## 5. 时间表

累计耗时 = 各组 CLI 调用耗时相加，含调用内的工具执行、网络等待和超时，不含调用外的场景准备、清理、排期和暂停；与 TEST_SUMMARY 一致，已与账本的 `subject.wallSeconds` 逐组核对。平均每槽位 = 累计耗时 ÷ 登记槽位，是近似值（含 invalid 与替补尝试的耗时）。

| 配置 | 主机 | 累计 CLI 耗时（时:分:秒） | 槽位 | 平均每槽位（秒，约） |
|---|---|---:|---:|---:|
| gpt-6-astra / high | Codex CLI | 1:12:06 | 42 | 103 |
| gpt-6.1-sol / high | Codex CLI | 1:26:09 | 42 | 123 |
| gpt-5.6-terra / high | Codex CLI | 2:35:18 | 42 | 222 |
| gpt-6-luna / max | Codex CLI | 3:34:02 | 42 | 306 |
| gpt-6.1-sol / xhigh | Codex CLI | 2:36:04 | 42 | 223 |
| gpt-6.1-sol / max | Codex CLI | 3:34:26 | 42 | 306 |
| gpt-6-astra / xhigh | Codex CLI | 2:20:26 | 42 | 201 |
| gpt-6-astra / max | Codex CLI | 3:20:36 | 42 | 287 |
| Claude Fable 5.1 / high | Claude Code | 0:24:56 | 14 | 107 |
| Claude Opus 5.5 / high | Claude Code | 0:31:38 | 14 | 136 |
| Claude Sonnet 5.5 / high | Claude Code | 0:24:15 | 14 | 104 |
| Gemini 4 Argon / xhigh | Claude Code | 1:11:39 | 14 | 307 |
| Gemini 3.8 Flash / medium | Antigravity CLI | 3:34:32 | 42 | 306 |
| Gemini 3.8 Flash / high | Antigravity CLI | 6:12:04 | 42 | 532 |
| Kimi K3 / max | Kimi Code CLI | 3:02:00 | 42 | 260 |
| Claude Haiku 5.5 / 默认 | Cursor CLI | 0:31:57 | 14 | 137 |
| Claude Sonnet 5.5 / 默认 | Cursor CLI | 0:24:09 | 14 | 104 |
| Claude Opus 5.5 / 默认 | Cursor CLI | 0:28:48 | 14 | 123 |
| Grok 4.7 / high | Cursor CLI | 3:15:58 | 42 | 280 |
| **合计** | — | **40:41:03** | **602** | — |

合计为各行取整后相加。Gemini 4 Argon 含 S08 在旧 300 s 上限处中断的 0:05:00（之后放宽为 2 小时的保护性超时）；Antigravity 含 49 次 invalid 尝试的耗时，其 high 行平均每槽位因此偏高。

## 6. 失败原因

### 6.1 partial（21 条）

| 类别 | 条数 | 构成 | 说明 |
|---|---:|---|---|
| 保存了运行场景 | 9 | Antigravity：T1 ×3、T4 ×1；Kimi K3：T1、T3、T4、T5、S06 各 1 | 题面只写“不要切换、重新加载或另存场景”，断言却按“无场景保存”判定（B12 / M10 / V8 / T3·P10 为关键断言，S06·D6 非关键）。场景保存只出现在这两个主机（共 14 次尝试：9 partial、4 invalid、1 contaminated）；Kimi 的 5 次保存对应 3.5 分扣分 |
| T2 验证器误判 | 3 | Luna max（D3）；Terra high、Astra max（D12） | 模型实际正确：D3 只读取了第一个空 TMP 标签，D12 把无路径的回滚删除事件误判为越界。前两项为已批准的裁定，第三项为单独复核（见 §3） |
| 与 SDK 缺陷 / 路径歧义纠缠 | 3 | T7 Terra high；T1 Luna max rep2、Kimi K3 rep2 | T7：P10 参与——查询返回了大小写不同的诱饵，模型未做题面要求的大小写后过滤，误改 3 个诱饵并报告 76（应为 73）。T1：模型找到了正确的 Respawn Dummy，却用共享的 `referencePath` 写入，落到同名兄弟对象上，答案与实际状态不符（B9、B13）；归因为模型选择 / 回读错误，静默的歧义路径解析是诱因。与 P1 同类，但 P1 的签名（`entityId` 改写）未被触发，Kimi 一例据报告与流记录判断为同一模式 |
| 超时或缺少最终答案 | 4 | Luna max S03；Opus 5.5 T1；Kimi K3 T10；Grok 4.7 S03 | 前三条状态断言全过，只失败答案字段断言：S03 在 420 s 超时前未提交 final.json，Opus 把 final.json 写在了 `work/` 子目录，Kimi T10 缺 final.json。Grok S03 在 420 s 超时时仍未完成改父级，状态断言（含关键断言 B6）一并失败，得 1.5 分 |
| 能力缺失 | 2 | Luna max S08；Gemini 4 Argon T3 | S08：API 返回了全部 8 个目标，答案漏报 Enemy_17（总数 7）。T3：两次只读查询后正常结束，未做任何修改、未提交 final.json，得 1.5 分 |

T1 单题占 21 条 partial 中的 7 条。逐条明细（T3 的断言编号 P1–P11 与 §7 的 SDK 缺陷编号无关）：

| attemptId | 配置 | 任务 / rep | 分数 | 失败断言 | 类别 |
|---|---|---|---:|---|---|
| `f4cfe25cd5f2` | Gemini 3.8 Flash high（Antigravity） | T1 / 1 | 9.5 | B12（关键） | 保存了运行场景 |
| `a70a941dc9e3` | Gemini 3.8 Flash high（Antigravity） | T1 / 2 | 9.5 | B12（关键） | 保存了运行场景 |
| `de6e52134adc` | Gemini 3.8 Flash medium（Antigravity） | T1 / 2 | 9.5 | B12（关键） | 保存了运行场景 |
| `8225f3c97669` | Gemini 3.8 Flash medium（Antigravity） | T4 / 2 | 9.5 | M10（关键） | 保存了运行场景 |
| `3622c45200b5` | Kimi K3 max | T1 / 0 | 9.5 | B12（关键） | 保存了运行场景 |
| `d8a93b28d619` | Kimi K3 max | T3 / 0 | 9.5 | P10（关键） | 保存了运行场景 |
| `b0ec8d4d58f0` | Kimi K3 max | T4 / 1 | 9.5 | M10（关键） | 保存了运行场景 |
| `efb384ea0ab3` | Kimi K3 max | T5 / 0 | 9.0 | V8（关键） | 保存了运行场景 |
| `29f7f3ada314` | Kimi K3 max | S06 / 2 | 9.0 | D6 | 保存了运行场景 |
| `c854bf101f47` | gpt-6-luna max | T2 / 1 | 9.5 | D3 | T2 验证器误判 |
| `091aeec4ce3f` | gpt-5.6-terra high | T2 / 2 | 9.5 | D12（关键） | T2 验证器误判 |
| `f694a1044ea9` | gpt-6-astra max | T2 / 2 | 9.5 | D12（关键） | T2 验证器误判 |
| `981bede9c31a` | gpt-5.6-terra high | T7 / 1 | 8.5 | G6（关键）、G10 | 与 SDK 缺陷 / 路径歧义纠缠 |
| `4512471bfc36` | gpt-6-luna max | T1 / 2 | 8.5 | B9、B13 | 与 SDK 缺陷 / 路径歧义纠缠 |
| `3a4cb1173110` | Kimi K3 max | T1 / 2 | 8.5 | B9、B13 | 与 SDK 缺陷 / 路径歧义纠缠 |
| `a0ff98c77d5f` | gpt-6-luna max | S03 / 0 | 9.5 | B7 | 超时或缺少最终答案 |
| `5ae1007f9d61` | Claude Opus 5.5 high（Claude Code） | T1 / 0 | 9.5 | B13 | 超时或缺少最终答案 |
| `5073a0562db6` | Kimi K3 max | T10 / 2 | 9.5 | H8 | 超时或缺少最终答案 |
| `0865c9bcc321` | Grok 4.7 high（Cursor） | S03 / 0 | 1.5 | B1、B2、B3、B4、B6（关键）、B7 | 超时或缺少最终答案 |
| `1bd3f49416dd` | gpt-6-luna max | S08 / 0 | 8.6875 | E1、E4 | 能力缺失 |
| `898c000226dc` | Gemini 4 Argon xhigh（Claude Code） | T3 / 0 | 1.5 | P1、P2、P3、P4、P5、P6、P7、P8、P11 | 能力缺失 |

### 6.2 invalid（78 条）

invalid 反映证据 / 合规失败，不等于答错：78 条中任务分为 10.0 的 54 条、9.0–9.5 的 6 条、低于 9.0 的 17 条，另有 1 条无分数（模型启动前的 REST 502）。按主机：Antigravity 49（占其 104 次尝试的 47%）、Cursor 16（Claude 6 + Grok 10，占 84 次的 19%）、Codex 10（均在扩展批次，占 178 次的 5.6%）、Gemini 4 Argon 2、Kimi 1；Claude Code 的 Fable / Opus / Sonnet 与 Codex P0 基线为 0。

| 类别 | 条数 | 主机分布 | 原因 |
|---|---:|---|---|
| 宿主工具 / 后台进程越出冻结规则 | 41 | Antigravity 37、Cursor 4 | Antigravity 使用了冻结工具策略之外的宿主工具 `manage_task`（多为查询或取消自身后台任务），其中 5 条另用 `schedule`、2 条另有终态 ERROR、2 条另有工具步骤缺 done 事件；Cursor 的后台 shell 超出尝试条件（4）。41 条的任务分均不低于 9.5（37 条为 10.0） |
| 上游无响应、连接中断、模型身份未确认 | 21 | Codex 8、Antigravity 8、Cursor 5 | `model_mismatch` 9：无 init 事件的 8 条（Codex 3 条为平台 120 s 无响应、5/5 次重试用尽；Antigravity 2 条启动即失败，分别为要求登录和本机代理拒绝连接；Cursor 3 条启动前即被拒，无原生调用），另有 Cursor Opus S01 显示 `Claude Opus 5.5 1M` 与登记的 300k / medium / fast=false 不符 1 条。`api_retries ≥ 3` 6 条（Codex 5、Cursor Grok 1，任务分均为 10.0）。Antigravity 终态 `ERROR`（主机故障或未资格状态）6 条 |
| 用量证据缺失 / 不一致 | 3 | Codex 2、Antigravity 1 | Codex `token_usage_unavailable` 2 条（流中断、无用量，1.0 分）；Antigravity 缓存读取大于输入 1 条（后已更正用量） |
| 超时 | 2 | Gemini 4 Argon 1、Antigravity 1 | Argon S08 在旧的 300 s 上限处中断（放宽后补跑 pass）；Antigravity S03 high rep2 在 420 s 截止时没有终态结果 |
| 清理、所有权与资格未确认 | 3 | Antigravity 2、Kimi 1 | Antigravity T2 medium rep0、T10 medium rep2：进程归属跟踪在截止前中止（`pre-freeze process ownership unknown`）；Kimi T3 rep1：额度限制中止，会话身份与终态证据不完整 |
| 评测框架绑定 / 连接错误 | 8 | Cursor 7、Gemini 4 Argon 1 | Cursor Grok fast 请求绑定校验失败 6 条、Opus native-default 条件绑定校验失败 1 条（这 7 条中 5 条任务分为 10.0）；Argon T9 在模型启动前遇到 REST 502（无模型调用） |
| **合计** | **78** | Antigravity 49、Cursor 16、Codex 10、Argon 2、Kimi 1 | |

### 6.3 contaminated（5 条）

| attemptId | 配置 | 任务 / rep | 分数 | 触发原因 |
|---|---|---|---:|---|
| `4a304d7dec78` | Gemini 3.8 Flash high（Antigravity） | T4 / 0 | 9.5 | 账本记录向端口 80 发请求（登记实例端口为 8091） |
| `eb6348d0bba3` | Gemini 3.8 Flash high（Antigravity） | T4 / 1 | 10.0 | 端口 809 信号；复核确认来自被截断的命令摘要 `localhost:809…`，实际端口为 8091，原始状态不变 |
| `8c97a2e5e86e` | Claude Haiku 5.5（Cursor） | T2 / 0 | 10.0 | 触及 `.claude/skills` |
| `63164b80c4f6` | Grok 4.7 high（Cursor） | S03 / 1 | 4.5 | 触及 `.agents/skills`、`.claude/skills` |
| `2f5f1b6ba480` | Grok 4.7 high（Cursor） | S06 / 2 | 10.0 | 触及 `.claude/skills` |

## 7. 已知 SDK 问题

v2.9.0（`58d3339d`）的 14 项已知缺陷，评测时只记录、未修复；均计划在 v3.0 中处理。失败签名匹配且模型请求在文档契约之内时，复核标为 `product_defect`、不计入能力统计（端到端表保留）。完整证据见 [known-defects.md][kd]。“—”表示评审未记录到影响，不等于已证明无影响。

| ID | 摘要 | v1 评审中的记录 |
|---|---|---|
| [P1][kd] | 无原生 `entityId` 参数的技能（component_*、material_*、prefab_*、event_*、light_*、camera_*、ui_*、animator_*）会把 `entityId` 改写成路径或名称，同名对象被静默改错目标 | T1 同名目标 ×2（相关，未确认直接触发） |
| [P2][kd] | `pageOffset` / `pageLimit` 仅在 `verbose=false` 时生效，与协议文档“显式分页总是生效”不符 | — |
| [P3][kd] | `gameobject_find` / `scene_find_objects` 在 `limit`（默认 50）处截断，不返回总数或截断标志 | — |
| [P4][kd] | `prefab_get_overrides` 只返回数量（描述写“list”），且没有路径参数 | — |
| [P5][kd] | `event_add_listener` 描述支持 Object 参数，实现并不支持 | — |
| [P6][kd] | `material_create` 的提示推荐不存在的 `asset_save` | — |
| [P7][kd] | 缺失引用与 null 引用都读作 `"null"`，无法区分 | — |
| [P8][kd] | `physics_create_material` 在保存目录不存在时失败（`INTERNAL Invalid path`） | — |
| [P9][kd] | `component_get_serialized_properties` 遗漏不可见的序列化字段（如 `HingeJoint.m_ConnectedBody`） | — |
| [P10][kd] | `batch_query_*` 的 `namePattern` 不区分大小写，文档只写“regex” | T7（Terra high）已确认参与；S08（Luna max）已正确处理 |
| [P11][kd] | `batch_query_*` 的结果数被 `queryJson.limit`（默认 500）截断，无截断标志 | — |
| [P12][kd] | `prefab_find_instances` 只看到激活对象，并把实例的子物体也算作实例 | — |
| [P13][kd] | `event_get_listeners` 只报告目标名称，同名目标无法区分 | — |
| [P14][kd] | （未确认）Vector / Quaternion / Color 的描述使用区域性敏感的插值，Float 用不变区域 G9，仅非英文区域设置受影响 | — |

[kd]: known-defects.md

## 8. 局限

- **样本量小**：Codex P0 每个任务 × 配置 3 次；Claude Code、Gemini 4 Argon、Cursor Claude 每格 1 次；Antigravity（31 个槽位）、Cursor Claude（7）、Cursor Grok（12）有槽位没有有效结果。不做显著性检验。
- **天花板效应**：552 个有效结果中 531 个为 pass（96.2%）。这不是模型通过率（invalid 已排除，各批次主机、条件与重复次数不同），而是题库区分度不足的体现：P0 Codex 组 14 题中 10 题为天花板，S03、S08、T1、T7 弱区分，没有强区分题或地板题。
- **主机与条件不可直接比较**：Antigravity 与 Kimi 无法验证有效提示词隔离；强度（xhigh、max 等）是请求参数，服务端实际强度未独立验证；Cursor 为 native-default，实际 context / effort / fast 未验证，Grok 一行合并了两种条件。各主机的工具策略与 invalid 判据不同（Antigravity 104 次尝试中 49 次 invalid，主要因宿主工具 `manage_task`）。
- **记账口径**：Token 与耗时都含 invalid 与替补尝试；“≥”为已知下限；Antigravity 为 CLI 会话汇总（† 不可比）、Cursor 为终端小计、Kimi 为主 wire 增量；耗时不含调用外的准备、清理与排期；平均每槽位为近似值；美元费用多数未知，本页不比较。
- **裁定与原始分数分列**：两项已批准的 T2 修正、`f694a1044ea9` 的单独复核、Antigravity 的 76/84 复核视图都不改写原始分数、状态与用量；六条 Codex `cleanupOk=false` 记录（原始 pass / 10.0）未通过严格资格验证；两份历史 T2 空日志（`6a45943bd4e3`、`06e71649db2c`）的 D12 缺独立佐证。
- **题面与断言**：9 条 partial 来自“保存了当前场景”，而题面只禁止切换、重新加载与另存；Opus T1 的 1 条来自 final.json 写错目录。v3.0 若沿用这类断言，应在题面中写明，否则规则歧义会计入能力分。
- **SDK 缺陷**：v2.9.0 存在 P1–P14，T7 已确认受 P10 影响，T1 的同名目标失败与 P1 相关；v3.0 的 SDK 与题库均有变化，与 v1 不严格可比。
- **与来源文档的出入（已核对）**：① 账本重算的 Antigravity medium 用量为 62,878,934，TEST_SUMMARY 为 ≥ 62,998,257，差 119,323，即 `ccc3aec6f046` 的缓存读取更正（processed 103,875 → 223,198）；② Kimi 账本合计 12,271,325，TEST_SUMMARY 为 ≥ 11,860,134，差 411,191，即额度中止尝试 `a60972742058` 的不完整前缀（按“完整尝试小计”扣除）；③ Gemini 4 Argon 账本 16 条记录（2 条 invalid）对文档的 15 次调用（1 条 invalid），差 1 条模型启动前的 T9 REST 502 记录。其余 17 组用量与全部 19 组耗时和 TEST_SUMMARY 完全一致。
