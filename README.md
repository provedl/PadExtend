# PadExtend

把 Android 平板变成 Windows 的无线/USB 扩展显示屏。Windows 端通过 IDD（间接显示驱动）创建一块虚拟显示器，把画面经 Media Foundation 编码成 H.264，再通过 ADB（USB）转发的 TCP 通道实时推送到 Android 客户端，由 MediaCodec 硬解并渲染。

> 当前为开发原型阶段：IDD 虚拟屏、帧抓取、H.264 编码、协议传输与 Android 解码链路均已在真机验证。驱动使用测试签名，面向开发/测试环境。

## 功能特性

- Windows 虚拟扩展屏：基于 IddCx（UMDF）间接显示驱动，创建 1920×1080 虚拟显示器。
- 实时推流：Media Foundation H.264 编码，默认 30 FPS，可调码率。
- 低延迟 USB 通道：通过 `adb reverse` 把流量经 USB 转发，走 `TCP 127.0.0.1:27555`。
- 自定义协议 v1.1：`PEX1` 帧头（magic/seq/type/length），支持文本、JPEG、H.264 配置/关键帧/非关键帧与视频元信息，并带序号校验与断线重连。
- Android 硬解渲染：MediaCodec `video/avc` 解码输出到 SurfaceView，MJPEG 可作降级显示。

## 架构

```
┌──────────────── Windows PC ────────────────┐        USB (adb reverse)        ┌──── Android ────┐
│ IDD 虚拟显示器 (IddSampleDriver, UMDF)      │                                 │ PadExtend.Client │
│      │ 抓帧 (frame.bmp / Desktop Duplication)│   TCP 127.0.0.1:27555           │   MediaCodec     │
│      ▼                                       │  ───────────────────────────▶  │   (video/avc)    │
│ PadExtend.Sender  → PadExtend.Encoder (H.264)│       协议 v1.1 (PEX1)          │   SurfaceView    │
└──────────────────────────────────────────────┘                               └──────────────────┘
```

## 目录结构

```
src/
  Shared/                      协议与共享定义 (padextend_shared.h)
  Driver/PadExtend.IddDriver/  IDD 间接显示驱动 (IddCx/UMDF, C++) + 测试工具 IddSampleApp
  Windows/
    PadExtend.Encoder/         Media Foundation H.264 编码器 (C++ DLL)
    PadExtend.Sender/          发送端 (.NET 9, C#)：抓帧 → 编码 → 协议传输
    PadExtend.Launcher/        启动器 (.NET 9, C#)
    PadExtend.TestSender/      协议回归测试发送端
  Android/PadExtend.Client/    Android 接收端 (Kotlin, MediaCodec)
docs/                          协议规范与各阶段研究/验证报告
scripts/                       驱动换装、链路联调等 PowerShell 脚本
```

## 技术栈

- Windows 驱动：C++ / IddCx / UMDF，WDK 构建，测试签名（WDKTestCert）。
- 编码器：C++ / Media Foundation H.264 (AVC, Annex-B)。
- 发送端/启动器：C# / .NET 9（自包含 win-x64 发布，目标机免装运行时）。
- Android：Kotlin / MediaCodec，compileSdk 35 / AGP 8.5.2。
- 传输：自定义 PEX1 协议（见 [docs/protocol-v1.md](docs/protocol-v1.md)），走 ADB(USB) 转发的 TCP。

## 快速开始

预编译好的安装包和绿色版见本仓库 [Releases](https://github.com/provedl/PadExtend/releases)。

### 1. 安装 IDD 驱动（PC，管理员）

驱动为测试签名，需先开启测试模式并重启：

```powershell
bcdedit /set testsigning on
```

安装驱动（在驱动包目录）：

```powershell
pnputil /add-driver IddSampleDriver.inf /install
```

用 `IddSampleApp.exe` 创建/移除虚拟显示设备。

### 2. 安装 Android 客户端

```bash
adb install -r PadExtend.Client-debug.apk
```

release 包未签名，发布前需自行签名：

```bash
apksigner sign --ks <your.keystore> PadExtend.Client-release-unsigned.apk
```

### 3. 建立 USB 通道并启动发送端（PC）

```bash
adb reverse tcp:27555 tcp:27555
PadExtend.Sender.exe --port=27555 --fps=30 --bitrate=8000000
```

Sender 默认从 `C:\ProgramData\PadExtend\frame.bmp` 读取驱动写出的帧，经 H.264 编码后按协议 v1.1 通过 `TCP 127.0.0.1:27555` 发送，经 `adb reverse` 走 USB 到 Android 客户端。

## 从源码构建

- Windows 发送端/启动器：`dotnet publish -c Release -r win-x64 --self-contained`（需 .NET 9 SDK）。
- 编码器 DLL：运行 `src/Windows/PadExtend.Encoder/build_dll.bat`（需 MSVC / Windows SDK）。
- IDD 驱动：运行 `src/Driver/PadExtend.IddDriver/build_driver_vs2022.bat`（需 Visual Studio 2022 + WDK）。
- Android：`src/Android/PadExtend.Client/` 下执行 `./gradlew assembleDebug`。

## 文档

- [协议规范 protocol-v1.md](docs/protocol-v1.md)
- [阶段一：环境检查报告](docs/stage1-environment-report.md)
- [阶段三：IddCx 帧处理与桥接方案](docs/stage3-frame-pipeline.md)

## 备注

- 驱动使用 WDKTestCert 测试签名，仅用于开发/测试；正式分发需 EV 证书并提交 WHQL。
- Sender 发布时有 1 条无害告警（CA1416，`EventWaitHandle.OpenExisting` 仅 Windows），本项目仅在 Windows 运行，无影响。
