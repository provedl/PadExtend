# PadExtend 阶段一环境检查报告

> 记录时间：2026-10-16。所有结果均为本机实测。

## 操作系统 / 架构
- Windows 11，OS 构建 10.0.26100（26200 内部），x64。

## 已就位的开发工具链

| 组件 | 版本 / 路径 | 状态 |
|------|-------------|------|
| JDK | Microsoft OpenJDK 17.0.20.1 LTS | ✓ |
| .NET SDK | 9.0.304 / 10.0.x | ✓ |
| adb | 1.0.41 (37.0.1)，`C:\Android\sdk\platform-tools` | ✓ |
| Android SDK | platforms/android-35，build-tools 34/35，cmdline-tools/latest，`C:\Android\sdk` | ✓ |
| Gradle | 8.9（wrapper） | ✓ |
| Git | 2.50.1 | ✓ |
| Visual Studio 2022 Professional | 17.14.36408.4 | ✓ |
| VS C++ 桌面工作负载 | Microsoft.VisualStudio.Workload.NativeDesktop | ✓（本阶段安装） |
| MSVC v143 | 14.44.35207（Hostx64/x64 cl.exe） | ✓ |
| Windows 11 SDK | 10.0.26100.0 | ✓ |
| WDK | Windows Driver Kit 10.0.26100.1591 | ✓（本阶段安装） |
| IddCx 头 / 库 | IddCx.h（1.0–1.10）、iddcxstub.lib（x64/arm64） | ✓ |
| WDK VS2022 扩展 | WDK.vsix（build\10.0.26100.0\WindowsDriver.*.props/targets） | ✓（本阶段安装） |

另装有 VS 2026 Professional (18.7)，但 IddCx 驱动构建统一走 VS 2022（官方 WDK 当前主支持 2022）。

## 本阶段安装的组件（按顺序）
1. VS 2022 + C++ 桌面工作负载 + Windows 11 SDK 26100（VS Installer modify，经 `--channelId VisualStudio.17.Release` 定位 2022 实例）。
2. WDK for Windows 11 24H2（官方 `wdksetup.exe`，签名校验通过，CN=Microsoft Corporation）。
3. WDK.vsix 装入 VS 2022（VSIXInstaller /admin）。

## 排错记录（真实问题）
- VS Installer `--installPath` 匹配失败：2022 实例 state.json 的 installName/installPath 字段为空，改用 `--channelId` 定位解决。
- `setup.exe` 不认 `--wait`：去掉该参数，用进程等待替代。

## Android 链路（阶段二原型已验证）
- 小米 12S Ultra（model 2203121C / thor）经 adb USB 授权为 device。
- MIUI 需开“USB 安装”解除 INSTALL_FAILED_USER_RESTRICTED。
- 接收端缺 INTERNET 权限导致 socket EPERM，已修复。
- 端到端：adb reverse + TCP，连续帧 gaps=0，文本与 JPEG 均正确解析。

## 下一步
- 拉取微软官方 IndirectDisplay 示例，原样编译（不改代码）。

## 任务2：官方 IddCx 示例编译结果（2026-10-16）

### 源码
- 仓库：microsoft/Windows-driver-samples（稀疏检出 video/IndirectDisplay）
- commit：2dc3fd3a0cc84a2933f2194e7ec0871584979071（2026-09-25）
- 本地：src/Driver/windows-driver-samples/video/IndirectDisplay
- 解决方案：IddSampleDriver.sln（驱动 IddSampleDriver + 测试 IddSampleApp）

### 工程关键配置（原样，未修改）
- 驱动类型：UMDF 用户态驱动，PlatformToolset=WindowsUserModeDriver10.0，ConfigurationType=DynamicLibrary
- 平台：x64 / ARM64（本项目用 x64）
- IddCx：编译宏 IDDCX_VERSION_MINOR=6，IDDCX_MINIMUM_VERSION_REQUIRED=4（README 旧描述写 1.4）
- INF：Class=Display，硬件 ID Root\IddSampleDriver，UMDF（Include WUDFRD.inf）

### 为编译补装的组件（经排错确认缺失）
1. Component.Microsoft.Windows.DriverKit（VS Installer，装入 WindowsUserModeDriver10.0 等工具集；裸装 WDK.vsix 被 VS 拒绝，必须走 VS Installer）
2. Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre（Spectre 缓解库，MSB8040 要求）

### 构建命令
MSBuild.exe IddSampleDriver.sln /t:Build /p:Configuration=Release /p:Platform=x64

### 产物（x64\Release\IddSampleDriver\）
- IddSampleDriver.dll（51272 B，WDKTestCert 签名）
- IddSampleDriver.inf（4132 B，DriverVer 已 stamp）
- iddsampledriver.cat（2551 B，WDKTestCert 签名）
- IddSampleApp.exe（测试用，创建/移除软件设备）
- Signability test：Errors None / Warnings None

### 已知非致命问题
- INF 验证步骤报 `无法加载 DLL x86\InfVerif.dll`（WDK 24H2 bin 下缺该 DLL）。不影响 DLL/CAT/INF 生成与签名，驱动包完整可用。安装阶段再评估是否需要补 InfVerif 工具。

### 下一步（任务3，需用户确认）
- 安装并验证虚拟显示器需要开启“测试签名模式”（bcdedit /set testsigning on，需重启）。这会改动启动配置，属高风险操作，安装前单独确认与说明恢复方式。
