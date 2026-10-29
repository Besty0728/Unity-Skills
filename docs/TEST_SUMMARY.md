# 本轮模型测试汇总

Token 合并展示，不拆分缓存；“≥”表示已知小计，完整消耗未知。分数为原始任务分（满分 10），包含无效及受污染尝试，不等同于有效测试通过率。

| 模型 | 思考强度 | 测试智能体 | Token 总量 | 最高分 | 最低分 |
|---|---|---|---:|---:|---:|
| gpt-6-astra | high | Codex CLI | 10,191,215 | 10.0 | 10.0 |
| gpt-6.1-sol | high | Codex CLI | 11,747,345 | 10.0 | 10.0 |
| gpt-5.6-terra | high | Codex CLI | 16,752,997 | 10.0 | 8.5 |
| gpt-6-luna | max | Codex CLI | 21,502,515 | 10.0 | 8.5 |
| gpt-6.1-sol | xhigh | Codex CLI | ≥ 14,218,714 | 10.0 | 1.0 |
| gpt-6.1-sol | max | Codex CLI | ≥ 15,249,367 | 10.0 | 1.0 |
| gpt-6-astra | xhigh | Codex CLI | ≥ 13,600,606 | 10.0 | 3.5 |
| gpt-6-astra | max | Codex CLI | ≥ 15,040,206 | 10.0 | 4.5 |
| Claude Fable 5.1 | high | Claude Code | 2,569,105 | 10.0 | 10.0 |
| Claude Opus 5.5 | high | Claude Code | 3,781,802 | 10.0 | 9.5 |
| Claude Sonnet 5.5 | high | Claude Code | 2,575,477 | 10.0 | 10.0 |
| Gemini 4 Argon | xhigh | Claude Code | ≥ 1,764,097 | 10.0 | 1.5 |
| Gemini 3.8 Flash | medium | Antigravity CLI | ≥ 62,998,257 | 10.0 | 1.0 |
| Gemini 3.8 Flash | high | Antigravity CLI | ≥ 140,824,599 | 10.0 | 1.0 |
| Kimi K3 | max | Kimi Code CLI | ≥ 11,860,134 | 10.0 | 8.5 |
| Claude Haiku 5.5 | 默认 | Cursor CLI | ≥ 6,973,420 | 10.0 | 5.5 |
| Claude Sonnet 5.5 | 默认 | Cursor CLI | ≥ 4,278,155 | 10.0 | 10.0 |
| Claude Opus 5.5 | 默认 | Cursor CLI | ≥ 4,433,714 | 10.0 | 10.0 |
| Grok 4.7 | high | Cursor CLI | ≥ 28,717,726 | 10.0 | 1.5 |

## 累计运行耗时

按各组 CLI 调用耗时相加，包含调用内的工具执行、网络等待和超时；不含调用外的场景准备、清理、排期和暂停。

| 模型 | 思考强度 | 测试智能体 | 累计耗时（时:分:秒） |
|---|---|---|---:|
| gpt-6-astra | high | Codex CLI | 1:12:06 |
| gpt-6.1-sol | high | Codex CLI | 1:26:09 |
| gpt-5.6-terra | high | Codex CLI | 2:35:18 |
| gpt-6-luna | max | Codex CLI | 3:34:02 |
| gpt-6.1-sol | xhigh | Codex CLI | 2:36:04 |
| gpt-6.1-sol | max | Codex CLI | 3:34:26 |
| gpt-6-astra | xhigh | Codex CLI | 2:20:26 |
| gpt-6-astra | max | Codex CLI | 3:20:36 |
| Claude Fable 5.1 | high | Claude Code | 0:24:56 |
| Claude Opus 5.5 | high | Claude Code | 0:31:38 |
| Claude Sonnet 5.5 | high | Claude Code | 0:24:15 |
| Gemini 4 Argon | xhigh | Claude Code | 1:11:39 |
| Gemini 3.8 Flash | medium | Antigravity CLI | 3:34:32 |
| Gemini 3.8 Flash | high | Antigravity CLI | 6:12:04 |
| Kimi K3 | max | Kimi Code CLI | 3:02:00 |
| Claude Haiku 5.5 | 默认 | Cursor CLI | 0:31:57 |
| Claude Sonnet 5.5 | 默认 | Cursor CLI | 0:24:09 |
| Claude Opus 5.5 | 默认 | Cursor CLI | 0:28:48 |
| Grok 4.7 | high | Cursor CLI | 3:15:58 |
