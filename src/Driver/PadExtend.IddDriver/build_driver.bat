@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
msbuild IddSampleDriver.sln /p:Configuration=Release /p:Platform=x64 /t:IddSampleDriver /m /nologo /v:minimal
echo EXIT=%errorlevel%
