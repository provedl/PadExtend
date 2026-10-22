'== swap3 '+(Get-Date -Format o)+' ==' | Out-File 'D:\Extendscreen\PadExtend\scripts\swap3.log'
pnputil /delete-driver oem292.inf /uninstall 2>&1 | Out-File 'D:\Extendscreen\PadExtend\scripts\swap3.log' -Append
pnputil /add-driver 'D:\Extendscreen\PadExtend\src\Driver\PadExtend.IddDriver\x64\Release\IddSampleDriver\IddSampleDriver.inf' /install 2>&1 | Out-File 'D:\Extendscreen\PadExtend\scripts\swap3.log' -Append
Start-Process -FilePath 'D:\Extendscreen\PadExtend\src\Driver\PadExtend.IddDriver\x64\Release\IddSampleApp.exe' -WindowStyle Hidden
'== done ==' | Out-File 'D:\Extendscreen\PadExtend\scripts\swap3.log' -Append
