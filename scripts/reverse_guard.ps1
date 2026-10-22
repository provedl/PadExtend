# reverse_guard.ps1 - adb reverse 守护
# 拔插 USB 会清掉 adb reverse 规则，导致手机连不上 127.0.0.1:<Port> 而黑屏重试。
# 本守护轮询检查，发现设备在线但缺少 reverse 规则时自动补上，实现拔插后自动恢复。
param(
    [int]$Port = 27555,
    [int]$IntervalMs = 1000
)
$ErrorActionPreference = "SilentlyContinue"
$adb = "C:\Android\sdk\platform-tools\adb.exe"
$rule = "tcp:$Port"

Write-Host "[reverse-guard] 守护启动：Port=$Port 间隔=${IntervalMs}ms。Ctrl+C 退出。"
$lastOnline = $null
while ($true) {
    # 设备是否在线（state == device）。
    $online = $false
    foreach ($line in (& $adb devices)) {
        if ($line -match "\tdevice$") { $online = $true; break }
    }

    if ($online) {
        $list = (& $adb reverse --list) -join "`n"
        if ($list -notmatch [regex]::Escape($rule)) {
            & $adb reverse $rule $rule | Out-Null
            Write-Host ("[reverse-guard] {0:HH:mm:ss} 设备在线但缺 reverse，已重建 {1}" -f (Get-Date), $rule)
        }
        if ($lastOnline -eq $false) {
            Write-Host ("[reverse-guard] {0:HH:mm:ss} 设备重新上线" -f (Get-Date))
        }
    }
    elseif ($lastOnline -ne $false) {
        Write-Host ("[reverse-guard] {0:HH:mm:ss} 设备离线，等待插回…" -f (Get-Date))
    }

    $lastOnline = $online
    Start-Sleep -Milliseconds $IntervalMs
}
