---
name: unity-pico-design
description: Doc-anchored design rules for PICO Unity Integration SDK v3.4.0 — XR/MR/platform services, version diffs 2.x-3.4, hallucination shields
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Writing or reviewing PICO Unity Integration SDK (`PXR_*` / `Pico.Platform`) code
- Setting up a PICO XR project or choosing the Unity OpenXR plugin route
- Video seethrough / spatial anchors / spatial mesh / SecureMR / platform services
- Migrating from SDK 2.x or an older 3.x version
- 编写或审查 PICO Unity SDK 代码、配置 PICO 项目或 OpenXR 路线、透视/锚点/网格/SecureMR/平台服务、2.x→3.x 版本迁移

# PICO Unity SDK - Design Rules（v3.4.0）

Advisory 模块。全部规则提炼自 **PICO 官方文档 v3.4.0**（2026-08-13 全量抓取：指南 203 页 + API 参考 32 页 + 更新说明 14 个版本 + 8 个版本的 API 快照 diff）。SDK 闭源，锚定方式为**官方文档 URL**——每条规则可点击复核。正文中文，API 名保持英文。

> **Mode**: Documentation only——无 REST skills 可门禁；Approval / Auto / Bypass 任何模式下均可自由加载。

## 为什么需要本模块

AI 训练语料中的 PICO 知识大量停留在 SDK 2.x / 早期 3.x：旧振动接口、AnchorEntity 事件三段式、`PXR_SDK >` 菜单、`PXR_EyeTracking` 类……这些在 3.4.0 已废弃或更名。3.2.0 一个版本就废弃了 PXR_Input 30 个成员、PXR_MotionTracking 27 个成员。凭记忆写 PICO 代码 = 高概率幻觉。**生成任何 PICO 代码前先加载本模块对应子文档。**

## Critical Rule Summary（跨域最高优先级，子文档细读前也要记住）

| # | 规则 | 详见 |
|---|------|------|
| 1 | PICO XR 插件与 Unity OpenXR 插件**二选一**：同时勾选只有 PICO XR 生效；OpenXR 路线勾选其他厂商插件会导致应用无法运行 | [SETUP.md](./SETUP.md) |
| 2 | `PXR_Manager` 必须挂**每一个场景**（含加载场景）；SDK 3.1.0 起仅支持 64 位（IL2CPP + 仅 ARM64），且 Unity 菜单是 `PICO` 不是 `PXR_SDK` | [SETUP.md](./SETUP.md) |
| 3 | `PXR_MotionTracking` 系方法返回 **int 状态码**（0 成功）不是 bool；`SetFoveationLevel` 是两参且 `None = -1` | [API_REFERENCE.md](./API_REFERENCE.md) |
| 4 | 旧触觉接口全组（`SetControllerVibration` / `StartVibrateBy*` / `CreateHapticStream` 等）3.2.0 已废弃 → 现行 `SendHapticImpulse` | [VERSIONS.md](./VERSIONS.md) |
| 5 | MR 全家（锚点/场景标定/平面检测）操作前必须 `StartSenseDataProvider`；anchor handle 重启失效，跨会话只能存 **UUID**；删锚点先 UnPersist 再 Destroy | [MR.md](./MR.md) |
| 6 | 视频透视四前置：禁后处理、Vulkan 下禁 HDR、Clear Flags=Solid Color、背景 RGBA 全 0；`EnableVideoSeeThrough` 生效有延迟且 resume 后要重调 | [MR.md](./MR.md) |
| 7 | 手柄与裸手**不能同时追踪**（Controller And Hands 是自动切换）；全身动捕与体感追踪器独立追踪互斥 | [INTERACTION.md](./INTERACTION.md) |
| 8 | 平台服务：`CoreService.AsyncInitialize` 判两层（`IsError` + `Success/AlreadyInitialized`）；房间/匹配/排行榜/成就还需 `GameInitialize` | [PLATFORM.md](./PLATFORM.md) |
| 9 | 面部追踪在 v3.4.0 是**官方矛盾区**：指南在教的 API 同版本已标 Deprecated，语料无替代类——可沿用但标注状态，**勿编造新 API** | [PITFALLS.md](./PITFALLS.md) |
| 10 | 渲染互斥矩阵：AppSW×BufferDiscards=撕裂、AppSW×内容保护=拖影、LateLatching×合成层=抖动、超分×锐化同 Buffer 只生效超分 | [RENDERING.md](./RENDERING.md) |

