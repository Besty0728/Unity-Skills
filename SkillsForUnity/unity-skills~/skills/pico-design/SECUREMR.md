# PICO Unity SDK — SecureMR 隐私安全混合现实（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。
> 本域是 AI 训练语料稀缺区，下面出现的每一个类名、operand 名、枚举值都来自官方文档原文；文中未出现的 API 请一律视为不存在，先查 SDK 源码再写。

## 何时加载本文档

- 需要在 PICO 4 Ultra 上做**不获取相机权限**的 MR 效果（VST 图像处理、环境取色、目标检测叠加）。
- 代码里出现 `Unity.XR.PXR.SecureMR` 命名空间、`Provider` / `Pipeline` / `Tensor` / `*Operator` 这类 SecureMR 概念。
- 要把 PyTorch / TensorFlow / ONNX 模型转成 QNN 二进制，塞进 `RunModelInferenceOperator` 在 NPU 上跑。
- 要用 glTF 模型做 SecureMR 侧渲染（贴 VST 画面、绘制文字、数据驱动动画、换材质贴图）。
- 涉及 Dynamic Texture、Readback Tensor、JavaScript Operator 这些 3.2.0+ 新增能力。
- 排查 `Secure MR::Server` 打出的 `[INVALID PARAMETER]` / `[HANDLE NOT INITIALIZED]` 错误。

## 关键规则

