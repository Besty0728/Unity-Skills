# PICO Unity SDK - Rendering and Performance (v3.4.0)

> Anchored to the PICO official documentation for v3.4.0 (crawled 2026-08-13). Every rule can be checked against its source URL.

## When to load this document

- Configuring or debugging foveated rendering (FFR / ETFR / Subsampling), or when it is "enabled but has no effect".
- Using composition layers (Composition Layer / PXR_Composition Layer / PXR_OverLay) for UI, video, 360 panoramas or spatial images.
- Trading quality against power: MSAA, supersampling, sharpening, super resolution, adaptive resolution, renderViewportScale, Eye Buffer resolution.
- Enabling low-latency or power-saving features: Application SpaceWarp, Late Latching, Multiview, Optimize Buffer Discards.
- URP problems on PICO (FFR not working, Underlay or VST not showing, black Eye Buffer, frame drops, crashes).
- Integrating the PICO spatial audio renderer (free field, environmental acoustics, Ambisonics, MR spatial audio).

## Key Rules

| # | Rule | Source |
| --- | --- | --- |
| 1 | ETFR and FFR are **mutually exclusive**. An app can enable only one. ETFR is supported only on devices with an eye-tracking camera: PICO Neo3 Pro Eye, PICO 4 Pro, PICO 4 Enterprise, on system 5.7.0 or later. | [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/) |
| 2 | Switching foveation mode takes **two steps**. First call `SetFoveationLevel(FoveationLevel.None, current isETFR)` to turn it off, then set the target level with the target `isETFR`. The second call of an FFR-to-ETFR switch may fail and need a third call. | [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/) |
| 3 | URP + FFR is disabled by default. URP introduces an Intermediate Texture, so the frame renders to that texture instead of the Eye Buffer. Disable Post-Processing, HDR, and any Renderer Feature that uses the Intermediate Texture. From URP 10.10.1, choosing Skybox as Clear Flags drops the Color Attachment Invalidate. Comment out the `CameraClearFlags.Skybox` branch in `ScriptableRenderer.GetCameraClearFlag` so it returns `ClearFlag.All`. The Built-in pipeline also needs post-processing disabled. | [FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) |
| 4 | Subsampling (downsampling, available since SDK 2.1.5) is **not yet supported** when the OpenGLES graphics API, Gamma color space and FFR are all used together. | [FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) |
| 5 | Super resolution and sharpening **cannot both be on for the same Eye Buffer**. If both are on, the SDK uses only super resolution. Neither can be used together with Subsampling. Super resolution applies only to the Eye Buffer and **does not support composition layers**. Requirements for both: Neo3 / 4 / 4 Ultra series, system 5.8.0 or later. | [super-resolution](https://developer-cn.picoxr.com/document/unity/super-resolution/) / [sharpening](https://developer-cn.picoxr.com/document/unity/sharpening/) |
| 6 | For **the same composition layer**, supersampling, sharpening and super resolution are one-of. If more than one is set dynamically, only supersampling takes effect. The Blurred Quad layer does not support the Enhance Mode parameter. | [layer-ss-sharp-sr](https://developer-cn.picoxr.com/document/unity/enable-supersampling-sharpening-and-super-resolution-for-compositor-layers/) |
| 7 | A scene supports at most 7 VR composition layers, and **at most 4 is recommended**. Nearer objects must occlude farther ones, or slight wobble can occur. Enabling super resolution on more than 3 layers can overload the compositor GPU and cause tearing. | [overview](https://developer-cn.picoxr.com/document/unity/compositor-layer-overview/) / [super-resolution](https://developer-cn.picoxr.com/document/unity/super-resolution/) |
| 8 | A URP project using an Underlay layer must **disable HDR**, or the Underlay does not take effect. The Underlay depends on the render target alpha channel and also needs a hole cut in the Eye Buffer. Use `PXR_UnderlayHole` under `Packages/PICO Integration/Assets/Resources/Shader`, or write your own shader. | [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) |
| 9 | A smaller composition layer `Depth` value is **closer to the Eye Buffer** (schematic: `[Camera](Overlay)2/1/0[EyeBuffer]0/1/2(Underlay)`). World-Locked is the default. For Head-Locked, make the object carrying the composition layer script a child of Main Camera. | [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) / [general-procedure](https://developer-cn.picoxr.com/document/unity/general-procedure-for-using-compositor-layers/) |
| 10 | AppSW prerequisites are all required: Neo3 / 4 / 4 Ultra series, system 5.4.0 or later, Unity 2021 LTS or later (2021 recommended; 2022/2023 may be incompatible), Stereo Rendering Mode = **Multiview**, **Vulkan** as the first graphics API, and the URP / Core RP / Shader Graph packages from the `AppSpacewarpForUnity` branch of the `Pico-Developer/Unity-Graphics` repository, with dynamic-object shaders replaced. Unity 6 and later also need URP 17.0.3+ with Render Graph in **Compatibility Mode (Render Graph Disabled)**. | [AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/) |
| 11 | AppSW and Optimize Buffer Discards **cannot be on together**. AppSW needs the depth buffer, and buffer discards throw away depth content, which causes tearing. AppSW together with content protection causes judder and ghosting. | [AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/) |
| 12 | Late Latching requires: Neo3 / 4 / 4 Ultra series, system 5.6.0 or later. **Multiview must be enabled first**, and Vulkan must be the first graphics API. After ticking `Use Late Latching` on PXR_Manager, the `PXR_Late Latching` script is attached to Main Camera automatically. Known issue: using it with composition layers (Overlay/Underlay) causes layer judder. Debug mode supports only Development Build, requires SDK 2.2.0+ and Unity 2021.3.19f or later, and **Unity 2022 is not yet supported**. | [late-latching](https://developer-cn.picoxr.com/document/unity/late-latching/) |
| 13 | Adaptive resolution requires system 5.7.0 or later. `Min/Max Adaptive Resolution Scale` takes values from 0.7 to 1.3 (defaults 0.7 and 1.26). The base resolution is 1504 x 1504, and the actual resolution = 1504 x Scale. Known issue: URP's `pipelineAsset.renderScale` **overrides** the maximum adaptive resolution scale. | [adaptive-resolution](https://developer-cn.picoxr.com/document/unity/adaptive-resolution/) |
| 14 | `XR.XRSettings.renderViewportScale` defaults to 1.0 and takes values from 0.0 to 1.0. It can change at runtime **without reallocating the eye textures**, so it is the preferred way to adjust rendered pixels dynamically. Changing it while the camera is rendering is ignored by the system and logs an error. When changed during the Gameplay update, it takes effect on the next frame. | [render-viewport-scaling](https://developer-cn.picoxr.com/document/unity/render-viewport-scaling/) |
| 15 | `XRSettings.eyeTextureResolutionScale` defaults to 1.0 and takes values from 0.8 to 2.0. **Going above 1.5 is not recommended.** Setting it above 2.0 has no effect and falls back to the default. If it exceeds the device limit, the device maximum is used. Changing the Eye Buffer always reallocates the eye textures, which is expensive. In URP projects, use `((UniversalRenderPipelineAsset)GraphicsSettings.renderPipelineAsset).renderScale` instead. | [modify-eye-texture-resolution](https://developer-cn.picoxr.com/document/unity/modify-eye-texture-resolution/) |
| 16 | MSAA defaults to 4x through the `Use Recommended MSAA` tick on PXR_Manager (ticked by default). To set a custom multiplier, **untick it first**, then set Project Settings > Quality > Rendering > Anti Aliasing. When performance is tight, at least 2x is still recommended. | [anti-aliasing](https://developer-cn.picoxr.com/document/unity/anti-aliasing/) / [enhance-image-quality](https://developer-cn.picoxr.com/document/unity/enhance-image-quality/) |
| 17 | URP known-issue checklist (verify item by item before configuring): with Unity 2021+ and URP, setting MSAA drops frames. Vulkan has lower frame rate and higher memory and GPU usage than OpenGLES. With Vulkan + URP + HDR together, the Underlay layer and the VST layer do not display. Enabling SSAO on the URP Renderer causes low frame rate and high cost. On Unity 2022+ with OpenGLES + MultiView, when an unused URP is introduced, remove the URP Package and re-add the scene lights, or it may crash. On Unity 6 + URP + OpenGL + Multi-pass + MSAA (not Disabled), Eye Buffer content cannot render. | [URP](https://developer-cn.picoxr.com/document/unity/universal-render-pipeline/) |
| 18 | Using Post Processing after enabling Multiview (formerly single-pass stereo rendering) costs performance and reduces frame rate. Multiview halves draw calls and occlusion culling work. | [multiview](https://developer-cn.picoxr.com/document/unity/multiview-rendering/) |
| 19 | Performance targets (PICO Neo 3 series): at least 72 FPS, with triangle count kept **under 1 million**. The default screen refresh rate is 72 Hz. Options are Default / 72 / 90 (recommended) / 120. Optimize Buffer Discards requires Vulkan and system 5.3.0 or later. | [performance-target](https://developer-cn.picoxr.com/document/unity/performance-target/) / [display-refresh-rate](https://developer-cn.picoxr.com/document/unity/display-refresh-rate/) / [buffer-discards](https://developer-cn.picoxr.com/document/unity/optimize-buffer-discards/) |
| 20 | Spatial audio requires Neo3 / 4 / 4 Ultra series and system 5.12.0 or later. The official validation covers only Unity 2021.1.9f1c1 and 2020.3.21f1 LTS. With environmental acoustic simulation, **the number of sound sources that play at the same time with Doppler enabled must stay within 20**. MR spatial audio (virtual sound interacting with the real-space mesh) is supported **only on PICO 4 Ultra series**. | [spatial-audio](https://developer-cn.picoxr.com/document/unity/spatial-audio/) |

## Workflows

### A. Enable foveated rendering (FFR or ETFR)

1. Hierarchy > **+ > XR > XR Origin (VR)** to add XR Origin. Select it, then at the bottom of the Inspector click **Add Component** and search for and add **PXR_Manager**.
2. In the **PXR_Manager (Script)** panel, set `Foveated Rendering Mode` to `Fixed Foveated Rendering` or `Eye Tracking Foveation Rendering`.
3. Set `Foveated Rendering Level`: `None` / `Low` / `Med` / `High` / `Top High` (High and Top High currently look the same). Choosing a level other than None shows the `Subsampling` checkbox.
4. FFR recommends ticking `Subsampling`. For ETFR it is optional. Downsampling removes visual artifacts in the low-resolution areas at the edge of the view, making transitions smoother and reducing dizziness.
5. Switch at runtime with `PXR_FoveationRendering.SetFoveationLevel(level, isETFR)`. Switching between modes must first set `FoveationLevel.None` (see rule 2).
6. When ETFR is enabled, the SDK writes the `picovr.software.eye_tracking` meta-data and the `com.picovr.permission.EYE_TRACKING` permission into AndroidManifest.xml automatically. Do not change them unless necessary. With a custom Manifest, add them yourself.
7. Verify with the log line `Pxr_SetEyeFoveationLevelEnable`. `bSupported` shows whether the device supports eye tracking. `level` maps -1 / 0 / 1 / 2 / 3 to off / Low / Med / High / Top High. `result = 0` means success.
   Source: [FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) / [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/)

### B. Build an Overlay Quad composition layer

1. Add XR Origin to the scene and attach **PXR_Manager**. Set the Tag of Main Camera under XR Origin to **MainCamera**, and delete the Main Camera that came with the scene.
2. **GameObject > 3D Object** to create a visible 3D object (such as a Cube). Add the **PXR_Composition Layer** component via Add Component.
3. Basic parameters: `Type = Overlay`, `Shape = Quad`, `Depth = 0`.
4. For `Texture Type`, choose `Dynamic Texture` (updated each frame, such as a RenderTexture) or `Static Texture` (a still image). Then assign the textures for the left and right eyes in `Texture`. **The left and right eyes must use the same texture, with the same width and height.** Assign them separately only when you need a 3D effect.
5. Optional: tick `Texture Rects` and set `Source Rects` / `Destination Rects`. Tick `Layer Blend` and set Src/Dst Color and Alpha. Tick `Override Color Scale` and set `Scale` / `Offset` (XYZW map to RGBA; final color = original color x Scale + Offset).
6. Head-Locked: create the 3D object as a child of **XR Origin > Camera Offset > Main Camera**. Other parameters are the same as above.
7. Video and panorama variants: `External Surface` uses an Android player video texture (better quality, can tick `DRM` and choose a `3D Surface Type`). `Shape = Equirect` for 360 or 180 panoramas. `Shape = Eac` uses `Model Type` to choose one of four EAC modes. `Shape = Blurred Quad` for spatial images or video (**currently supports External Surface only**).
   Source: [general-procedure](https://developer-cn.picoxr.com/document/unity/general-procedure-for-using-compositor-layers/) / [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) / [EAC](https://developer-cn.picoxr.com/document/unity/use-the-eac-layer/) / [blurred-quad](https://developer-cn.picoxr.com/document/unity/use-the-blurred-quad-layer/)

### C. Enable Application SpaceWarp (seven steps)

1. **Edit > Project Settings > XR Plug-in Management > PICO**. Set `Stereo Rendering Mode = Multiview`.
2. Clone `Pico-Developer/Unity-Graphics`, then `git checkout "AppSpacewarpForUnity"`. Use **Window > Package Manager > + > Add package from disk** to import the package.json files for Core RP Library, Shader Graph and Universal RP.
3. Enable URP: create a URP Asset, add it to Graphics settings, disable HDR, and upgrade materials.
4. Replace the shaders of dynamic objects in the scene with URP shaders that generate motion vectors (see `com.unity.render-pipelines.universal/Shaders/Lit.shader` for reference).
5. **Edit > Project Settings > Player > Other Settings > Rendering**. Add **Vulkan** to Graphics APIs and move it to the first position.
6. **XR Plug-in Management > PICO > Android Settings**, tick `Application SpaceWarp`. Attach PXR_Manager to XR Origin in the scene. Create a script that calls `PXR_Manager.Instance.SetSpaceWarp(true)` in `Start`.
7. Verify with `PxrMetric` log lines such as `FPS=36/72,MTP=40.60ms,AppSW=on,...`. Troubleshooting: straight lines or grids on simple backgrounds may warp, and fast-rotating objects may show artifacts around them (reduce the rotation speed).
   Source: [AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/)

### D. Quality and power tuning order

1. **Set the baseline first**: keep MSAA at the recommended 4x, enable Multiview, and confirm the refresh-rate option (72 / 90 / 120) and target FPS.
2. **Then adjust resolution**: if there is performance headroom, raise `eyeTextureResolutionScale` (up to 1.5) or URP `renderScale`. If performance is tight, enable adaptive resolution (`Adaptive Resolution` + Min/Max Scale + `Power Setting`), or reduce dynamically at runtime with `renderViewportScale`.
3. **Finally add post-processing enhancement**: on the Eye Buffer side, tick `Super Resolution` or choose a `Sharpening Mode` on PXR_Manager. Choose one. If sharpening cannot be selected, untick Super Resolution first. At runtime, use `PXR_Plugin.Render.UPxr_SetSuperResolutionOrSharpening`. Sharpening best practice: enable Fixed Foveated and Self Adaptive enhancement together (that is, `Both`). This clearly reduces power draw while keeping visual quality.
4. **Set composition layers individually**: on each PXR_Composition Layer, choose `Supersampling Mode` / `Sharpening Mode` / `Super Resolution` per layer (one-of per layer).
5. **Textures and text**: for all textures, enable Generate Mip Maps and set `Filter Mode = Trilinear`. For surfaces seen at oblique angles, such as walls and floors, also set `Aniso Level`. Use TextMeshPro for text, not the Text component.
6. **Measure**: Window > Analysis > **Profiler** (Ctrl+7 / Command+7), Window > Analysis > **Frame Debugger** to inspect draw calls, and in the Scene view dropdown > Miscellaneous > **Overdraw** for the overdraw heat map.
   Source: [enhance-image-quality](https://developer-cn.picoxr.com/document/unity/enhance-image-quality/) / [sharpening](https://developer-cn.picoxr.com/document/unity/sharpening/) / [adaptive-resolution](https://developer-cn.picoxr.com/document/unity/adaptive-resolution/) / [profiler](https://developer-cn.picoxr.com/document/unity/performance-monitoring-and-analysis/) / [overdraw](https://developer-cn.picoxr.com/document/unity/view-overdraw/)

### E. Integrate spatial audio

1. Create a GameObject and add **PXR_Audio_Spatializer_Context**. Set `Spatializer Api Impl` (Unity or Wwise) and `Rendering Quality`.
2. Add **PXR_Audio_Spatializer_Audio Source** to the sound source object. It adds an Audio Source component automatically. **Keep `Spatial Blend` at 0.** Set AudioClip, Play On Awake and Loop.
3. Add **PXR_Audio_Spatializer_Audio Listener** to the object that carries the Audio Listener (usually Main Camera). Choose `Output Method`: `On Audio Filter Read` (standard) or `Pico Audio Router` (routes through an Audio Mixer for independent volume and post-processing).
4. Environmental acoustics: add **PXR_Audio_Spatializer_SceneGeometry** to models that take part in the simulation. It brings in **PXR_Audio_Spatializer_SceneMaterial** automatically. Split the mesh by material and set `Material Preset` / Absorption / Scattering / Transmission for each part. For static meshes, bake when needed.
5. Ambisonics: in Project Settings > Audio, set `Ambisonic Decoder Plugin` to **PICO Ambisonic Decoder**. Create an Audio Mixer. On the Master channel, Add Effect > **PICO Ambisonic Renderer**. Add **PXR_Audio_Spatializer_Ambisonic Source** to the object and point the Audio Source's Output to this Mixer. The audio must be first-order Ambix format and tick `Ambisonic`.
6. MR spatial audio (PICO 4 Ultra series only): on PXR_Manager, tick `Spatial Mesh` and choose the LOD. Attach **PXR_Spatial Mesh Manager** and assign a Mesh Prefab that has a MeshFilter. Attach **PXR_Audio_Spatializer_MR Scene Geometry Manager**.
7. Editor setting: **Edit > Preferences > General**, change `Script Changes While Playing` to `Recompile After Finished Player` or `Stop Playing And Compile`. Otherwise, compiling while audio plays can crash the editor.
   Source: [spatial-audio](https://developer-cn.picoxr.com/document/unity/spatial-audio/)

## Core API Anchors

### Component and script names (the official docs use several spellings for the same thing)

| Correct name | Notes |
| --- | --- |
| `PXR_Manager` | Master script attached to XR Origin. Holds Foveated Rendering Mode/Level, Subsampling, Super Resolution, Sharpening Mode, Adaptive Resolution, Use Late Latching, Use Recommended MSAA, Open Screen Fade, Use Premultiplied Alpha, Layer Blend, Spatial Mesh, etc. |
| `PXR_Composition Layer` / `PXR_Over Lay` / `PXR_OverLay.cs` | **Three spellings of the same composition layer component** in the docs. The panel and Add Component name is PXR_Composition Layer (older docs write PXR_Over Lay). The source file is PXR_OverLay.cs. |
| `PXR_UnderlayHole` | Script that cuts a hole for the Underlay. Located in `Packages/PICO Integration/Assets/Resources/Shader` |
| `PXR_Late Latching` | Attached to Main Camera automatically when Use Late Latching is ticked |
| `PXR_Screen Fade` | Attached to Main Camera automatically when Open Screen Fade is ticked. Parameters: `Gradient Time` (default 5 seconds) and `Fade Color` (default black) |
| `PXR_Spatial Mesh Manager` | MR spatial audio and spatial mesh. Requires a `Mesh Prefab` |
| `PXR_Audio_Spatializer_Context` / `_Audio Source` / `_Ambisonic Source` / `_Audio Listener` | Base spatial audio scripts (`Packages/PICO Integration/SpatialAudio/Runtime`) |
| `PXR_Audio_Spatializer_API` / `_Types` | Spatial audio helper scripts |
| `PXR_Audio_Spatializer_Scene Geometry` / `_Scene Material` | Environmental acoustics scripts (shown as SceneGeometry / SceneMaterial in the panel) |
| `PXR_Audio_Spatializer_MR Scene Geometry Manager` | MR spatial audio manager |

### Runtime methods

```csharp
PXR_FoveationRendering.SetFoveationLevel(FoveationLevel level, bool isETFR);  // switch mode or level
PXR_FoveationRendering.GetFoveationLevel();                                   // get level
PXR_FoveationRendering.SetFoveationParameters(...);                           // marked Deprecated in the FFR docs
PXR_Manager.Instance.SetSpaceWarp(bool);                                      // AppSW toggle (instance method)
PXR_Plugin.Render.UPxr_SetSuperResolutionOrSharpening(SuperResolutionOrSharpeningType type);
UnityEngine.XR.XRSettings.renderViewportScale;        // 0.0 to 1.0, default 1.0
UnityEngine.XR.XRSettings.eyeTextureResolutionScale;  // 0.8 to 2.0, default 1.0
((UniversalRenderPipelineAsset)GraphicsSettings.renderPipelineAsset).renderScale;  // URP branch
```

Events (focus awareness is event-only, with no interface): `PXR_Plugin.System.FocusStateLost`, `PXR_Plugin.System.FocusStateAcquired`.

Spatial audio utility functions: `Resume`, `SetGainDB` / `GetGainDB`, `SetReflectionGainDB` / `GetReflectionGainDB`, `SetSize` / `GetSize`, `SetDopplerStatus` / `GetDopplerStatus`, `GetAttenuationMode`, `SetMinAttenuationRange` / `GetMinAttenuationRange`, `SetMaxAttenuationRange` / `GetMaxAttenuationRange`, `SetDirectivity(alpha, order)`. On the Context side: `SetRenderingQuality(PXR_Audio.Spatializer.RenderingMode quality)`.

### Enums and fields

- `FoveationLevel`: the only members used in the docs' code samples are `FoveationLevel.None` and `FoveationLevel.High`. Panel levels are None / Low / Med / High / Top High, which map to logs -1 / 0 / 1 / 2 / 3.
- `SuperResolutionOrSharpeningType`: `None`, `SuperResolution`, `NormalSharpening`, `NormalSharpeningAndFixedFoveated`, `NormalSharpeningAndSelfAdaptive`, `NormalSharpeningAndFixedFoveatedAndSelfAdaptive`, `QualitySharpening`, `QualitySharpeningAndFixedFoveated`, `QualitySharpeningAndSelfAdaptive`, `QualitySharpeningAndFixedFoveatedAndSelfAdaptive`. Setting any one value automatically sets the others to None.
- `HDRFlags`: `None`, `HdrPQ`, `HdrHLG` (HDR video is supported only for External Surface textures).
- `BlurredQuadMode`: code field `blurredQuadMode = BlurredQuadMode.SmallWindow`. Panel values are Small Window / Immersion. Other fields: `blurredQuadScale` (0.5), `blurredQuadShift` (0.01), `blurredQuadFOV` (70), `blurredQuadIPD` (0.064). Editing the values directly in PXR_OverLay.cs takes effect every frame.
- Composition layer Inspector values: `Type` = Overlay / Underlay. `Shape` = Quad / Cylinder / Equirect / Cubemap / Equi-Angular Cubemap (EAC) / Blurred Quad. `Texture Type` = External Surface / Dynamic Texture / Static Texture. `Source Rects` = Mono Scopic / Stereo Scopic / Custom. `Destination Rects` = Default / Custom. `3D Surface Type` = Single / Left Right / Top Bottom. EAC `Model Type` = EAC 360 / EAC 360 View Port / EAC 180 / EAC 180 View Port, with companion fields `Offset Pos Left` / `Offset Pos Right` / `Offset Rot Left` / `Offset Rot Right` / `Overlap Factor`.
- Quality-related Inspector values: `Supersampling Mode` = None / Normal / Quality. `Supersampling Enhance Mode` = None / Fixed Foveated. `Sharpening Mode` = None / Normal / Quality. `Sharpening Enhance Mode` = None / Fixed Foveated / Self Adaptive / Both. `Power Setting` = HIGH_QUALITY / BALANCED / BATTERY_SAVING.
- Spatial audio Inspector values: `Source Attenuation Mode` = None / Fixed / Inverse Square / Custom (**do not use Custom**). `Output Method` = On Audio Filter Read / Pico Audio Router. `Spatializer Api Impl` = Unity / Wwise.

### Menu paths

- `Edit > Project Settings > XR Plug-in Management > PICO > Android Settings`: Application SpaceWarp, Optimize Buffer Discards (Vulkan), Stereo Rendering Mode, Display Refresh Rates, System Splash Screen.
- `Edit > Project Settings > Player > Other Settings > Rendering > Graphics API`: add Vulkan and move it to the top.
- `Assets > Create > Rendering > Universal Render Pipeline > Pipeline Asset (Forward Renderer)`. `Edit > Render Pipeline > Universal Render Pipeline > Upgrade Project Materials to UniversalRP Materials`.
- `Window > Analysis > Profiler` / `Window > Analysis > Frame Debugger` / `Window > TextMeshPro > Font Asset Creator`.

## DO NOT

| Wrong | Right |
| --- | --- |
| `PXR_FoveationRendering.SetFoveationLevel(FoveationLevel.High)` with only one argument | Always pass `isETFR`: `SetFoveationLevel(FoveationLevel.High, true/false)` |
| Switch from FFR to ETFR in one call | First `SetFoveationLevel(FoveationLevel.None, false)`, then `SetFoveationLevel(level, true)`. If it fails, call again once |
| Enable FFR and ETFR together to "stack the effects" | They are mutually exclusive. Choose one |
| URP project with FFR enabled but keeping Post-Processing, HDR, or a Renderer Feature that uses the Intermediate Texture | Disable all of them. If needed, change `ScriptableRenderer.GetCameraClearFlag` to return `ClearFlag.All` |
| Tick both PXR_Manager `Super Resolution` and `Sharpening Mode` expecting them to stack | Choose one (if both are on, only super resolution applies). In the editor, untick Super Resolution first if sharpening cannot be selected |
| Enable PXR_Manager Super Resolution on composition layers, or expect Eye Buffer super resolution to apply to layers | PXR_Manager super resolution applies only to the Eye Buffer. Set supersampling, sharpening and super resolution for composition layers individually on PXR_Composition Layer, one-of per layer |
| Enable AppSW and Optimize Buffer Discards together | Choose one, or the screen tears |
| Call `PXR_Manager.SetSpaceWarp(true)` as a static method | `PXR_Manager.Instance.SetSpaceWarp(true)` |
| Enable AppSW with OpenGLES or Multi-pass | Vulkan first, plus `Stereo Rendering Mode = Multiview` |
| Change `XRSettings.eyeTextureResolutionScale` every frame for dynamic scaling | Use `XRSettings.renderViewportScale` (does not reallocate eye textures). In URP scenes, change `renderScale` |
| Change `renderViewportScale` while the camera is rendering, or pass a value above 1.0 | Change it in Update (takes effect next frame), with values from 0.0 to 1.0 |
| Assume ETFR works on every PICO 4 | Only Neo3 Pro Eye / PICO 4 Pro / PICO 4 Enterprise |
| Drop an Underlay layer into the scene and expect it to show | It needs `PXR_UnderlayHole` (or your own shader) to cut a hole in the Eye Buffer. Under URP, HDR must also be disabled |
| Put the camera outside the inscribed sphere of a Cylinder layer | The camera must be inside the inscribed sphere. Near the sphere surface, the composition layer does not display |
| Use Dynamic or Static Texture with a Blurred Quad layer | Currently only `External Surface` is supported |
| Stack more than 8 composition layers in one scene, or enable super resolution on more than 3 layers | The limit is 7 layers, and 4 or fewer is recommended. Too many super-resolution layers cause tearing |
| Keep using Overlay/Underlay with Late Latching and expect no judder | Known conflict: Late Latching with composition layers causes layer judder. Make a trade-off |
| Set the Audio Source `Spatial Blend` to 1 expecting "more spatial" | Keep it at 0. Setting it to 1 gives up PICO spatialization and falls back to Unity's built-in Doppler. This is a CPU-saving fallback |
| Render VR text with the Text component | Use TextMeshPro (SDF rendering, clear at any distance) |
| Enable Mipmap without Trilinear | They go together. Otherwise, mip level switches are clearly visible in VR |

## Supplementary Anchors (low frequency but easy to get wrong)

- System Splash Screen (5.5.0+): the image must be **PNG with resolution at most 1024 x 1024**. Translucency is not yet supported. Keep the initialization loading screen under 5 seconds. [splash-screen](https://developer-cn.picoxr.com/document/unity/splash-screen/)
- Focus awareness: only events are provided, with no interface. If the app does not adapt, pressing the controller Home button may show four overlapping controller models. [focus-awareness](https://developer-cn.picoxr.com/document/unity/focus-awareness/)
- Equirect layer: setting `Radius` to 0 or to `1.0f/0.0f` (positive infinity) means infinite radius, which looks like a skybox. In that case the X of Destination Rects has no effect, and W maps to the center angle and is symmetric about (0,0). With Overlay and custom Rects, X/Y is in [0,1) and W/H is in (0,1]. [overlay-params](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/)
- Reference magnitudes for an immersive scene (the official "Spring" scene): 110,000 triangles, 50,000 faces on screen, textures 8192 x 2 astc 10x10, package size 23 MB. The mapping images use unlit materials. [create-immersive-scenes](https://developer-cn.picoxr.com/document/unity/create-immersive-scenes/)
- Battery constraint: under PICO Store review standards, the app must be able to run for at least 45 minutes on a fully charged device without a low-battery warning. [modify-eye-texture-resolution](https://developer-cn.picoxr.com/document/unity/modify-eye-texture-resolution/)
