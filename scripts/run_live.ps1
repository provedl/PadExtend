# run_live.ps1 - PadExtend 真机联调一键脚本
# 前置：手机用 USB 连接并开启「USB 调试」；驱动已安装且虚拟屏在渲染内容。
param(
    [int]$Port = 27555,
    [int]$Fps = 30,
    [int]$Bitrate = 8000000,
    [switch]$Install,
    [bool]$Guard = $true
)
$ErrorActionPreference = "Stop"
$adb = "C:\Android\sdk\platform-tools\adb.exe"
$apk = "D:\Extendscreen\PadExtend\src\Android\PadExtend.Client\app\build\outputs\apk\debug\app-debug.apk"
$sender = "D:\Extendscreen\PadExtend\src\Windows\PadExtend.Sender\bin\Release\net9.0\PadExtend.Sender.exe"

Write-Host "== 1. 检查设备 =="
& $adb devices -l
$devs = (& $adb devices) | Select-String -Pattern "\tdevice$"
if (-not $devs) { Write-Host "没有检测到设备，请插手机并开启 USB 调试。" -ForegroundColor Red; exit 1 }

Write-Host "== 1b. 清理残留（避免连接错位）=="
# 杀掉上一轮残留的发送端，并强制停止 App，否则 sender 可能还卡在服务旧连接、
# 新客户端连上却收不到任何字节（历史踩坑）。
Get-Process -Name PadExtend.Sender -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  停止残留 sender PID $($_.Id)"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
# 停掉上一轮残留的 reverse 守护（命令行里含 reverse_guard.ps1 的 pwsh/powershell 进程）。
Get-CimInstance Win32_Process -Filter "Name='pwsh.exe' OR Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*reverse_guard.ps1*' } |
    ForEach-Object {
        Write-Host "  停止残留 reverse 守护 PID $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
& $adb shell am force-stop com.padextend.client 2>&1 | Out-Null
# 等端口释放（最多 ~3 秒）。
for ($i = 0; $i -lt 15; $i++) {
    $busy = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue
    if (-not $busy) { break }
    Start-Sleep -Milliseconds 200
}

if ($Install) {
    Write-Host "== 2. 安装 APK =="
    & $adb install -r $apk
}

Write-Host "== 3. 设置端口反向转发 =="
& $adb reverse tcp:$Port tcp:$Port
& $adb reverse --list

if ($Guard) {
    Write-Host "== 3b. 启动 reverse 守护（拔插后自动恢复）=="
    Start-Process -WindowStyle Hidden -FilePath "powershell" `
        -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$PSScriptRoot\reverse_guard.ps1","-Port","$Port" `
        -RedirectStandardOutput "$PSScriptRoot\reverse_guard.out.log" -RedirectStandardError "$PSScriptRoot\reverse_guard.err.log"
    Write-Host "  守护已后台运行，日志：$PSScriptRoot\reverse_guard.out.log"
}

Write-Host "== 4. 启动发送端 =="
Start-Process -WindowStyle Hidden -FilePath $sender -ArgumentList "--port=$Port","--fps=$Fps","--bitrate=$Bitrate" `
    -RedirectStandardOutput "$PSScriptRoot\live_sender.out.log" -RedirectStandardError "$PSScriptRoot\live_sender.err.log"
Start-Sleep -Seconds 1
Get-Content "$PSScriptRoot\live_sender.out.log" -ErrorAction SilentlyContinue

Write-Host "== 5. 启动 App =="
& $adb shell am start -n com.padextend.client/.MainActivity

Write-Host ""
Write-Host "完成。手机上应出现虚拟屏画面。" -ForegroundColor Green
Write-Host "查看接收端日志： $adb logcat -s PadExtendRx:I"
Write-Host "停止发送端： Get-NetTCPConnection -LocalPort $Port -State Listen | %% { Stop-Process -Id `$_.OwningProcess -Force }"
