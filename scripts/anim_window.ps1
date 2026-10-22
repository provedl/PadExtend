# anim_window.ps1 - 在指定坐标开一个持续重绘的窗口，制造动态画面用于帧率实测。
param([int]$X=3840, [int]$Y=0, [int]$Seconds=12)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point($X, $Y)
$form.Size = New-Object System.Drawing.Size(1200, 800)
$form.BackColor = 'Black'
$form.TopMost = $true
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$frame = 0
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 16  # ~60fps 重绘
$timer.Add_Tick({
    $script:frame++
    $g = $form.CreateGraphics()
    $hue = ($script:frame * 4) % 360
    $c = [System.Drawing.Color]::FromArgb(
        [int](128+127*[Math]::Sin($script:frame*0.1)),
        [int](128+127*[Math]::Sin($script:frame*0.1+2)),
        [int](128+127*[Math]::Sin($script:frame*0.1+4)))
    $g.Clear($c)
    $x = [int](500 + 400*[Math]::Sin($script:frame*0.15))
    $y = [int](350 + 250*[Math]::Cos($script:frame*0.15))
    $g.FillEllipse([System.Drawing.Brushes]::White, $x, $y, 120, 120)
    $g.DrawString("PadExtend DYNAMIC frame=$($script:frame)  t=$([int]$sw.ElapsedMilliseconds)ms",
        (New-Object System.Drawing.Font('Consolas',28)), [System.Drawing.Brushes]::Yellow, 30, 30)
    $g.Dispose()
    if ($sw.Elapsed.TotalSeconds -ge $Seconds) { $timer.Stop(); $form.Close() }
})
$timer.Start()
[System.Windows.Forms.Application]::Run($form)
Write-Host "animation done, drew $frame frames in $([int]$sw.ElapsedMilliseconds)ms"
