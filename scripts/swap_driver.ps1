# swap_driver.ps1 - 卸载旧 PadExtend 驱动包，安装新构建的包（需管理员）。
$ErrorActionPreference = "Continue"
$log = "D:\Extendscreen\PadExtend\scripts\swap_new.log"
$newInf = "D:\Extendscreen\PadExtend\src\Driver\PadExtend.IddDriver\x64\Release\IddSampleDriver\IddSampleDriver.inf"
"== swap start $(Get-Date -Format o) ==" | Out-File $log
"== 卸载旧包 oem292.inf ==" | Out-File $log -Append
pnputil /delete-driver oem292.inf /uninstall /force 2>&1 | Out-File $log -Append
"== 安装新包 ==" | Out-File $log -Append
pnputil /add-driver "$newInf" /install 2>&1 | Out-File $log -Append
"== 当前 IddSample 驱动 ==" | Out-File $log -Append
pnputil /enum-drivers 2>&1 | Select-String -Pattern 'iddsampledriver' -Context 2,2 | Out-File $log -Append
"== done $(Get-Date -Format o) ==" | Out-File $log -Append