| # | 规则 | 来源 |
| --- | --- | --- |
| 1 | 硬件门槛：仅 **PICO 4 Ultra 系列** + 系统 **5.13.0 及以上**；官方示例工程环境为 Unity 6 + PICO Unity Integration SDK 3.2.0 | [overview](https://developer-cn.picoxr.com/document/unity/securemr-overview/) / [samples](https://developer-cn.picoxr.com/document/unity/securemr-samples/) |
| 2 | 启用是两件套：在 **PXR_Manager (Script)** 面板勾选 **SecureMR**，再按 Video Seethrough 文档配置视频透视；示例代码里另有 `PXR_Manager.EnableVideoSeeThrough = true` | [quickstart](https://developer-cn.picoxr.com/document/unity/securemr-quickstart/) / [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/) |
| 3 | 通信严格单向：应用可传入数据 / 3D 资产 / 算法 / 执行命令，**相机帧、深度图、MR 处理结果一律不回传**；SecureMR 服务维持一个覆盖式 OpenXR 会话，把 pipeline 结果渲染在应用图层**下方**的独立图层 | [architecture](https://developer-cn.picoxr.com/document/unity/securemr-architecture/) |
| 4 | 每个应用同一时刻**只能有一个未被销毁的 Provider session**；session 一旦销毁，其中创建的所有资源全部回收释放 | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 5 | `new Provider(width, height)` 的宽高即期望的 VST 图像尺寸，**创建后不可更改**（要换尺寸只能先 `Destroy()` 再重建）；默认 1024x1024，建议与 AI 模型输入尺寸一致以省掉 resize | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 6 | tensor 由 shape / channel / dataType / usage 四属性定义，且 **channel 不算 shape 的一个维度**（OpenCV 约定）：`[1024,1024,3]` 单通道 ≠ `[1024,1024]` 三通道 | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 7 | usage 取值：`Matrix`（默认）/ `Scalar` / `Timestamp` / `Color` / `Point` / `Slice`，外加特殊的 `Gltf`；`Timestamp` 必须 4-channel `Int32` 且 shape `[1,]`；`Slice` 必须 2/3-channel 整型，2 通道 = `(START, END)`，3 通道 = `(START, END, SKIP)` | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 8 | glTF tensor **只能建成全局 tensor**（`provider.CreateTensor<Gltf>(bytes)`），因为渲染资产有生命周期；`tensor.Destroy()` 同样**仅支持全局 tensor** | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 9 | 全局 tensor **不能直接绑定到 operator**（线程安全约束），必须在 pipeline 内建占位符 `CreateTensorReference<...>()` 绑给 operator，提交时用 `pipeline.CreateTensorMapping()` 建映射对象、`Set(占位符, 全局 tensor)` 后传给 `Execute*`；**该映射只对本次提交有效** | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 10 | pipeline 内 operator **按加入顺序依次执行**；提交一次 = SecureMR 服务调度整条 pipeline 运行**一次**，返回一个 run 句柄 | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 11 | 执行调度：同一 pipeline 不并发；读/写同一全局 tensor 的执行互相等待；`condition` 提示**必须是全局 tensor**，指向全零 tensor 时本次执行被取消；SDK 不限频但有排队，前一次没跑完新提交会入队，必须按实际耗时自行控频 | [key-concepts](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 12 | 全局 tensor 用「无锁」等待做同步，因此**慢的生产者 pipeline 会拖慢所有下游**；解耦手法是插一个「复制者 pipeline」，把最新值锁存到一个副本全局 tensor，下游改读副本 | [pipeline-synchronization](https://developer-cn.picoxr.com/document/unity/pipeline-synchronization/) |
| 13 | operator **不能单独销毁**，只能随所属 pipeline 一起销毁 | [use-different-operators](https://developer-cn.picoxr.com/document/unity/use-different-operators/) |
| 14 | `RectifiedVstAccessOperator` 的 `left image` / `right image` 结果 shape 必须与创建 Provider 时设定的 VST 宽高一致，否则运行期报 `Server Err: [INVALID PARAMETER] ... result N size (2464x3248) mismatches the config (256x256)` | [use-different-operators](https://developer-cn.picoxr.com/document/unity/use-different-operators/) / [troubleshooting](https://developer-cn.picoxr.com/document/unity/securemr-troubleshooting/) |
| 15 | `UpdateGltfOperator` 只有在该 glTF **已由 `SwitchGltfRenderStatusOperator` 开始渲染后**才生效；而 `SwitchGltfRenderStatusOperator` 的 `world pose` 为空即停止渲染（此时 `visible` 取任何值都不可见） | [use-different-operators](https://developer-cn.picoxr.com/document/unity/use-different-operators/) |
| 16 | `view locked` 非零时 glTF 以 OpenXR **view space** 为参考系，此时**不应**再使用 `CAMERA SPACE TO WORLD`（它以 OpenXR local 坐标系为参考） | [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/) |
| 17 | Dynamic Texture 的成立条件是**两个枚举同时满足**：DataType ∈ {`TensorDataType.DynamicTextureByte`, `TensorDataType.DynamicTextureFloat`} 且 Usage = `TensorUsage.DynamicTexture`；仅 `GlobalTensor` 或 `PipelinePlaceholder` 可声明为 Dynamic Texture | [use-dynamic-texture](https://developer-cn.picoxr.com/document/unity/use-dynamic-texture/) |
| 18 | Dynamic Texture 经 `Operator Load_Texture` 绑定后，**不需要、也不允许**调用 `Operator Update_Texture`；更新内容只能直接写它绑定的 tensor 数据（非 Dynamic Texture 建出来的是静态 texture，才需要 `Update_Texture`） | [use-dynamic-texture](https://developer-cn.picoxr.com/document/unity/use-dynamic-texture/) |
| 19 | Readback 权限是 **session 级**：只要当前 SecureMR Framework Session 内**任一 pipeline** 用了相机类 operator（如 `Rectified_VST_Access`），**无论是否实际执行**，都必须已授予 `android.permission.CAMERA`；用了空间数据类 operator（如 `UV_to_Camera_Space`）则必须已授予 `com.picovr.permission.SPATIAL_DATA` | [use-the-readback-tensor](https://developer-cn.picoxr.com/document/unity/use-the-readback-tensor/) |
| 20 | 读回 CPU 用 `tensor.CreateBufferAsync`：跨进程拷贝、耗时较长且是**一次性**的，tensor 更新后必须重新拷贝；零拷贝 GPU 读回（Vulkan Image / OpenGL ES Image）**仅支持 dynamic-texture 类型 tensor**，且只能当 GPU sampler 用 | [use-the-readback-tensor](https://developer-cn.picoxr.com/document/unity/use-the-readback-tensor/) |

## 工作流程

### A. 最小可用：在 VST 场景里渲染一个 glTF 模型

来源：[quickstart](https://developer-cn.picoxr.com/document/unity/securemr-quickstart/) + [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/)

1. **PXR_Manager (Script)** 面板勾选 **SecureMR**；按 Video Seethrough 文档配好透视。
2. `Start()` 里 `PXR_Manager.EnableVideoSeeThrough = true;`，然后 `provider = new Provider(image_width, image_height);`。
3. `pipeline = provider.CreatePipeline();` → `pipeline.CreateOperator<SwitchGltfRenderStatusOperator>()`。
4. glTF 资源走 `TextAsset`：全局侧 `provider.CreateTensor<Gltf>(tvGltf.bytes)`，pipeline 侧 `pipeline.CreateTensorReference<Gltf>()` 建占位符。
5. 位姿是 4x4 矩阵：`pipeline.CreateTensor<float,Matrix>(1, new TensorShape(4,4))` + `poseMat.Reset(poseMatValue)`。
6. `SetOperand("gltf", placeholder)`、`SetOperand("world pose", poseMat)`。
7. 每次驱动（示例用 `InvokeRepeating(nameof(RenderFrame), 0, 0.02f)`）：`var m = pipeline.CreateTensorMapping(); m.Set(placeholder, gltfTensor); pipeline.Execute(m);`

### B. 把 VST 画面贴到 glTF 材质上（双 pipeline + 全局 tensor）

来源：[use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/)

1. **采集 pipeline**：`RectifiedVstAccessOperator.SetResult("left image", rawRgb)`（`rawRgb` 为 3-channel `byte`/`Matrix`，示例 shape 用 `new TensorShape(image_height, image_width)`）。
2. 裁剪走仿射：`GetAffineOperator`（`src`/`dst` 各 3 个 2-channel `Point`）→ `ApplyAffineOperator`（`affine` + `src image` → `dst image`）。
3. `dst image` 写进一个**占位符** `cropRgbWrite`，提交时映射到全局 tensor `cropRgbGlobal`。
4. **渲染 pipeline**：`LoadTextureOperator` 以 `rgb image` = 读侧占位符 `cropRgbRead`、`gltf` = glTF 占位符，输出 `SetResult("texture ID", gltfTextureIndex)`。
5. `UpdateGltfOperator` 配 `SecureMRGltfOperatorAttribute.MaterialBaseColorTexture`，`SetOperand("gltf" / "material ID" / "value")`，其中 `value` 就是上一步的 texture ID tensor。
6. `SwitchGltfRenderStatusOperator` 提供 `gltf` + `world pose` 开启渲染。
7. 两条 pipeline 各自 `Execute(mapping)`，靠 `cropRgbGlobal` 这一个全局 tensor 串起来（同步语义见规则 11/12）。

### C. 跑自定义算法（QNN 模型）

来源：[profile-securemr-models](https://developer-cn.picoxr.com/document/unity/profile-securemr-models/) + [create-a-qnn-model-to-run-algorithms](https://developer-cn.picoxr.com/document/unity/create-a-qnn-model-to-run-algorithms/) + [use-cases](https://developer-cn.picoxr.com/document/unity/securemr-use-cases/)

1. 用 PyTorch / TensorFlow / ONNX 实现算法（SecureMR 原生 operator 表达不了的逻辑走这条路）。
2. 装 Docker，用 SecureMRTools 里的 `./convert_model.sh --input /path/to/onnx_model`（可选 `--custom_io custom_io.yaml` 指定 NCHW/NHWC 布局）转成 QNN context binary。
3. 可选：`./run_docker_container.sh` + `./profile_model.sh -m model.serialized.bin -i 000000.raw` 拿 `qnn-net-run` 的耗时数据。
4. 模型较大时放 `StreamingAssets`，用 `UnityWebRequest` 读成 `byte[]` 后再建 operator（示例是协程里先 `LoadData()` 再 `CreatePipeline()`）。
5. 用 `SecureMROperatorModelConfig { encodingType, nodeName, operatorIOName }` 逐个描述输入 / 输出节点，`encodingType` 取 `SecureMRModelEncoding.Float32` / `.Int32`。
6. `new ModelOperatorConfiguration(inputConfigs, outputConfigs, fileBytes, SecureMRModelType.QnnContextBinary, "mnist")` → `pipeline.CreateOperator<RunModelInferenceOperator>(config)`。
7. **绑定用的名字就是 `operatorIOName`**（示例：`SetOperand("input_1", ...)`、`SetResult("_538", ...)`、`SetResult("_539", ...)`），不是固定的 `operand0`/`result`。
8. 前后处理用 CPU operator 拼：`ConvertColorOperator`（RGB→GRAY）→ `AssignmentOperator`（uint8→float32）→ `ArithmeticComposeOperator("{0} / 255.0")` 归一化 → 模型。

### D. 调试 pipeline 内的 tensor

来源：[debug-tensors-in-a-pipeline](https://developer-cn.picoxr.com/document/unity/debug-tensors-in-a-pipeline/) + [troubleshooting](https://developer-cn.picoxr.com/document/unity/securemr-troubleshooting/)

1. tensor 内容读不出来，唯一直观手段是把它渲染出来：`RenderTextOperator` 的 `text` operand 接任意 tensor，非 UTF-8 标量会**以矩阵形式打印原始数值**，默认最多显示 5x5 摘要。
2. 配置：`new RenderTextOperatorConfiguration(SecureMRFontTypeface.SansSerif, "en-US", 1440, 960)`；再配 `start`（2-channel float `Point`，XY 为 0~1 相对值）、`colors`（4-channel `byte` `Color`，shape `{2,}`，先文字色后背景色）、`texture ID`、`font size`、`gltf`。
3. 想按条件显示，可把布尔结果 tensor 接到 `SwitchGltfRenderStatusOperator` 的 `visible`。
4. 看 logcat 里 `Secure MR::Server` 的报错：`[INVALID PARAMETER]` 多为 shape/尺寸与 config 不符（见规则 14）；`[HANDLE NOT INITIALIZED]: cannot find the local tensor with ID = ...` 表示有 tensor 没注册进该 pipeline（底层注册接口是 `xrCreateSecureMrPipelineTensorPICO`），日志里会给出 pipeline ID。

## 核心 API 锚点

### 命名空间与入口

| 名称 | 说明 |
| --- | --- |
| `Unity.XR.PXR` / `Unity.XR.PXR.SecureMR` | 示例中同时 `using` 这两个命名空间 |
| `using Color = Unity.XR.PXR.SecureMR.Color;` | 官方示例的显式别名，用于避开 `UnityEngine.Color` 冲突 |
| `PXR_Manager` | 面板勾选 **SecureMR**；示例代码用 `PXR_Manager.EnableVideoSeeThrough` |

### 三大句柄类型与方法

| 类型 | 文档中出现的成员 |
| --- | --- |
| `Provider` | `new Provider(width, height)`、`CreatePipeline()`、`CreateTensor<Gltf>(byte[])`、`CreateTensor<T,Usage>(channel, TensorShape)`、`Destroy()` |
| `Pipeline` | `CreateOperator<T>()` / `CreateOperator<T>(config)`、`CreateTensor<T,Usage>(channel, TensorShape[, data])`、`CreateTensorReference<Gltf>()` / `CreateTensorReference<T,Usage>(channel, TensorShape)`、`CreateTensorMapping()`、`Execute(mapping)`、`ExecuteAfter(runId, mapping)`、`ExecuteConditional(runId, mapping)`、`Destroy()` |
| `Tensor` | `Reset(data)`（复用/更新内容）、`Destroy()`（仅全局）、`CreateBufferAsync`（Readback 到 CPU 内存） |
| `CreateTensorMapping()` 的返回对象（官方示例只用 `var` 接收，**未给出类名**，勿臆造） | `Set(placeholder, globalTensor)`，作为 `Execute*` 的可选参数 |
| `TensorShape` | `new TensorShape(4,4)` / `new TensorShape(30)` / `new TensorShape(int[])` |

### Operator 类名（全部来自 use-different-operators / use-cases）

- 算术与逻辑：`ArithmeticComposeOperator`、`ElementwiseMinOperator`、`ElementwiseMaxOperator`、`ElementwiseMultiplyOperator`、`ElementwiseOrOperator`、`ElementwiseAndOperator`、`CustomizedCompareOperator`、`AllOperator`、`AnyOperator`、`InversionOperator`、`NormalizeOperator`
- 数据搬运与排序：`AssignmentOperator`、`SortVectorOperator`、`SortMatrixOperator`、`ArgmaxOperator`
- 几何与视觉：`NmsOperator`、`SolvePnPOperator`、`GetAffineOperator`、`ApplyAffineOperator`、`ApplyAffinePointOperator`、`GetTransformMatrixOperator`、`ConvertColorOperator`、`UvTo3DInCameraSpaceOperator`（**代码示例写作 `UVTo3DInCameraSpaceOperator`**）、`CameraSpaceToWorldOperator`、`RectifiedVstAccessOperator`
- 渲染与 glTF：`SwitchGltfRenderStatusOperator`、`UpdateGltfOperator`、`RenderTextOperator`、`LoadTextureOperator`
- 模型与脚本：`RunModelInferenceOperator`、`JavascriptOperator`
- 文档中只给了功能说明、**未给 C# 类名**的：SVD（`src` → `w`/`u`/`vt`）、Norm（`operand0` → `result0`）、Swap Hwc Chw（`operand0` → `result0`）——用之前必须查 SDK 源码确认真实类名。

### 配置类与枚举（⚠ 官方两篇文档命名不一致，见 DO NOT 第 8 条）

| 出处 | 配置类 | 枚举 / 结构体 |
| --- | --- | --- |
| use-different-operators | `ArithmeticComposeConfiguration`、`ComparisonOperatorConfiguration`、`NmsConfiguration`、`NormalizeConfiguration`、`ConvertColorConfiguration`、`SortMatrixOpConfiguration`、`RenderTextConfiguration`、`JavascriptOperatorConfiguration` | `CustomizedComparison{LargerThan, SmallerThan, SmallerOrEqual, LargerOrEqual, EqualTo, NotEqual}`、`NormalizeType{L1, L2, Inf, MinMax}`、`MatrixSortType{Column, Row}`、`FontTypeFace{Default, SansSerif, Serif, MonoSpace, Bold, Italic}`、`GltfOperatorAttribute{Texture, Animation, WorldPose, LocalTransform, MaterialMetallicFactor, MaterialRoughnessFactor, MaterialOcclusionMapTextureFactor, MaterialBaseColorFactor, MaterialEmissiveFactor, MaterialEmissiveStrengthFactor, MaterialEmissiveTextureFactor, MaterialBaseColorTextureFactor, MaterialNormalMapTextureFactor, MaterialMetallicRoughnessTexture}` |
| use-cases / debug-tensors（**完整可编译示例**） | `RenderTextOperatorConfiguration`、`ColorConvertOperatorConfiguration`、`ArithmeticComposeOperatorConfiguration`、`UpdateGltfOperatorConfiguration`、`ModelOperatorConfiguration` | `SecureMRFontTypeface.SansSerif`、`SecureMRGltfOperatorAttribute.MaterialBaseColorTexture`、`SecureMRModelEncoding.Float32/.Int32`、`SecureMRModelType.QnnContextBinary`、`SecureMROperatorModelConfig{encodingType, nodeName, operatorIOName}` |

### 高频 operand / result 名（**大小写与空格必须一模一样**）

`operand0`、`operand1`、`operand`、`result`、`src`、`dst`、`src image`、`dst image`、`src points`、`dst points`、`src slices`、`src channel slice`、`dst slices`、`dst channel slice`、`affine`、`object points`、`image points`、`camera matrix`、`camera intrinsic`（代码示例中误写为 `camera intrisic`）、`left image`、`right image`、`timestamp`、`uv`、`point_xyz`、`rotation`、`translation`、`scale`、`scores`、`boxes`、`indices`、`sorted`、`alpha_beta`、`gltf`、`world pose`、`visible`、`view locked`、`rgb image`、`texture ID`、`material ID`、`node ID`、`animation ID`、`animation timer`、`transform`、`value`、`text`、`start`、`colors`、`font size`。

### Readback / Dynamic Texture 专有名词

`TensorDataType.DynamicTextureByte`、`TensorDataType.DynamicTextureFloat`、`TensorUsage.DynamicTexture`、`GlobalTensor`、`PipelinePlaceholder`、`Operator Load_Texture`、`Operator Update_Texture`、`Operator UpdateMaterial`、`tensor.CreateBufferAsync`、`android.permission.CAMERA`、`com.picovr.permission.SPATIAL_DATA`。

## DO NOT

1. **不要以为能把 VST 图像 / 推理结果读回应用**
   ❌ `var frame = provider.GetVstImage();` / 期待 operator 结果直接进 C# 变量
   ✅ 结果只能留在 SecureMR 服务里渲染出来；确需数据时走 Readback Tensor（`tensor.CreateBufferAsync`）并**先拿到相机 / 空间数据权限**（规则 19、20）。

2. **不要把全局 tensor 直接绑给 operator**
   ❌ `op.SetOperand("gltf", provider.CreateTensor<Gltf>(bytes));`
   ✅ `var ph = pipeline.CreateTensorReference<Gltf>(); op.SetOperand("gltf", ph);`，再 `mapping.Set(ph, globalGltfTensor); pipeline.Execute(mapping);`

3. **不要在 pipeline 里创建 glTF tensor**
   ❌ `pipeline.CreateTensor<Gltf>(bytes)`
   ✅ glTF tensor 只能是全局的：`provider.CreateTensor<Gltf>(tvGltf.bytes)`；pipeline 侧只放占位符。

4. **不要把带空格的 operand 名写成驼峰**
   ❌ `SetOperand("textureID", ...)`、`SetOperand("fontSize", ...)`、`SetOperand("srcImage", ...)`、`SetOperand("worldPose", ...)`
   ✅ `"texture ID"`、`"font size"`、`"src image"`、`"world pose"`（官方名带空格，见 API 锚点清单）。

5. **不要猜 `ArithmeticComposeOperator` 的 operand 名**
   ❌ 文档正文说「各个 operand 的名称为 `{X}`」，其自身示例却写 `SetOperand("operand0", ...)`——两处冲突
   ✅ 以完整可运行示例为准：`normalizeOp.SetOperand("{0}", cropGrayFloat);`，表达式里 `{0}`/`{1}` 即 operand 序号；表达式 operand 最多 10 个且必须是 `Matrix`。

6. **不要对 Dynamic Texture 调用 `Update_Texture`**
   ❌ 建完 Dynamic Texture 后再 `Update_Texture` 拷数据
   ✅ 直接写它绑定的 tensor，texture 自动更新；`Update_Texture` 只属于非 Dynamic Texture 建出的静态 texture。

7. **不要臆造 SecureMR 的「管理器 / 读数据」API**
   ❌ `SecureMRManager.Instance`、`PXR_SecureMR.Initialize()`、`pipeline.Run()`、`pipeline.Submit()`、`tensor.GetData()`、`tensor.ReadBack()`、`CreateOperator(OperatorType.Xxx)`
   ✅ 语料中只存在：`new Provider(w,h)`、`provider.CreatePipeline()`、`pipeline.CreateOperator<T>(config)`、`pipeline.Execute/ExecuteAfter/ExecuteConditional(mapping)`、`tensor.Reset(data)`、`tensor.Destroy()`、`tensor.CreateBufferAsync`。

8. **不要凭记忆写配置类 / 枚举名**
   ❌ 混着写 `RenderTextConfiguration` 与 `SecureMRFontTypeface`
   ✅ 官方两篇文档存在两套命名（`XxxConfiguration` + 裸枚举 vs `XxxOperatorConfiguration` + `SecureMR*` 前缀枚举）。完整示例用的是后者；写代码前**必须在 SDK 源码里确认当前版本的真名**，不要把两套混搭。同类风险：`UvTo3DInCameraSpaceOperator` vs `UVTo3DInCameraSpaceOperator`。

9. **不要漏掉 `Color` 类型别名**
   ❌ 同时 `using UnityEngine;` 和 `using Unity.XR.PXR.SecureMR;` 后直接写 `CreateTensor<byte,Color>(...)` → 二义性编译错误
   ✅ 照官方示例加 `using Color = Unity.XR.PXR.SecureMR.Color;`。

10. **不要用「每帧新建 Provider / 改分辨率」的写法**
    ❌ `Update()` 里 `new Provider(...)`，或想中途换 VST 尺寸
    ✅ 一个 session 一个 Provider，尺寸不可变；确需改则 `Destroy()` 后重建，并清楚其中所有 tensor / pipeline 都会失效。

11. **不要指望 operator 能单独回收**
    ❌ `myOperator.Destroy();`
    ✅ operator 只随 pipeline 销毁；tensor 的 `Destroy()` 也只对全局 tensor 有效。

12. **不要在 JavaScript Operator 里给要当 operand/result 的变量赋初值**
    ❌ `var sum2 = 0;` 然后期待把 `sum2` 当输出绑定
    ✅ 只有**未初始化且非 `const` 的全局 `var`** 才会被识别为 operand / result，名称与顺序同声明顺序；同一个变量可同时充当 operand 和 result。

13. **不要在 `view locked` 生效时使用 `CAMERA SPACE TO WORLD`**
    ❌ 模型跟随 view space 的同时用相机到世界的变换定位
    ✅ `CAMERA SPACE TO WORLD` 以 OpenXR local 坐标系为参考系，二者语义冲突（规则 16）。

14. **不要让 VST 结果 tensor 的尺寸与 Provider 不一致**
    ❌ `Provider(3248, 2464)` 却把 `left image` 结果建成 256x256
    ✅ 两者必须一致；示例中原图 tensor 写作 `CreateTensor<byte,Matrix>(3, new TensorShape(image_height, image_width))`，裁剪要用 `GetAffineOperator` + `ApplyAffineOperator` 而不是直接给个小 shape。
