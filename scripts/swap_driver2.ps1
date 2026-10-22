$log='D:\Extendscreen\PadExtend\scripts\swap_new2.log'
'== swap2 start '+(Get-Date -Format o)+' ==' | Out-File $log
pnputil /delete-driver oem292.inf /uninstall 2>&1 | Out-File $log -Append
pnputil /add-driver 'D:\Extendscreen\PadExtend\src\Driver\PadExtend.IddDriver\x64\Release\IddSampleDriver\IddSampleDriver.inf' /install 2>&1 | Out-File $log -Append
Start-Process -FilePath 'D:\Extendscreen\PadExtend\src\Driver\PadExtend.IddDriver\x64\Release\IddSampleApp.exe' -WindowStyle Hidden
'== done '+(Get-Date -Format o)+' ==' | Out-File $log -Append
