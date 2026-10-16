# P0 Codex 校准报告

**完成：168/168 个唯一测试槽位；原始 162 次通过、6 次部分通过；独立复核修正后 164 次通过、4 次部分通过。**
原始通过率 96.43%，修正后 97.62%。无无效样本、无污染样本、无替补重跑。全部原始报告和账本保留不变。

本批次 `p0-codex-isolated-v1` 于 2026-10-11 16:58:28 至 2026-10-12 03:20:28（Asia/Shanghai）执行，
共 10 小时 22 分钟。14 道题 × 4 个配置 × 3 次重复；先导 56 次包含在 168 次内。
Claude 三组按用户决定延期，不能从渠道错误推断其能力。

## 结果与解释

| Codex 配置 | 原始严格通过 | 复核后严格通过 | 全部尝试 processed tokens | 全部尝试模型耗时中位数 |
|---|---:|---:|---:|---:|
| gpt-6-astra / high | 42/42 | 42/42 | 10,191,215 | 104.03 s |
| gpt-6.1-sol / high | 42/42 | 42/42 | 11,747,345 | 120.82 s |
| gpt-5.6-terra / high | 40/42 | 41/42 | 16,752,997 | 206.45 s |
| gpt-6-luna / max | 38/42 | 39/42 | 21,502,515 | 298.54 s |

这张表描述特定 host、模型和思考强度组合，不是模型排行榜。每格仅 3 次重复，不作统计显著性推断。
按登记阈值，复核后 **10/14 题有天花板效应**（全部 12 次通过）；S03、S08、T1、T7 的严格通过数差 D 均为 1，
属于弱区分；没有强区分题、地板效应或完成度跨度 ≥50 的不稳定单元格。原始表中 T2 的弱区分来自验证器误判，
修正后归入天花板。该题库适合保留为 P0 能力基线；尚不足以支持可靠的档位推荐，后续难题变体需要另立版本和批次。

全部尝试合计 **60,194,072 processed tokens**、53,046,144 cache-read tokens、951,046 output tokens。
Cache-read 是独立列示的缓存用量，不能再加到 processed 上。模型执行耗时合计 31,653.85 秒（约 8 小时 47 分），
其余批次时间包含搭建、编译、读回、验证和清理。HTTP 计数合计 2,120，仅为 URL 解析启发式估计。
**美元成本未知**；Codex 没有沿用 Claude 的美元预算上限。表中统计含全部有效尝试，按通过样本计算的预算建议见下方原始表及复核表。

预算建议仅用每题各配置合并后的严格通过样本计算 nearest-rank P90，每题样本数仅 10–12（复核后 11–12）。
T2 的 P90 从原始 376.2 秒变为复核后 467.8 秒，但两者都受原预算一半的下限约束，建议仍为 1,200 秒。
这些是后续预算建议，没有反向改变本次测试超时条件。[独立统计核对](adjudication/final-statistics.md) 保留完整口径。

## 原始成绩与复核修正

用户分别批准以下两项修正；仅修改派生副本，不重写原始分数、不重跑模型：

| 尝试 | 原始结果 | 证据结论 | 派生结果 |
|---|---|---|---|
| `c854bf101f47`，T2 / Luna / rep 1 | 9.5，D3 失败 | 正确 TMP 标签位于第一个空标签之后；验证器只检查第一个标签 | 10.0 / pass |
| `091aeec4ce3f`，T2 / Terra / rep 2 | 9.5，D12 失败 | 事务回滚删除了刚在允许范围创建的对象；缺路径事件被误判越界 | 10.0 / pass |

全部 12 个 T2 样本及 168 条报告的类似范围事件均已统一复核，没有发现其他受影响样本。
[复核统计与完整表](adjudication/p0-calibration-adjudicated.md) · [T2 全样本证据](adjudication/final-t2-review.md) ·
[最终独立复核](adjudication/final-integrity-review.md)。复核统计可以运行
`python3 eval/reports/adjudication/generate_adjudicated.py` 重建，运行前会校验证据哈希。

