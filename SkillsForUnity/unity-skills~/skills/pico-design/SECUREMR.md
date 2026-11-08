# PICO Unity SDK - SecureMR Privacy-Safe Mixed Reality (v3.4.0)

> Anchored to the PICO official documentation for v3.4.0 (crawled 2026-08-13). Every rule can be checked against its source URL.
> This area is scarce in AI training corpora. Every class name, operand name and enum value below comes from the official documentation. Treat any API not listed here as nonexistent, and check the SDK source before writing code.

## When to load this document

- You need MR effects on PICO 4 Ultra **without camera permission** (VST image processing, ambient color sampling, target detection overlays).
- The code uses the `Unity.XR.PXR.SecureMR` namespace or SecureMR concepts such as `Provider`, `Pipeline`, `Tensor` and `*Operator`.
- You need to convert a PyTorch, TensorFlow or ONNX model into a QNN binary and run it on the NPU through `RunModelInferenceOperator`.
- You need to render a glTF model on the SecureMR side (paste the VST image, draw text, drive animation, swap material textures).
- The task involves Dynamic Texture, Readback Tensor or JavaScript Operator, which are new in 3.2.0+.
- You are debugging `[INVALID PARAMETER]` or `[HANDLE NOT INITIALIZED]` errors logged by `Secure MR::Server`.

## Key Rules

