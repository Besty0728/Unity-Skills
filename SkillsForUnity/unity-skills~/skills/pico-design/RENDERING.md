# PICO Unity SDK — 渲染与性能（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。

## 何时加载本文档

- 配置或调试注视点渲染（FFR / ETFR / Subsampling），或排查"开了却没生效"。
- 使用合成层（Composition Layer / PXR_Composition Layer / PXR_OverLay）做 UI、视频、360 全景、空间图片。
- 做画质与功耗取舍：MSAA、超采样、锐化、超分辨率、自适应分辨率、renderViewportScale、Eye Buffer 分辨率。
- 启用低延迟/省电特性：Application SpaceWarp、Late Latching、Multiview、Optimize Buffer Discards。
- URP 在 PICO 上出怪问题（FFR 失效、Underlay/VST 不显示、Eye Buffer 全黑、掉帧、崩溃）。
- 接入 PICO 空间音频渲染器（自由声场、环境声学、Ambisonics、MR 空间音频）。

## 关键规则

| # | 规则 | 来源 |
| --- | --- | --- |
| 1 | ETFR 与 FFR **互斥**，一个应用只能开其中一个。ETFR 仅支持搭载眼动追踪摄像头的机型：PICO Neo3 Pro Eye、PICO 4 Pro、PICO 4 Enterprise，且系统 5.7.0 及以上。 | [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/) |
| 2 | 切换注视点渲染模式必须**两步**：先 `SetFoveationLevel(FoveationLevel.None, 当前 isETFR)` 关闭，再用目标 `isETFR` 指定等级。FFR→ETFR 的第二次调用可能失败，需要再调用第三次。 | [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/) |
| 3 | URP + FFR 默认失效：URP 引入 Intermediate Texture，画面先渲到中间纹理而非 Eye Buffer。必须禁用 Post-Processing、HDR 及会用到 Intermediate Texture 的 Renderer Feature；URP 10.10.1 起 Clear Flags 选 Skybox 会丢失 Color Attachment 的 Invalidate，需在 `ScriptableRenderer.GetCameraClearFlag` 中注释掉 `CameraClearFlags.Skybox` 分支使其返回 `ClearFlag.All`。Built-in 管线同样需禁用后处理。 | [FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) |
| 4 | OpenGLES 图形接口 + Gamma 颜色空间 + FFR 三者同时使用时，**暂不支持** Subsampling（下采样，SDK 2.1.5 起提供）。 | [FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) |
| 5 | Eye Buffer 的超分辨率与锐化**不能在同一 Eye Buffer 内同时开启**，同开时 SDK 只用超分辨率；两者都不能与下采样同时开启；超分辨率仅对 Eye Buffer 有效，**不支持合成层**。二者环境要求：Neo3 / 4 / 4 Ultra 系列 + 系统 5.8.0 及以上。 | [super-resolution](https://developer-cn.picoxr.com/document/unity/super-resolution/) / [sharpening](https://developer-cn.picoxr.com/document/unity/sharpening/) |
| 6 | 对**同一个合成层**，超采样、锐化、超分辨率三者只能开一种；动态设置时若同时开启，仅超采样生效。Blurred Quad 层不支持 Enhance Mode 参数。 | [layer-ss-sharp-sr](https://developer-cn.picoxr.com/document/unity/enable-supersampling-sharpening-and-super-resolution-for-compositor-layers/) |
| 7 | 单个场景最多支持 7 个 VR 合成层，**建议不超过 4 个**；须遵循近处物体遮挡远处物体的规则，否则可能出现轻微晃动。对 3 个以上图层开超分辨率可能导致合成服务侧 GPU 紧张而画面撕裂。 | [overview](https://developer-cn.picoxr.com/document/unity/compositor-layer-overview/) / [super-resolution](https://developer-cn.picoxr.com/document/unity/super-resolution/) |
| 8 | URP 项目使用 Underlay 层必须**禁用 HDR**，否则 Underlay 不生效；Underlay 依赖渲染目标 alpha 通道，还需在 Eye Buffer 上"挖洞"——用 `Packages/PICO Integration/Assets/Resources/Shader` 下的 `PXR_UnderlayHole`，或自写 shader。 | [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) |
| 9 | 合成层 `Depth` 值**越小越靠近 Eye Buffer**（示意：`[Camera](Overlay)2/1/0[EyeBuffer]0/1/2(Underlay)`）。World-Locked 是默认行为；Head-Locked 需把挂载合成层脚本的对象作为 Main Camera 的子节点。 | [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) / [general-procedure](https://developer-cn.picoxr.com/document/unity/general-procedure-for-using-compositor-layers/) |
| 10 | AppSW 前置条件缺一不可：Neo3 / 4 / 4 Ultra 系列 + 系统 5.4.0+、Unity 2021 LTS 及以上（推荐 2021，2022/2023 可能不兼容）、Stereo Rendering Mode = **Multiview**、Graphics API 首位为 **Vulkan**、导入 `Pico-Developer/Unity-Graphics` 仓库 `AppSpacewarpForUnity` 分支的 URP/Core RP/Shader Graph 包并替换动态物体 Shader。Unity 6 及以上还需 URP 17.0.3+ 且 Render Graph 处于 **Compatibility Mode (Render Graph Disabled)**。 | [AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/) |
| 11 | AppSW 与 Optimize Buffer Discards **不能同时开启**（AppSW 需要深度缓冲区，而缓冲区丢弃优化会丢弃深度内容 → 画面撕裂）；AppSW 与内容保护同开会导致画面抖动及拖影。 | [AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/) |
| 12 | Late Latching 要求：Neo3 / 4 / 4 Ultra 系列 + 系统 5.6.0+，**必须先开多视图渲染**且 Graphics API 首位为 Vulkan。勾选 PXR_Manager 的 `Use Late Latching` 后，`PXR_Late Latching` 脚本会自动挂到 Main Camera。已知问题：与合成层（Overlay/Underlay）同用会导致合成层抖动。Debug 模式仅支持 Development Build，需 SDK 2.2.0+ 与 Unity 2021.3.19f 及以上，**暂不支持 Unity 2022**。 | [late-latching](https://developer-cn.picoxr.com/document/unity/late-latching/) |
| 13 | 自适应分辨率要求系统 5.7.0+；`Min/Max Adaptive Resolution Scale` 取值 0.7–1.3（默认分别为 0.7 与 1.26），基准分辨率 1504×1504，实际分辨率 = 1504 × Scale。已知问题：URP 的 `pipelineAsset.renderScale` 会**覆盖**最大自适应分辨率比例。 | [adaptive-resolution](https://developer-cn.picoxr.com/document/unity/adaptive-resolution/) |
| 14 | `XR.XRSettings.renderViewportScale` 默认 1.0、取值 0.0–1.0，运行时可改且**无需重新分配眼部纹理**——这是动态调节渲染像素的首选。相机正在渲染时修改会被系统忽略并记录错误日志；在 Gameplay 更新过程中修改需等下一帧生效。 | [render-viewport-scaling](https://developer-cn.picoxr.com/document/unity/render-viewport-scaling/) |
| 15 | `XRSettings.eyeTextureResolutionScale` 默认 1.0、取值 0.8–2.0，**不建议高于 1.5**；设置 >2.0 无效并回落默认值，超出设备上限则自动取设备最大分辨率。修改 Eye Buffer 总会重新分配眼部纹理，成本高。URP 项目改用 `((UniversalRenderPipelineAsset)GraphicsSettings.renderPipelineAsset).renderScale`。 | [modify-eye-texture-resolution](https://developer-cn.picoxr.com/document/unity/modify-eye-texture-resolution/) |
| 16 | MSAA 默认为 4x，通过 PXR_Manager 的 `Use Recommended MSAA` 勾选（默认已勾）。要自定义倍数**必须先取消该勾选**，再到 Project Settings > Quality > Rendering > Anti Aliasing 设置；性能吃紧时也建议至少开 2x。 | [anti-aliasing](https://developer-cn.picoxr.com/document/unity/anti-aliasing/) / [enhance-image-quality](https://developer-cn.picoxr.com/document/unity/enhance-image-quality/) |
| 17 | URP 已知问题清单（配置前逐条核对）：Unity 2021+ 用 URP 设 MSAA 会掉帧；Vulkan 相比 OpenGLES 存在帧率低、内存与 GPU 占用高的问题；Vulkan + URP + HDR 同用时 Underlay 层与 VST 层无法显示；URP Renderer 启用 SSAO 导致低帧率高消耗；Unity 2022+ 用 OpenGLES + MultiView 且引入了未使用的 URP 时，需删除 URP Package 并删除重加场景灯光，否则可能崩溃；Unity 6 + URP + OpenGL + Multi-pass + MSAA（非 Disabled）会导致 Eye Buffer 内容无法渲染。 | [URP](https://developer-cn.picoxr.com/document/unity/universal-render-pipeline/) |
| 18 | 开启多视图渲染（Multiview，即原单通道立体渲染）后再使用 Post Processing 会造成性能损耗、影响帧率。多视图可减少一半绘制调用和遮罩剔除。 | [multiview](https://developer-cn.picoxr.com/document/unity/multiview-rendering/) |
| 19 | 性能目标（PICO Neo 3 系列）：FPS 至少 72 帧/秒，三角面数量控制在 **100 万以内**。默认屏幕刷新率 72 Hz，可选 Default / 72 / 90（推荐）/ 120。Optimize Buffer Discards 要求 Vulkan + 系统 5.3.0 及以上。 | [performance-target](https://developer-cn.picoxr.com/document/unity/performance-target/) / [display-refresh-rate](https://developer-cn.picoxr.com/document/unity/display-refresh-rate/) / [buffer-discards](https://developer-cn.picoxr.com/document/unity/optimize-buffer-discards/) |
| 20 | 空间音频要求 Neo3 / 4 / 4 Ultra 系列 + 系统 5.12.0 及以上，官方仅在 Unity 2021.1.9f1c1 与 2020.3.21f1 LTS 验证通过。使用环境声学模拟时，**同时发声且开启多普勒效应的声源数量须在 20 以内**；MR 空间音频（虚拟声与真实空间网格交互）**仅 PICO 4 Ultra 系列**支持。 | [spatial-audio](https://developer-cn.picoxr.com/document/unity/spatial-audio/) |

## 工作流程

### A. 开启注视点渲染（FFR 或 ETFR）

1. Hierarchy > **+ > XR > XR Origin (VR)** 添加 XR Origin；选中它 > Inspector 底部 **Add Component** > 搜索并添加 **PXR_Manager**。
2. 在 **PXR_Manager (Script)** 面板设置 `Foveated Rendering Mode`：`Fixed Foveated Rendering` 或 `Eye Tracking Foveation Rendering`。
3. 设置 `Foveated Rendering Level`：`None` / `Low` / `Med` / `High` / `Top High`（High 与 Top High 目前效果一致）。选择非 None 后面板出现 `Subsampling` 选框。
4. FFR 推荐勾选 `Subsampling`；ETFR 可选。下采样消除视野边缘低分辨率区域的视觉伪影，画面过渡更平滑、减少眩晕感。
5. 运行时切换：`PXR_FoveationRendering.SetFoveationLevel(level, isETFR)`。跨模式切换必须先设 `FoveationLevel.None`（见规则 2）。
6. 启用 ETFR 后 SDK 自动向 AndroidManifest.xml 写入 `picovr.software.eye_tracking` meta-data 与 `com.picovr.permission.EYE_TRACKING` 权限，非必要不要改；用自定义 Manifest 时需自行补齐。
7. 验证：日志 `Pxr_SetEyeFoveationLevelEnable` 中 `bSupported` 表示设备是否支持眼动，`level` 为 -1/0/1/2/3 对应 关闭/Low/Med/High/Top High，`result = 0` 表示成功。
   来源：[FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) / [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/)

### B. 搭一个 Overlay Quad 合成层

1. 场景添加 XR Origin 并挂 **PXR_Manager**；把 XR Origin 下的 Main Camera 的 Tag 设为 **MainCamera**，删除场景自带的 Main Camera。
2. **GameObject > 3D Object** 建一个可见的 3D 对象（如 Cube），Add Component 搜索 **PXR_Composition Layer** 添加。
3. 基础参数：`Type = Overlay`、`Shape = Quad`、`Depth = 0`。
4. `Texture Type` 选 `Dynamic Texture`（每帧更新，如 RenderTexture）或 `Static Texture`（静态图片），再在 `Texture` 指定左右眼纹理——**左右眼须为同一张纹理且宽高一致**，需要 3D 效果时才分别指定。
5. 可选：勾 `Texture Rects` 配 `Source Rects` / `Destination Rects`；勾 `Layer Blend` 配 Src/Dst Color 与 Alpha；勾 `Override Color Scale` 配 `Scale`/`Offset`（XYZW 对应 RGBA，最终颜色 = 原始颜色 × Scale + Offset）。
6. Head-Locked：把上述 3D 对象建成 **XR Origin > Camera Offset > Main Camera** 的子对象，其余参数同上。
7. 视频/全景层变体：`External Surface` 用于 Android 播放器视频纹理（清晰度更佳，可勾 `DRM`、选 `3D Surface Type`）；`Shape = Equirect` 做 360/180 全景；`Shape = Eac` 后用 `Model Type` 选四种 EAC 模式；`Shape = Blurred Quad` 做空间图片/视频（**目前仅支持 External Surface**）。
   来源：[general-procedure](https://developer-cn.picoxr.com/document/unity/general-procedure-for-using-compositor-layers/) / [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) / [EAC](https://developer-cn.picoxr.com/document/unity/use-the-eac-layer/) / [blurred-quad](https://developer-cn.picoxr.com/document/unity/use-the-blurred-quad-layer/)

### C. 启用 Application SpaceWarp（七步）

1. **Edit > Project Settings > XR Plug-in Management > PICO**，`Stereo Rendering Mode = Multiview`。
2. 克隆 `Pico-Developer/Unity-Graphics`，`git checkout "AppSpacewarpForUnity"`；**Window > Package Manager > + > Add package from disk** 导入 Core RP Library、Shader Graph、Universal RP 三个 package.json。
3. 启用 URP：创建 URP Asset、加入 Graphics 配置、禁用 HDR、升级 Materials。
4. 把场景内动态物体的 Shader 换成能生成运动矢量的 URP Shader（参考 `com.unity.render-pipelines.universal/Shaders/Lit.shader`）。
5. **Edit > Project Settings > Player > Other Settings > Rendering**，Graphics API 添加 **Vulkan** 并移到首位。
6. **XR Plug-in Management > PICO > Android Settings** 勾选 `Application SpaceWarp`；场景中 XR Origin 挂 PXR_Manager，新建脚本在 `Start` 调 `PXR_Manager.Instance.SetSpaceWarp(true)`。
7. 验证：`PxrMetric` 日志形如 `FPS=36/72,MTP=40.60ms,AppSW=on,...`。排查：简洁背景的直线/网格可能扭曲失真，高速旋转物体周围可能出现伪影（降低转速）。
   来源：[AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/)

### D. 画质/功耗档位调优顺序

1. **先定基线**：MSAA 保持推荐 4x；Multiview 打开；确认刷新率档位（72/90/120）与目标 FPS。
2. **再调分辨率**：性能有余量 → 提 `eyeTextureResolutionScale`（≤1.5）或 URP `renderScale`；性能吃紧 → 开自适应分辨率（`Adaptive Resolution` + Min/Max Scale + `Power Setting`），或运行时用 `renderViewportScale` 动态降。
3. **最后上后处理增强**：Eye Buffer 侧在 PXR_Manager 勾 `Super Resolution` 或选 `Sharpening Mode`（二选一，锐化不可选时先取消 Super Resolution 勾选），运行时用 `PXR_Plugin.Render.UPxr_SetSuperResolutionOrSharpening`。锐化最佳实践：同时开 Fixed Foveated 与 Self Adaptive 增强（即 `Both`），可明显降功耗且保持视觉效果。
4. **合成层单独设**：在 PXR_Composition Layer 上按层选 `Supersampling Mode` / `Sharpening Mode` / `Super Resolution`（同层三选一）。
5. **纹理与文字**：所有纹理开 Generate Mip Maps + `Filter Mode = Trilinear`；墙面/地面等斜视角纹理再设 `Aniso Level`；文字用 TextMeshPro 而非 Text 组件。
6. **测量**：Window > Analysis > **Profiler**（Ctrl+7 / Command+7）、Window > Analysis > **Frame Debugger** 看 draw call、Scene 视图下拉 > Miscellaneous > **Overdraw** 看超量绘制热图。
   来源：[enhance-image-quality](https://developer-cn.picoxr.com/document/unity/enhance-image-quality/) / [sharpening](https://developer-cn.picoxr.com/document/unity/sharpening/) / [adaptive-resolution](https://developer-cn.picoxr.com/document/unity/adaptive-resolution/) / [profiler](https://developer-cn.picoxr.com/document/unity/performance-monitoring-and-analysis/) / [overdraw](https://developer-cn.picoxr.com/document/unity/view-overdraw/)

### E. 接入空间音频

1. 建一个 GameObject，Add Component **PXR_Audio_Spatializer_Context**，配 `Spatializer Api Impl`（Unity 或 Wwise）与 `Rendering Quality`。
2. 声源对象加 **PXR_Audio_Spatializer_Audio Source**（会自动补 Audio Source 组件，**建议 `Spatial Blend` 保持 0**），设置 AudioClip、Play On Awake、Loop。
3. 挂着 Audio Listener 的对象（一般是 Main Camera）加 **PXR_Audio_Spatializer_Audio Listener**，选 `Output Method`：`On Audio Filter Read`（常规）或 `Pico Audio Router`（走 Audio Mixer 便于独立音量与后处理）。
4. 环境声学：在参与模拟的模型上加 **PXR_Audio_Spatializer_SceneGeometry**（自动带出 **PXR_Audio_Spatializer_SceneMaterial**），按材质拆分 Mesh 分别设 `Material Preset` / Absorption / Scattering / Transmission；必要时用 Bake 烘焙静态 Mesh。
5. Ambisonics：Project Settings > Audio 把 `Ambisonic Decoder Plugin` 设为 **PICO Ambisonic Decoder**；建 Audio Mixer，Master 声道 Add Effect > **PICO Ambisonic Renderer**；对象加 **PXR_Audio_Spatializer_Ambisonic Source** 并把 Audio Source 的 Output 指向该 Mixer；音频须为一阶 Ambix 格式并勾 `Ambisonic`。
6. MR 空间音频（仅 PICO 4 Ultra 系列）：PXR_Manager 勾 `Spatial Mesh` 选 LOD → 挂 **PXR_Spatial Mesh Manager** 并指定带 MeshFilter 的 Mesh Prefab → 挂 **PXR_Audio_Spatializer_MR Scene Geometry Manager**。
7. 编辑器设置：**Edit > Preferences > General** 把 `Script Changes While Playing` 改为 `Recompile After Finished Player` 或 `Stop Playing And Compile`，否则播放音频时编译会导致编辑器崩溃。
   来源：[spatial-audio](https://developer-cn.picoxr.com/document/unity/spatial-audio/)

## 核心 API 锚点

### 组件 / 脚本名（注意官方文档同名多写法）

| 正确名称 | 说明 |
| --- | --- |
| `PXR_Manager` | 总控脚本，挂在 XR Origin 上。承载 Foveated Rendering Mode/Level、Subsampling、Super Resolution、Sharpening Mode、Adaptive Resolution、Use Late Latching、Use Recommended MSAA、Open Screen Fade、Use Premultiplied Alpha、Layer Blend、Spatial Mesh 等 |
| `PXR_Composition Layer` / `PXR_Over Lay` / `PXR_OverLay.cs` | **同一个合成层组件在文档中的三种写法**：面板/Add Component 名为 PXR_Composition Layer（旧文写作 PXR_Over Lay），源文件为 PXR_OverLay.cs |
| `PXR_UnderlayHole` | Underlay 挖洞脚本，位于 `Packages/PICO Integration/Assets/Resources/Shader` |
| `PXR_Late Latching` | 勾选 Use Late Latching 后自动挂到 Main Camera |
| `PXR_Screen Fade` | 勾选 Open Screen Fade 后自动挂到 Main Camera；参数 `Gradient Time`（默认 5 秒）、`Fade Color`（默认黑） |
| `PXR_Spatial Mesh Manager` | MR 空间音频/空间网格，需指定 `Mesh Prefab` |
| `PXR_Audio_Spatializer_Context` / `_Audio Source` / `_Ambisonic Source` / `_Audio Listener` | 空间音频基础脚本（`Packages/PICO Integration/SpatialAudio/Runtime`） |
| `PXR_Audio_Spatializer_API` / `_Types` | 空间音频辅助脚本 |
| `PXR_Audio_Spatializer_Scene Geometry` / `_Scene Material` | 环境声学模拟脚本（面板显示为 SceneGeometry / SceneMaterial） |
| `PXR_Audio_Spatializer_MR Scene Geometry Manager` | MR 空间音频管理器 |

### 运行时方法

```csharp
PXR_FoveationRendering.SetFoveationLevel(FoveationLevel level, bool isETFR);  // 切换模式或等级
PXR_FoveationRendering.GetFoveationLevel();                                   // 获取等级
PXR_FoveationRendering.SetFoveationParameters(...);                           // FFR 文档标注为 Deprecated
PXR_Manager.Instance.SetSpaceWarp(bool);                                      // AppSW 开关（实例方法）
PXR_Plugin.Render.UPxr_SetSuperResolutionOrSharpening(SuperResolutionOrSharpeningType type);
UnityEngine.XR.XRSettings.renderViewportScale;        // 0.0–1.0，默认 1.0
UnityEngine.XR.XRSettings.eyeTextureResolutionScale;  // 0.8–2.0，默认 1.0
((UniversalRenderPipelineAsset)GraphicsSettings.renderPipelineAsset).renderScale;  // URP 分支
```

事件（焦点感知只有事件、没有接口）：`PXR_Plugin.System.FocusStateLost`、`PXR_Plugin.System.FocusStateAcquired`。

空间音频工具函数：`Resume`、`SetGainDB`/`GetGainDB`、`SetReflectionGainDB`/`GetReflectionGainDB`、`SetSize`/`GetSize`、`SetDopplerStatus`/`GetDopplerStatus`、`GetAttenuationMode`、`SetMinAttenuationRange`/`GetMinAttenuationRange`、`SetMaxAttenuationRange`/`GetMaxAttenuationRange`、`SetDirectivity(alpha, order)`；Context 侧 `SetRenderingQuality(PXR_Audio.Spatializer.RenderingMode quality)`。

### 枚举与字段

- `FoveationLevel`：文档代码示例中出现过的成员只有 `FoveationLevel.None` 与 `FoveationLevel.High`；面板等级为 None / Low / Med / High / Top High，日志映射 -1 / 0 / 1 / 2 / 3。
- `SuperResolutionOrSharpeningType`：`None`、`SuperResolution`、`NormalSharpening`、`NormalSharpeningAndFixedFoveated`、`NormalSharpeningAndSelfAdaptive`、`NormalSharpeningAndFixedFoveatedAndSelfAdaptive`、`QualitySharpening`、`QualitySharpeningAndFixedFoveated`、`QualitySharpeningAndSelfAdaptive`、`QualitySharpeningAndFixedFoveatedAndSelfAdaptive`（设任一值会自动把其他设为 None）。
- `HDRFlags`：`None`、`HdrPQ`、`HdrHLG`（仅 External Surface 纹理支持 HDR 视频）。
- `BlurredQuadMode`：代码字段 `blurredQuadMode = BlurredQuadMode.SmallWindow`，面板值 Small Window / Immersion；其余字段 `blurredQuadScale`(0.5)、`blurredQuadShift`(0.01)、`blurredQuadFOV`(70)、`blurredQuadIPD`(0.064)，直接改 PXR_OverLay.cs 中的值会每帧生效。
- 合成层 Inspector 取值：`Type` = Overlay / Underlay；`Shape` = Quad / Cylinder / Equirect / Cubemap / Equi-Angular Cubemap (EAC) / Blurred Quad；`Texture Type` = External Surface / Dynamic Texture / Static Texture；`Source Rects` = Mono Scopic / Stereo Scopic / Custom；`Destination Rects` = Default / Custom；`3D Surface Type` = Single / Left Right / Top Bottom；EAC `Model Type` = EAC 360 / EAC 360 View Port / EAC 180 / EAC 180 View Port，配套字段 `Offset Pos Left`/`Offset Pos Right`/`Offset Rot Left`/`Offset Rot Right`/`Overlap Factor`。
- 画质相关 Inspector 取值：`Supersampling Mode` = None / Normal / Quality；`Supersampling Enhance Mode` = None / Fixed Foveated；`Sharpening Mode` = None / Normal / Quality；`Sharpening Enhance Mode` = None / Fixed Foveated / Self Adaptive / Both；`Power Setting` = HIGH_QUALITY / BALANCED / BATTERY_SAVING。
- 空间音频 Inspector 取值：`Source Attenuation Mode` = None / Fixed / Inverse Square / Custom（**Custom 请勿使用**）；`Output Method` = On Audio Filter Read / Pico Audio Router；`Spatializer Api Impl` = Unity / Wwise。

### 菜单路径

- `Edit > Project Settings > XR Plug-in Management > PICO > Android Settings`：Application SpaceWarp、Optimize Buffer Discards (Vulkan)、Stereo Rendering Mode、Display Refresh Rates、System Splash Screen。
- `Edit > Project Settings > Player > Other Settings > Rendering > Graphics API`：添加 Vulkan 并置顶。
- `Assets > Create > Rendering > Universal Render Pipeline > Pipeline Asset (Forward Renderer)`；`Edit > Render Pipeline > Universal Render Pipeline > Upgrade Project Materials to UniversalRP Materials`。
- `Window > Analysis > Profiler` / `Window > Analysis > Frame Debugger` / `Window > TextMeshPro > Font Asset Creator`。

## DO NOT

| 错误写法 / 做法 | 正确写法 / 做法 |
| --- | --- |
| `PXR_FoveationRendering.SetFoveationLevel(FoveationLevel.High)` 只传一个参数 | 必须传 `isETFR`：`SetFoveationLevel(FoveationLevel.High, true/false)` |
| 直接从 FFR 一次调用切到 ETFR | 先 `SetFoveationLevel(FoveationLevel.None, false)` 再 `SetFoveationLevel(level, true)`；失败时补调一次 |
| 同时开 FFR 与 ETFR 想要"叠加效果" | 两者互斥，只能选一个 |
| URP 项目开 FFR 但保留 Post-Processing / HDR / 用 Intermediate Texture 的 Renderer Feature | 全部禁用；必要时改 `ScriptableRenderer.GetCameraClearFlag` 返回 `ClearFlag.All` |
| 同时勾 PXR_Manager 的 `Super Resolution` 和 `Sharpening Mode` 期望叠加 | 二选一（同开只生效超分）；编辑器里锐化不可选时先取消 Super Resolution |
| 给合成层开 PXR_Manager 的 Super Resolution，或期望 Eye Buffer 超分对合成层生效 | PXR_Manager 的超分只作用 Eye Buffer；合成层的超采样/锐化/超分要在 PXR_Composition Layer 上单独设，且同层三选一 |
| 同时启用 AppSW 与 Optimize Buffer Discards | 二选一，否则画面撕裂 |
| 用 `PXR_Manager.SetSpaceWarp(true)` 当静态方法调用 | `PXR_Manager.Instance.SetSpaceWarp(true)` |
| 开 AppSW 却用 OpenGLES 或 Multi-pass | 必须 Vulkan 置顶 + `Stereo Rendering Mode = Multiview` |
| 每帧改 `XRSettings.eyeTextureResolutionScale` 做动态缩放 | 用 `XRSettings.renderViewportScale`（不重分配眼部纹理）；URP 场景改 `renderScale` |
| 在相机渲染过程中改 `renderViewportScale`，或传入 >1.0 的值 | 在 Update 中改（下一帧生效），取值 0.0–1.0 |
| 默认所有 PICO 4 都能开 ETFR | 仅 Neo3 Pro Eye / PICO 4 Pro / PICO 4 Enterprise |
| Underlay 层拖进场景就以为会显示 | 需 `PXR_UnderlayHole`（或自写 shader）在 Eye Buffer 挖洞；URP 下还必须禁用 HDR |
| Cylinder 层把相机放在圆柱内切球外 | 相机必须位于内切球内，靠近球面时合成层会不显示 |
| Blurred Quad 层配 Dynamic / Static Texture | 目前仅支持 `External Surface` |
| 一个场景堆 8 个以上合成层，或对 3 个以上图层开超分 | 上限 7 层、建议 ≤4；超分图层过多会撕裂 |
| 开 Late Latching 后照常使用 Overlay/Underlay 且期望不抖 | 已知冲突：Late Latching + 合成层会导致合成层抖动，需取舍 |
| 空间音频里把 Audio Source 的 `Spatial Blend` 设成 1 期望"更空间化" | 建议保持 0；设 1 表示放弃 PICO 空间化、退回 Unity 自带多普勒（这是省 CPU 的降级技巧） |
| 用 Text 组件渲染 VR 文字 | 用 TextMeshPro（SDF 渲染，任意距离清晰） |
| 只开 Mipmap 不开 Trilinear | 两者配套，否则 Mip 等级切换在 VR 里可明显感知 |

## 补充锚点（低频但易错）

- 启动画面（System Splash Screen，5.5.0+）：图片必须 **PNG 且分辨率 ≤ 1024×1024**，暂不支持半透明；初始化加载画面建议控制在 5 秒以内。[splash-screen](https://developer-cn.picoxr.com/document/unity/splash-screen/)
- 焦点感知：只提供事件不提供接口；不适配时按手柄 Home 键可能出现 4 个手柄模型重叠。[focus-awareness](https://developer-cn.picoxr.com/document/unity/focus-awareness/)
- Equirect 层：`Radius` 设为 0 或 `1.0f/0.0f`（正无限大）表示无限大半径，效果如天空盒；此时 Destination Rects 的 X 无用，W 映射到中心角并关于 (0,0) 对称。Overlay + 自定义 Rects 时 X/Y ∈ [0,1)、W/H ∈ (0,1]。[overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/)
- 沉浸式场景参考量级（官方"春"场景）：三角面 11 万、同屏面数 5 万、贴图 8192×2 astc 10×10、包体 23MB，映射图以 unlit 材质赋予。[create-immersive-scenes](https://developer-cn.picoxr.com/document/unity/create-immersive-scenes/)
- 电量约束：按 PICO 商店审核标准，应用需能在满电设备上运行至少 45 分钟而不触发低电量警告。[modify-eye-texture-resolution](https://developer-cn.picoxr.com/document/unity/modify-eye-texture-resolution/)