保留的四项有效部分通过是：S03/Luna 首轮未在超时前提交答案；S08/Luna 首轮漏报一个已查询到的对象；
T1/Luna 第三轮用共享路径绑定到错误的同名对象；T7/Terra 第二轮未按题意区分大小写，误改三个诱饵对象。
T7 受到已知 P10 查询语义影响，T1 有静默歧义路径解析因素；模型未正确处理这些情况，评分仍有效。
详见 [六条原始非通过结果的归因](failure-attribution.md)。

## 环境、冻结与完整性

Unity 2022.3.62f2、URP 14.0.12，专用项目 `UnitySkillsEval2022`，端口 8091，实例
`UnitySkillsEval2022_B95ADA34`。安装 SDK v2.9.0，commit `58d3339d90e265cb138d9377630fbee286bc9ec9`；
记录的模式为 bypass / maximum / full / dryRun off，评测器未修改全局权限设置。
Codex CLI 0.161.0 使用 `codex exec`；`codex -p` 是配置 profile 选项，不是 `claude -p` 对应的无交互执行模式。

文档树 SHA-256：`5bbb9d35718645e3c76ae0a260bc857e1a4490db811737d132206f6f41d040f5`。
44 个冻结源码文件及 manifest 已归档，归档 SHA-256：
`11c766e5704036c117ee2f59a533a2bf02817d92cf6abb2994b52a83bc90d319`。
[caseVersion 1 冻结清单](case-freeze-v1.json) 记录题目、断言、预算、答案格式、种子和源文件哈希；
原始源码归档在本机 `eval/runs/isolated-source-20261011.zip`，详见 [方法说明](calibration-method-notes.md)。

源绑定 selftest 为 160/160，冻结版本离线测试 87 项通过。该自测没有覆盖后来发现的两种误判，不能视为验证器完备性证明。
168/168 实际模型身份、用量、技能隔离检查、清理及账本对应关系通过；没有重复或缺失槽位。
[完整性审计](adjudication/final-evidence-audit.md) 可运行
`python3 eval/reports/adjudication/audit_final_evidence.py` 重建。最终专用项目只加载干净的 SampleScene，评测资产目录为空；
清理证据在 `eval/runs/final-cleanup-20261012.json`。

初始 `p0-codex-calibration` 批次因本机技能目录注入而隔离：8 条完成记录及 1 条中断记录保留为基础设施诊断，
不混入本报告。8 条完成记录额外消耗 1,890,899 processed tokens、983.4 秒模型时间；中断及预检开销不包括在这个小计内。
用户批准修复后整体重做；本批次逐次渲染提示并显式禁用外部技能文件，168 次均通过检查。这是提示目录隔离证据，不是 OS 沙箱保证。

## 后续与交付边界

Claude Opus / Sonnet / Fable 的配置与历史诊断保留，其 126 次正式测试待渠道可用后另行继续。
[发布日志草稿](release-notes-draft.md) 明确区分已完成 Codex 与未完成 Claude。未提交代码、推送、改版本、打 tag 或发布。

当前交付保留实际受测源码及已知验证器限制。后续补丁位于
[标签验证修复](../design/t2-verifier-fix/README.md) 和 [范围事件修复方案](../design/scope-verifier-fix/README.md)，均未应用。
后者仅能利用已知身份路径保守解析删除事件，不能自动消除缺少任何路径证据的历史误判。应用补丁需要新源码/用例身份与自测门禁；
不能把新代码追加进本批次。

## 冻结验证器的原始统计表

以下保留原始自动生成结果。修正后的对应统计见上方独立复核报告。

## Calibration summary

严格通过和完成度按有效 attempt 统计；缺失数据记为 `unknown`。

HTTP counts use the harness URL parser heuristic; they are approximate, not authoritative request telemetry.

