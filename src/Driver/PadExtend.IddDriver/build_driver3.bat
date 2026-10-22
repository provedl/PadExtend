@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 >nul 2>&1
set KIT=C:\Program Files (x86)\Windows Kits\10
set KV=10.0.26100.0
set WDKINC=%KIT%\Include\%KV%\um;%KIT%\Include\%KV%\shared;%KIT%\Include\%KV%\km;%KIT%\Include\%KV%\ucrt;%KIT%\Include\wdf\umdf\2.25;%KIT%\Include\%KV%\um\iddcx\1.6
set WDKLIB=%KIT%\Lib\%KV%\um\x64;%KIT%\Lib\%KV%\ucrt\x64;%KIT%\Lib\wdf\umdf\x64\2.25;%KIT%\Lib\%KV%\um\x64\iddcx\1.6
msbuild IddSampleDriver.sln /p:Configuration=Release /p:Platform=x64 /t:IddSampleDriver /m /nologo /v:minimal /p:IncludePath="%WDKINC%;$(IncludePath)" /p:LibraryPath="%WDKLIB%;$(LibraryPath)" 2>&1
echo EXIT=%errorlevel%