## Sub-doc Routing

| 子文档 | 何时读 |
|--------|--------|
| [SETUP.md](./SETUP.md) | 环境搭建、双插件路线选择、项目设置、AndroidManifest、Project Validation、Building Blocks、AR Foundation、工具与示例索引 |
| [RENDERING.md](./RENDERING.md) | FFR/ETFR、超分/锐化、合成层（Overlay/Underlay）、多视图、自适应分辨率、AppSW、Late Latching、URP、空间音频、性能目标 |
| [INTERACTION.md](./INTERACTION.md) | 控制器输入映射、触觉反馈、手势/眼动/面部/身体追踪、体感追踪器、系统键盘、与 XRI 的关系 |
| [MR.md](./MR.md) | 视频透视、空间锚点（含共享）、空间网格、场景标定、平面检测、MR 安全防护、空间数据权限、相机图像双路线、2.5→3.x MR 迁移 |
| [SECUREMR.md](./SECUREMR.md) | SecureMR 框架（3.2.0+）：Tensor/Operator/Pipeline 概念、gltf 渲染、Dynamic Texture、Readback、JavaScript Operator |
| [PLATFORM.md](./PLATFORM.md) | 平台服务精要：初始化、账号/好友、成就、排行榜、IAP、DLC、云存档、房间&匹配、RTC、内容保护、企业服务概览 |
| [API_REFERENCE.md](./API_REFERENCE.md) | XR 核心 9 类逐方法签名表（含 Deprecated 标记）、`PXR_Enterprise` 方法域清单、Legacy API 处置 |
| [VERSIONS.md](./VERSIONS.md) | 版本时间线（v2.0.1→v3.4.0）、逐版本方法级 API 增/删/废弃表、2.x→3.x 迁移要点——**废弃清单以本文件为单一事实源** |
| [PITFALLS.md](./PITFALLS.md) | 幻觉陷阱总闸：废弃 API 黑名单、签名语义陷阱、互斥矩阵、构建陷阱、官方文档自身矛盾清单、能力边界 |

## Routing to Other Modules

- 通用 XR Interaction Toolkit 交互（rig/抓取/传送/XR UI）→ [xr](../xr/SKILL.md)（本模块只覆盖 PICO 特有部分）
- 架构与生命周期决策 → [architecture](../architecture/SKILL.md) / [patterns](../patterns/SKILL.md) / [async](../async/SKILL.md)
- 性能审查热路径 → [performance](../performance/SKILL.md)
- URP 资产与渲染管线操作 → urp / graphics 功能模块

## Version Scope

锚定 **v3.4.0**（2026-02-27 上线，2026-08-13 抓取）。文档站可切换版本：3.4.0 / 3.3.2 / 3.3.0 / 3.2.0 / 3.1.0 / 3.0.5 / 3.0.0 / 2.5.0；更新说明覆盖至 v2.0.1。关键分水岭：

- **3.0.0**：MR 接口全面重构（AnchorEntity 事件式 → `*Async` + SenseDataProvider）
- **3.1.0**：菜单 `PXR_SDK` → `PICO`；仅支持 64 位应用
- **3.2.0**：史上最大废弃潮（PXR_Input 30 成员、PXR_MotionTracking 27 成员、PXR_System 面部/亮度组）；SecureMR 登场
- **3.3.0**：`PXR_Over Lay` → `PXR_Composition Layer`；部分 API 跨类搬家至 PXR_Enterprise
- **3.4.0**：平面检测、`PXR_CameraImage`（用户设备相机数据）、SecureMR JavaScript Operator

SDK 新版本发布后的语料更新方法见 memory：`temp/pico-crawl/` 脚本重跑即可。

## 使用铁律

1. 写 PICO 代码前先读对应子文档；**语料查不到的 API 宁可声明"官方文档未记载"也不要按记忆补全**。
2. 官方文档自身有矛盾（面部追踪、XRI 版本口径、SecureMR 双套命名）——矛盾区处置见 [PITFALLS.md](./PITFALLS.md) 第五节，不要只信单页。
3. 版本迁移问题一律先查 [VERSIONS.md](./VERSIONS.md) 的方法级增删表再回答。