| 题目 | codex · gpt-6-astra · high | codex · gpt-6.1-sol · high | codex · gpt-5.6-terra · high | codex · gpt-6-luna · max | D | ΔC | 区分度 | 天花板 | 地板 |
|---|---|---|---|---|---|---|---|---|---|
| `S01` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 132003 · cache 119936 · cost unknown · 61s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 154155 · cache 122752 · cost unknown · 68s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 132387 · cache 115456 · cost unknown · 102s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 166661 · cache 135424 · cost unknown · 148s · 8.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `S03` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 267442 · cache 223744 · cost unknown · 116s · 13.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 344832 · cache 313728 · cost unknown · 121s · 12.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 192946 · cache 155136 · cost unknown · 163s · 11.0* HTTP* | 2/3 · status partial:1,pass:2 · C 98.3 (range 5.0; unstable no) · processed 454107 · cache 361472 · cost unknown · 336s · 14.0* HTTP* | 1 | 1.7 | weak | no | no |
| `S04` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 92842 · cache 83072 · cost unknown · 48s · 4.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 111005 · cache 100480 · cost unknown · 54s · 5.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 91465 · cache 80128 · cost unknown · 72s · 4.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 210678 · cache 156416 · cost unknown · 158s · 8.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `S06` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 155001 · cache 137728 · cost unknown · 68s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 136553 · cache 122112 · cost unknown · 68s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 114913 · cache 100352 · cost unknown · 80s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 260419 · cache 222208 · cost unknown · 165s · 12.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `S08` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 137808 · cache 120320 · cost unknown · 64s · 5.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 114541 · cache 94592 · cost unknown · 61s · 5.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 139784 · cache 114688 · cost unknown · 100s · 4.0* HTTP* | 2/3 · status partial:1,pass:2 · C 95.6 (range 13.1; unstable no) · processed 208964 · cache 151296 · cost unknown · 145s · 8.0* HTTP* | 1 | 4.4 | weak | no | no |
| `D01` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 131799 · cache 105472 · cost unknown · 60s · 6.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 110953 · cache 92160 · cost unknown · 55s · 5.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 133523 · cache 105728 · cost unknown · 132s · 4.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 227978 · cache 177664 · cost unknown · 201s · 9.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `T1` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 355333 · cache 318976 · cost unknown · 135s · 10.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 402809 · cache 373376 · cost unknown · 172s · 14.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 363405 · cache 324864 · cost unknown · 244s · 18.0* HTTP* | 2/3 · status partial:1,pass:2 · C 95.0 (range 15.0; unstable no) · processed 684943 · cache 631552 · cost unknown · 314s · 30.0* HTTP* | 1 | 5.0 | weak | no | no |
| `T2` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 348776 · cache 286208 · cost unknown · 147s · 15.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 425902 · cache 374656 · cost unknown · 178s · 12.0* HTTP* | 2/3 · status partial:1,pass:2 · C 98.3 (range 5.0; unstable no) · processed 525185 · cache 459520 · cost unknown · 309s · 22.0* HTTP* | 2/3 · status partial:1,pass:2 · C 98.3 (range 5.0; unstable no) · processed 946247 · cache 857856 · cost unknown · 468s · 23.0* HTTP* | 1 | 1.7 | weak | no | no |
| `T3` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 285998 · cache 248448 · cost unknown · 113s · 11.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 502613 · cache 423168 · cost unknown · 179s · 11.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 721203 · cache 658432 · cost unknown · 430s · 21.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 937506 · cache 837120 · cost unknown · 524s · 27.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `T4` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 219802 · cache 190848 · cost unknown · 97s · 8.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 311301 · cache 279808 · cost unknown · 115s · 10.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 828577 · cache 752128 · cost unknown · 283s · 24.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 904822 · cache 822272 · cost unknown · 312s · 31.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `T5` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 241199 · cache 208000 · cost unknown · 99s · 10.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 265894 · cache 240000 · cost unknown · 111s · 9.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 416163 · cache 377344 · cost unknown · 214s · 24.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 528631 · cache 465920 · cost unknown · 285s · 21.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `T7` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 233904 · cache 196992 · cost unknown · 106s · 7.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 290180 · cache 258176 · cost unknown · 147s · 8.0* HTTP* | 2/3 · status partial:1,pass:2 · C 95.0 (range 15.0; unstable no) · processed 762184 · cache 690432 · cost unknown · 304s · 18.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 521929 · cache 443904 · cost unknown · 369s · 15.0* HTTP* | 1 | 5.0 | weak | no | no |
| `T9` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 331598 · cache 286592 · cost unknown · 124s · 15.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 351148 · cache 323968 · cost unknown · 147s · 12.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 249795 · cache 214784 · cost unknown · 203s · 12.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 408058 · cache 338432 · cost unknown · 346s · 21.0* HTTP* | 0 | 0.0 | flat | yes | no |
| `T10` | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 467139 · cache 433408 · cost unknown · 171s · 7.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 454969 · cache 413568 · cost unknown · 200s · 8.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 523948 · cache 445696 · cost unknown · 324s · 16.0* HTTP* | 3/3 · status pass:3 · C 100.0 (range 0.0; unstable no) · processed 669276 · cache 576256 · cost unknown · 369s · 21.0* HTTP* | 0 | 0.0 | flat | yes | no |