| # | Rule | Source |
| --- | --- | --- |
| 1 | Hardware requirement: **PICO 4 Ultra series only**, with system **5.13.0 or later**. The official sample project uses Unity 6 and PICO Unity Integration SDK 3.2.0 | [overview](https://developer-cn.picoxr.com/document/unity/securemr-overview/) / [samples](https://developer-cn.picoxr.com/document/unity/securemr-samples/) |
| 2 | Enabling takes two steps. Tick **SecureMR** in the **PXR_Manager (Script)** panel, then configure video see-through per the Video See-through docs. The sample code also sets `PXR_Manager.EnableVideoSeeThrough = true` | [quickstart](https://developer-cn.picoxr.com/document/unity/securemr-quickstart/) / [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/) |
| 3 | Communication is strictly one-way. The app can send data, 3D assets, algorithms and execution commands. **Camera frames, depth maps and MR processing results are never sent back.** The SecureMR service keeps one overriding OpenXR session and renders pipeline results on a separate layer **below** the app layer | [architecture](https://developer-cn.picoxr.com/document/unity/securemr-architecture/) |
| 4 | Each app can have only **one undestroyed Provider session** at a time. When a session is destroyed, all resources created in it are reclaimed | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 5 | The width and height passed to `new Provider(width, height)` are the expected VST image size, and **they cannot change after creation**. To change the size, call `Destroy()` and then recreate. The default is 1024x1024. Match the AI model input size to avoid a resize | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 6 | A tensor is defined by four attributes: shape, channel, dataType and usage. **Channel is not counted as a dimension of shape** (OpenCV convention). A `[1024,1024,3]` single-channel tensor is not the same as a `[1024,1024]` three-channel tensor | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 7 | Usage values: `Matrix` (default), `Scalar`, `Timestamp`, `Color`, `Point`, `Slice`, plus the special `Gltf`. `Timestamp` must be a 4-channel `Int32` with shape `[1,]`. `Slice` must be a 2- or 3-channel integer type. With 2 channels it is `(START, END)`; with 3 it is `(START, END, SKIP)` | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 8 | A glTF tensor **can only be created as a global tensor** (`provider.CreateTensor<Gltf>(bytes)`), because render assets have a lifetime. `tensor.Destroy()` also **supports only global tensors** | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 9 | A global tensor **cannot bind directly to an operator** (a thread-safety constraint). Create a placeholder inside the pipeline with `CreateTensorReference<...>()` and bind it to the operator. At submission, create a mapping object with `pipeline.CreateTensorMapping()`, call `Set(placeholder, globalTensor)`, and pass it to `Execute*`. **The mapping is valid only for that submission** | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 10 | Operators in a pipeline **run in the order they were added**. Each submission makes the SecureMR service schedule the whole pipeline to run **once**, and it returns a run handle | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 11 | Scheduling: one pipeline does not run concurrently. Executions that read or write the same global tensor wait for each other. The `condition` hint **must be a global tensor**, and if it points to an all-zero tensor, that execution is cancelled. The SDK does not limit frequency but queues submissions, so if the previous run has not finished, the new one is queued. Throttle based on actual run time | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 12 | Global tensors synchronize with lock-free waiting, so a **slow producer pipeline slows all downstream pipelines**. To decouple, insert a "copier pipeline" that latches the latest value into a copy global tensor, and have downstream pipelines read the copy | [pipeline-synchronization](https://developer-cn.picoxr.com/document/unity/pipeline-synchronization/) |
| 13 | An operator **cannot be destroyed on its own**. It is destroyed only with the pipeline it belongs to | [use-different-operators](https://developer-cn.picoxr.com/document/unity/use-different-operators/) |
| 14 | The `left image` / `right image` result shapes of `RectifiedVstAccessOperator` must match the VST width and height set when the Provider was created. Otherwise, at runtime you get `Server Err: [INVALID PARAMETER] ... result N size (2464x3248) mismatches the config (256x256)` | [use-different-operators](https://developer-cn.picoxr.com/document/unity/use-different-operators/) / [troubleshooting](https://developer-cn.picoxr.com/document/unity/securemr-troubleshooting/) |
| 15 | `UpdateGltfOperator` takes effect only **after the glTF has started rendering through `SwitchGltfRenderStatusOperator`**. `SwitchGltfRenderStatusOperator` stops rendering when `world pose` is empty, and then `visible` is invisible whatever its value | [use-different-operators](https://developer-cn.picoxr.com/document/unity/use-different-operators/) |
| 16 | When `view locked` is non-zero, the glTF uses the OpenXR **view space** as its reference frame. In that case, do **not** use `CAMERA SPACE TO WORLD`, which uses the OpenXR local coordinate system as its reference | [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/) |
| 17 | A Dynamic Texture requires **two enums to match at the same time**: DataType in {`TensorDataType.DynamicTextureByte`, `TensorDataType.DynamicTextureFloat`} and Usage = `TensorUsage.DynamicTexture`. Only a `GlobalTensor` or `PipelinePlaceholder` can be declared as a Dynamic Texture | [use-dynamic-texture](https://developer-cn.picoxr.com/document/unity/use-dynamic-texture/) |
| 18 | After a Dynamic Texture is bound through `Operator Load_Texture`, **do not call `Operator Update_Texture`, and it is not allowed**. Update content only by writing directly to the bound tensor data. Only static textures created without Dynamic Texture need `Update_Texture` | [use-dynamic-texture](https://developer-cn.picoxr.com/document/unity/use-dynamic-texture/) |
| 19 | Readback permission is **session-level**. If **any pipeline** in the current SecureMR Framework Session uses a camera-type operator (such as `Rectified_VST_Access`), `android.permission.CAMERA` must already be granted, **whether or not it actually runs**. If it uses a spatial-data operator (such as `UV_to_Camera_Space`), `com.picovr.permission.SPATIAL_DATA` must already be granted | [use-the-readback-tensor](https://developer-cn.picoxr.com/document/unity/use-the-readback-tensor/) |
| 20 | To read back to CPU, use `tensor.CreateBufferAsync`. It is a cross-process copy, takes a while, and is **one-time**. After the tensor updates, copy again. Zero-copy GPU readback (Vulkan Image / OpenGL ES Image) **supports only dynamic-texture tensors**, and the result can be used only as a GPU sampler | [use-the-readback-tensor](https://developer-cn.picoxr.com/document/unity/use-the-readback-tensor/) |

## Workflows

### A. Minimum working setup: render a glTF model in the VST scene

Source: [quickstart](https://developer-cn.picoxr.com/document/unity/securemr-quickstart/) + [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/)

1. In the **PXR_Manager (Script)** panel, tick **SecureMR**. Configure see-through per the Video See-through docs.
2. In `Start()`, set `PXR_Manager.EnableVideoSeeThrough = true;`, then `provider = new Provider(image_width, image_height);`.
3. `pipeline = provider.CreatePipeline();` then `pipeline.CreateOperator<SwitchGltfRenderStatusOperator>()`.
4. Load the glTF asset from a `TextAsset`. On the global side, `provider.CreateTensor<Gltf>(tvGltf.bytes)`. On the pipeline side, `pipeline.CreateTensorReference<Gltf>()` creates the placeholder.
5. The pose is a 4x4 matrix: `pipeline.CreateTensor<float,Matrix>(1, new TensorShape(4,4))` then `poseMat.Reset(poseMatValue)`.
6. `SetOperand("gltf", placeholder)` and `SetOperand("world pose", poseMat)`.
7. Drive each frame (the sample uses `InvokeRepeating(nameof(RenderFrame), 0, 0.02f)`): `var m = pipeline.CreateTensorMapping(); m.Set(placeholder, gltfTensor); pipeline.Execute(m);`

### B. Paste the VST image onto a glTF material (two pipelines + a global tensor)

Source: [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/)

1. **Capture pipeline**: `RectifiedVstAccessOperator.SetResult("left image", rawRgb)`. `rawRgb` is a 3-channel `byte` `Matrix`. The sample uses shape `new TensorShape(image_height, image_width)`.
2. Crop with an affine transform: `GetAffineOperator` (three 2-channel `Point` values each for `src` and `dst`), then `ApplyAffineOperator` (`affine` + `src image` to `dst image`).
3. Write `dst image` into a **placeholder** `cropRgbWrite`. At submission, map it to the global tensor `cropRgbGlobal`.
4. **Render pipeline**: `LoadTextureOperator` takes `rgb image` = the read-side placeholder `cropRgbRead`, and `gltf` = the glTF placeholder. It outputs `SetResult("texture ID", gltfTextureIndex)`.
5. `UpdateGltfOperator` uses `SecureMRGltfOperatorAttribute.MaterialBaseColorTexture`, with `SetOperand("gltf" / "material ID" / "value")`. `value` is the texture ID tensor from the previous step.
6. `SwitchGltfRenderStatusOperator` starts rendering with `gltf` and `world pose`.
7. Execute each pipeline with its own `Execute(mapping)`. The single global tensor `cropRgbGlobal` links them (synchronization semantics in rules 11 and 12).

### C. Run a custom algorithm (QNN model)

Source: [profile-securemr-models](https://developer-cn.picoxr.com/document/unity/profile-securemr-models/) + [create-a-qnn-model-to-run-algorithms](https://developer-cn.picoxr.com/document/unity/create-a-qnn-model-to-run-algorithms/) + [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/)

1. Implement the algorithm in PyTorch, TensorFlow or ONNX. Use this route for logic that native SecureMR operators cannot express.
2. Install Docker. Use `./convert_model.sh --input /path/to/onnx_model` from SecureMRTools (optionally `--custom_io custom_io.yaml` to set the NCHW/NHWC layout) to convert the model into a QNN context binary.
3. Optional: `./run_docker_container.sh` then `./profile_model.sh -m model.serialized.bin -i 000000.raw` to get run times from `qnn-net-run`.
4. For a large model, place it in `StreamingAssets`, read it into a `byte[]` with `UnityWebRequest`, and then create the operator. The sample calls `LoadData()` in a coroutine before `CreatePipeline()`.
5. Describe each input and output node with `SecureMROperatorModelConfig { encodingType, nodeName, operatorIOName }`. `encodingType` is `SecureMRModelEncoding.Float32` or `.Int32`.
6. `new ModelOperatorConfiguration(inputConfigs, outputConfigs, fileBytes, SecureMRModelType.QnnContextBinary, "mnist")`, then `pipeline.CreateOperator<RunModelInferenceOperator>(config)`.
7. **The binding name is the `operatorIOName`** (sample: `SetOperand("input_1", ...)`, `SetResult("_538", ...)`, `SetResult("_539", ...)`). It is not a fixed `operand0` or `result`.
8. Build pre- and post-processing from CPU operators: `ConvertColorOperator` (RGB to GRAY), then `AssignmentOperator` (uint8 to float32), then `ArithmeticComposeOperator("{0} / 255.0")` for normalization, then the model.

### D. Debug tensors inside a pipeline

Source: [debug-tensors-in-a-pipeline](https://developer-cn.picoxr.com/document/unity/debug-tensors-in-a-pipeline/) + [troubleshooting](https://developer-cn.picoxr.com/document/unity/securemr-troubleshooting/)

1. The only direct way to read tensor contents is to render them. Connect any tensor to the `text` operand of `RenderTextOperator`. Non-UTF-8 scalars are printed as **raw values in matrix form**, with a 5x5 summary shown by default.
2. Configure with `new RenderTextOperatorConfiguration(SecureMRFontTypeface.SansSerif, "en-US", 1440, 960)`. Then set `start` (a 2-channel float `Point`, with XY as 0 to 1 relative values), `colors` (a 4-channel `byte` `Color` with shape `{2,}`, text color first, then background color), `texture ID`, `font size`, and `gltf`.
3. To show something conditionally, connect a boolean result tensor to `visible` on `SwitchGltfRenderStatusOperator`.
4. Check `Secure MR::Server` errors in logcat. `[INVALID PARAMETER]` usually means the shape or size does not match the config (see rule 14). `[HANDLE NOT INITIALIZED]: cannot find the local tensor with ID = ...` means a tensor was not registered with that pipeline (the underlying registration API is `xrCreateSecureMrPipelineTensorPICO`). The log gives the pipeline ID.

## Core API Anchors

### Namespaces and entry points

| Name | Notes |
| --- | --- |
| `Unity.XR.PXR` / `Unity.XR.PXR.SecureMR` | The sample imports both namespaces |
| `using Color = Unity.XR.PXR.SecureMR.Color;` | Explicit alias used in the official sample to avoid a clash with `UnityEngine.Color` |
| `PXR_Manager` | Tick **SecureMR** in the panel. Sample code uses `PXR_Manager.EnableVideoSeeThrough` |

### The three handle types and their methods

| Type | Members that appear in the docs |
| --- | --- |
| `Provider` | `new Provider(width, height)`, `CreatePipeline()`, `CreateTensor<Gltf>(byte[])`, `CreateTensor<T,Usage>(channel, TensorShape)`, `Destroy()` |
| `Pipeline` | `CreateOperator<T>()` / `CreateOperator<T>(config)`, `CreateTensor<T,Usage>(channel, TensorShape[, data])`, `CreateTensorReference<Gltf>()` / `CreateTensorReference<T,Usage>(channel, TensorShape)`, `CreateTensorMapping()`, `Execute(mapping)`, `ExecuteAfter(runId, mapping)`, `ExecuteConditional(runId, mapping)`, `Destroy()` |
| `Tensor` | `Reset(data)` (reuse or update content), `Destroy()` (global only), `CreateBufferAsync` (readback to CPU memory) |
| Object returned by `CreateTensorMapping()` (the official sample only uses `var` and **does not give a class name**. Do not invent one) | `Set(placeholder, globalTensor)`, passed as an optional argument to `Execute*` |
| `TensorShape` | `new TensorShape(4,4)` / `new TensorShape(30)` / `new TensorShape(int[])` |

### Operator class names (all from use-different-operators / use-cases)

- Arithmetic and logic: `ArithmeticComposeOperator`, `ElementwiseMinOperator`, `ElementwiseMaxOperator`, `ElementwiseMultiplyOperator`, `ElementwiseOrOperator`, `ElementwiseAndOperator`, `CustomizedCompareOperator`, `AllOperator`, `AnyOperator`, `InversionOperator`, `NormalizeOperator`
- Data movement and sorting: `AssignmentOperator`, `SortVectorOperator`, `SortMatrixOperator`, `ArgmaxOperator`
- Geometry and vision: `NmsOperator`, `SolvePnPOperator`, `GetAffineOperator`, `ApplyAffineOperator`, `ApplyAffinePointOperator`, `GetTransformMatrixOperator`, `ConvertColorOperator`, `UvTo3DInCameraSpaceOperator` (**code samples write it as `UVTo3DInCameraSpaceOperator`**), `CameraSpaceToWorldOperator`, `RectifiedVstAccessOperator`
- Rendering and glTF: `SwitchGltfRenderStatusOperator`, `UpdateGltfOperator`, `RenderTextOperator`, `LoadTextureOperator`
- Models and scripts: `RunModelInferenceOperator`, `JavascriptOperator`
- Documented by function only, **with no C# class name given**: SVD (`src` to `w`/`u`/`vt`), Norm (`operand0` to `result0`), Swap Hwc Chw (`operand0` to `result0`). Confirm the real class name in the SDK source before use.

### Configuration classes and enums (the two official documents use different naming. See DO NOT item 8)

| Source | Configuration classes | Enums / structs |
| --- | --- | --- |
| use-different-operators | `ArithmeticComposeConfiguration`, `ComparisonOperatorConfiguration`, `NmsConfiguration`, `NormalizeConfiguration`, `ConvertColorConfiguration`, `SortMatrixOpConfiguration`, `RenderTextConfiguration`, `JavascriptOperatorConfiguration` | `CustomizedComparison{LargerThan, SmallerThan, SmallerOrEqual, LargerOrEqual, EqualTo, NotEqual}`, `NormalizeType{L1, L2, Inf, MinMax}`, `MatrixSortType{Column, Row}`, `FontTypeFace{Default, SansSerif, Serif, MonoSpace, Bold, Italic}`, `GltfOperatorAttribute{Texture, Animation, WorldPose, LocalTransform, MaterialMetallicFactor, MaterialRoughnessFactor, MaterialOcclusionMapTextureFactor, MaterialBaseColorFactor, MaterialEmissiveFactor, MaterialEmissiveStrengthFactor, MaterialEmissiveTextureFactor, MaterialBaseColorTextureFactor, MaterialNormalMapTextureFactor, MaterialMetallicRoughnessTexture}` |
| use-cases / debug-tensors (**complete compilable examples**) | `RenderTextOperatorConfiguration`, `ColorConvertOperatorConfiguration`, `ArithmeticComposeOperatorConfiguration`, `UpdateGltfOperatorConfiguration`, `ModelOperatorConfiguration` | `SecureMRFontTypeface.SansSerif`, `SecureMRGltfOperatorAttribute.MaterialBaseColorTexture`, `SecureMRModelEncoding.Float32/.Int32`, `SecureMRModelType.QnnContextBinary`, `SecureMROperatorModelConfig{encodingType, nodeName, operatorIOName}` |

### Frequently used operand and result names (**case and spaces must match exactly**)

`operand0`, `operand1`, `operand`, `result`, `src`, `dst`, `src image`, `dst image`, `src points`, `dst points`, `src slices`, `src channel slice`, `dst slices`, `dst channel slice`, `affine`, `object points`, `image points`, `camera matrix`, `camera intrinsic` (misspelled as `camera intrisic` in one code sample), `left image`, `right image`, `timestamp`, `uv`, `point_xyz`, `rotation`, `translation`, `scale`, `scores`, `boxes`, `indices`, `sorted`, `alpha_beta`, `gltf`, `world pose`, `visible`, `view locked`, `rgb image`, `texture ID`, `material ID`, `node ID`, `animation ID`, `animation timer`, `transform`, `value`, `text`, `start`, `colors`, `font size`.

### Readback and Dynamic Texture terms

`TensorDataType.DynamicTextureByte`, `TensorDataType.DynamicTextureFloat`, `TensorUsage.DynamicTexture`, `GlobalTensor`, `PipelinePlaceholder`, `Operator Load_Texture`, `Operator Update_Texture`, `Operator UpdateMaterial`, `tensor.CreateBufferAsync`, `android.permission.CAMERA`, `com.picovr.permission.SPATIAL_DATA`.

## DO NOT

1. **Do not assume VST images or inference results can be read back into the app.**
   Wrong: `var frame = provider.GetVstImage();`, or expecting an operator result to land in a C# variable.
   Right: results stay inside the SecureMR service and are rendered there. When data is needed, use Readback Tensor (`tensor.CreateBufferAsync`) and **obtain camera or spatial data permission first** (rules 19 and 20).

2. **Do not bind a global tensor directly to an operator.**
   Wrong: `op.SetOperand("gltf", provider.CreateTensor<Gltf>(bytes));`
   Right: `var ph = pipeline.CreateTensorReference<Gltf>(); op.SetOperand("gltf", ph);`, then `mapping.Set(ph, globalGltfTensor); pipeline.Execute(mapping);`

3. **Do not create a glTF tensor inside a pipeline.**
   Wrong: `pipeline.CreateTensor<Gltf>(bytes)`.
   Right: glTF tensors are global only: `provider.CreateTensor<Gltf>(tvGltf.bytes)`. The pipeline side holds only a placeholder.

4. **Do not write space-separated operand names in camel case.**
   Wrong: `SetOperand("textureID", ...)`, `SetOperand("fontSize", ...)`, `SetOperand("srcImage", ...)`, `SetOperand("worldPose", ...)`.
   Right: `"texture ID"`, `"font size"`, `"src image"`, `"world pose"`. The official names contain spaces (see the API anchors list).

5. **Do not guess the operand names of `ArithmeticComposeOperator`.**
   Wrong: the text says each operand is named `{X}`, but its own example writes `SetOperand("operand0", ...)`. The two conflict.
   Right: follow the complete runnable example: `normalizeOp.SetOperand("{0}", cropGrayFloat);`. In the expression, `{0}` and `{1}` are operand indices. An expression takes at most 10 operands, and each must be a `Matrix`.

6. **Do not call `Update_Texture` on a Dynamic Texture.**
   Wrong: calling `Update_Texture` to copy data after creating a Dynamic Texture.
   Right: write directly to the bound tensor. The texture updates automatically. `Update_Texture` belongs only to static textures created without Dynamic Texture.

7. **Do not invent SecureMR "manager" or "read data" APIs.**
   Wrong: `SecureMRManager.Instance`, `PXR_SecureMR.Initialize()`, `pipeline.Run()`, `pipeline.Submit()`, `tensor.GetData()`, `tensor.ReadBack()`, `CreateOperator(OperatorType.Xxx)`.
   Right: the corpus contains only `new Provider(w,h)`, `provider.CreatePipeline()`, `pipeline.CreateOperator<T>(config)`, `pipeline.Execute/ExecuteAfter/ExecuteConditional(mapping)`, `tensor.Reset(data)`, `tensor.Destroy()`, and `tensor.CreateBufferAsync`.

8. **Do not write configuration class or enum names from memory.**
   Wrong: mixing `RenderTextConfiguration` with `SecureMRFontTypeface`.
   Right: the two official documents use two naming sets (`XxxConfiguration` with bare enums, vs `XxxOperatorConfiguration` with `SecureMR*`-prefixed enums). The complete example uses the second set. **Before writing code, confirm the current version's real names in the SDK source.** Do not mix the two sets. Similar risk: `UvTo3DInCameraSpaceOperator` vs `UVTo3DInCameraSpaceOperator`.

9. **Do not forget the `Color` type alias.**
   Wrong: with both `using UnityEngine;` and `using Unity.XR.PXR.SecureMR;`, writing `CreateTensor<byte,Color>(...)` directly causes an ambiguity compile error.
   Right: add `using Color = Unity.XR.PXR.SecureMR.Color;` as in the official sample.

10. **Do not create a new Provider each frame or change resolution midway.**
    Wrong: `new Provider(...)` in `Update()`, or changing the VST size during a session.
    Right: one Provider per session, and its size is fixed. If you must change it, call `Destroy()` and recreate. All tensors and pipelines in it become invalid.

11. **Do not expect to reclaim an operator on its own.**
    Wrong: `myOperator.Destroy();`
    Right: operators are destroyed only with their pipeline. Tensor `Destroy()` also works only on global tensors.

12. **Do not give an initial value to a JavaScript Operator variable that is meant as an operand or result.**
    Wrong: `var sum2 = 0;`, then expecting `sum2` to bind as an output.
    Right: only **uninitialized, non-`const` global `var`s** are recognized as operands or results. Their names and order follow declaration order. The same variable can serve as both an operand and a result.

13. **Do not use `CAMERA SPACE TO WORLD` while `view locked` is in effect.**
    Wrong: positioning a model with the camera-to-world transform while it follows view space.
    Right: `CAMERA SPACE TO WORLD` uses the OpenXR local coordinate system as its reference. The two semantics conflict (rule 16).

14. **Do not let the VST result tensor size disagree with the Provider.**
    Wrong: `Provider(3248, 2464)`, but building the `left image` result as 256x256.
    Right: the two must match. In the sample, the raw image tensor is `CreateTensor<byte,Matrix>(3, new TensorShape(image_height, image_width))`. To crop, use `GetAffineOperator` with `ApplyAffineOperator`, not a small shape given directly.
