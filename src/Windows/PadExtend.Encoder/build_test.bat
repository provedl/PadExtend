@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /EHsc /std:c++17 test_dll.cpp PadExtend.Encoder.lib /Fe:test_dll.exe
if errorlevel 1 exit /b 1
echo ---RUN---
test_dll.exe