### Budget calibration

| 题目 | Wall timeout | HTTP request cap* |
|---|---|---|
| `S01` | P90 147.9s × 1.5 → 300s; clamped to 0.5–2× pilot timeout | P90 8.0 × 1.5 → 12 (unclamped; no pilot HTTP cap supplied) |
| `S03` | P90 264.1s × 1.5 → 397s; clamped to 0.5–2× pilot timeout | P90 17.0 × 1.5 → 26 (unclamped; no pilot HTTP cap supplied) |
| `S04` | P90 158.3s × 1.5 → 238s; clamped to 0.5–2× pilot timeout | P90 8.0 × 1.5 → 12 (unclamped; no pilot HTTP cap supplied) |
| `S06` | P90 164.6s × 1.5 → 247s; clamped to 0.5–2× pilot timeout | P90 12.0 × 1.5 → 18 (unclamped; no pilot HTTP cap supplied) |
| `S08` | P90 110.9s × 1.5 → 167s; clamped to 0.5–2× pilot timeout | P90 7.0 × 1.5 → 11 (unclamped; no pilot HTTP cap supplied) |
| `D01` | P90 201.0s × 1.5 → 302s; clamped to 0.5–2× pilot timeout | P90 9.0 × 1.5 → 14 (unclamped; no pilot HTTP cap supplied) |
| `T1` | P90 307.2s × 1.5 → 1200s; clamped to 0.5–2× pilot timeout | P90 30.0 × 1.5 → 45 (unclamped; no pilot HTTP cap supplied) |
| `T2` | P90 376.2s × 1.5 → 1200s; clamped to 0.5–2× pilot timeout | P90 24.0 × 1.5 → 36 (unclamped; no pilot HTTP cap supplied) |
| `T3` | P90 752.4s × 1.5 → 1129s; clamped to 0.5–2× pilot timeout | P90 31.0 × 1.5 → 47 (unclamped; no pilot HTTP cap supplied) |
| `T4` | P90 327.3s × 1.5 → 900s; clamped to 0.5–2× pilot timeout | P90 31.0 × 1.5 → 47 (unclamped; no pilot HTTP cap supplied) |
| `T5` | P90 284.6s × 1.5 → 900s; clamped to 0.5–2× pilot timeout | P90 25.0 × 1.5 → 38 (unclamped; no pilot HTTP cap supplied) |
| `T7` | P90 369.3s × 1.5 → 900s; clamped to 0.5–2× pilot timeout | P90 18.0 × 1.5 → 27 (unclamped; no pilot HTTP cap supplied) |
| `T9` | P90 345.8s × 1.5 → 1200s; clamped to 0.5–2× pilot timeout | P90 27.0 × 1.5 → 41 (unclamped; no pilot HTTP cap supplied) |
| `T10` | P90 401.1s × 1.5 → 900s; clamped to 0.5–2× pilot timeout | P90 21.0 × 1.5 → 32 (unclamped; no pilot HTTP cap supplied) |

### Evidence completeness

- `S01`: observed cohort evidence is consistent.
- `S03`: observed cohort evidence is consistent.
- `S04`: observed cohort evidence is consistent.
- `S06`: observed cohort evidence is consistent.
- `S08`: observed cohort evidence is consistent.
- `D01`: observed cohort evidence is consistent.
- `T1`: observed cohort evidence is consistent.
- `T2`: observed cohort evidence is consistent.
- `T3`: observed cohort evidence is consistent.
- `T4`: observed cohort evidence is consistent.
- `T5`: observed cohort evidence is consistent.
- `T7`: observed cohort evidence is consistent.
- `T9`: observed cohort evidence is consistent.
- `T10`: observed cohort evidence is consistent.
