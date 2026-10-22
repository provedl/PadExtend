# 阶段三：IddCx 帧处理研究与桥接方案

> 基于 microsoft/Windows-driver-samples commit 2dc3fd3a 的 IndirectDisplay 示例源码（已本地编译验证）。
> 文件：src/Driver/windows-driver-samples/video/IndirectDisplay/IddSampleDriver/Driver.cpp

## 1. 类结构与职责

- `IndirectDeviceContext`：处理 IddCx 适配器回调，创建适配器、上报显示器到达（FinishInit）。
- `IndirectMonitorContext`：单个显示器上下文，持有 `SwapChainProcessor`。
  - `AssignSwapChain(SwapChain, RenderAdapter LUID, NewFrameEvent)`：系统分配交换链时调用，基于渲染 GPU 建 `Direct3DDevice`，启动处理线程。
  - `UnassignSwapChain()`：交换链回收，停止处理线程。
- `Direct3DDevice`：按 `RenderAdapter` LUID 从 DXGI 枚举对应 GPU，建 D3D11 设备/上下文。
- `SwapChainProcessor`：独立线程消费交换链帧。核心是 `RunCore()`。

## 2. 帧出口：SwapChainProcessor::RunCore（关键）

循环逻辑：
1. `IddCxSwapChainSetDevice` 绑定我们的 D3D11 设备到交换链。
2. `IddCxSwapChainReleaseAndAcquireBuffer` 索要下一帧。
   - 返回 `E_PENDING`：用 `WaitForMultipleObjects`（NewFrameEvent + TerminateEvent，超时 16ms）等待新帧。
3. 成功时拿到 `Buffer.MetaData.pSurface`（`IDXGIResource`，GPU 上的桌面纹理表面）。
4. **TODO 区（我们的注入点）**：示例什么都不做，仅释放引用。微软建议的处理方式：
   - GPU 拷贝到 staging surface 再映射到 CPU，或
   - GPU 硬件编码（H.264），或
   - VPBlt / compute shader 编码。
5. `IddCxSwapChainFinishedProcessingFrame` 通知系统本帧处理完。
6. 可选 `IddCxSwapChainReportFrameStatistics` 上报编码/发送耗时。

线程用 `AvSetMmThreadCharacteristicsW(L"Distribution")` 提升多媒体优先级。

## 3. 显示模式（回应项目 1920×1080 要求）

- `s_SampleMonitors` 两套 EDID（Dell S2719DGF / Lenovo Y27fA），第一套模式含 1920×1080@60。
- `TargetModes` 列表原生包含：3840×2160@60、2560×1440@{144,90,60}、1920×1080@{144,90,60}。
- 结论：示例原生支持 1920×1080@60。自定义驱动把虚拟屏固定为 1920×1080，只需裁剪 EDID/模式表。

## 4. 对 PadExtend 的桥接设计（阶段四蓝图）

目标：把 RunCore 的 `pSurface` 帧 → H.264 硬件编码 → 经用户态服务 → USB → Android 解码显示。

关键约束（基于源码事实）：
- 帧是 GPU 纹理（IDXGIResource / ID3D11Texture2D）。最高效路径是 GPU 直编码，避免拷回 CPU（符合“硬件编码降 CPU”目标）。
- RunCore 是性能关键区，重活必须异步化：驱动内只做“把纹理交给编码器/入队”，编码与 USB 发送放到独立线程或用户态服务。
- 驱动（UMDF 用户态）与业务服务（C#）之间需要一座桥：
  - 方案A：驱动内用 Media Foundation H.264 硬件编码器直接编码 pSurface，产出的码流通过共享内存/命名管道送到用户态 C# 服务，再走 USB。
  - 方案B：驱动把 pSurface 复制到共享的 D3D keyed-mutex 纹理，用户态服务读取后编码。
  - 两种都需验证 UMDF 驱动进程内能否访问硬件编码器与跨进程共享 GPU 资源——阶段四先做最小 POC，不假定可行。
- 不得假定“驱动帧能直接通过未经验证的 C# API 读取”（项目原则）。桥接层先用 C++ 实现并最小化。

## 5. 下一步（阶段四起步，待确认）

1. 先在示例 TODO 区做最小实验：把 pSurface 复制到一张 staging 纹理并统计帧率/耗时（验证我们能稳定拿到帧，不改动其它逻辑）。
2. 再评估 Media Foundation 硬件 H.264 编码器对 pSurface 的直接编码可行性。
3. 编码产物先落地写文件验证，再接 USB 传输到现有 Android 接收端。

## 6. 抓帧+FPS 最小实验结果（2026-10-16，真机验证）

工作副本：src/Driver/PadExtend.IddDriver（官方原版保留，出处见 UPSTREAM.txt）。
改动：仅在 SwapChainProcessor::RunCore 的 TODO 区加入：
- 从 Buffer.MetaData.pSurface 取 ID3D11Texture2D 的 D3D11_TEXTURE2D_DESC，读取实际 Width/Height/Format（不写死分辨率）。
- 按 QueryPerformanceCounter 统计每秒 FPS；分辨率/格式变化时立即记录。
- 结果同时 TraceEvents 并追加写 C:\ProgramData\PadExtend\frames.log（避开 WPP/tracefmt 解析问题）。

验证输出（frames.log 实测）：
- 成功抓到帧，FPS 统计随画面活动在 0.5~100+ 之间波动（活动时 40~100，静止时趋近 0）。
- 分辨率自动适配：日志出现 3840x2160、2560x1440、1024x768 三种模式，DXGI_FORMAT=87（B8G8R8A8_UNORM）。
- 证明“动态读取每帧纹理尺寸”有效，无需硬编码分辨率。

重要发现（影响阶段四架构）：
- 日志中三种分辨率并发刷新，对应本机同时存在的多个 IDD 实例（本驱动 + 向日葵 OrayIddDriver + MuMu）。
- 当前统计把不同显示器实例的帧混在一起。阶段四必须按 IDDCX_MONITOR / 交换链实例区分帧流，只处理本产品创建的那块虚拟屏。
- WPP trace（tracelog+tracefmt）命令行解析 PDB 格式困难，改用文件日志更可靠；生产可换命名管道/共享内存送到用户态服务。

结论：阶段三“能稳定拿到虚拟显示器帧并读取其分辨率”已验证。进入阶段四前需解决“实例区分”和“帧送出用户态”的桥接。
