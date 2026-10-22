@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 >nul 2>&1
msbuild IddSampleDriver.sln /p:Configuration=Release /p:Platform=x64 /t:IddSampleDriver /m /nologo /v:minimal
echo EXIT=%errorlevel%
