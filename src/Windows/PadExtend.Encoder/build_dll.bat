@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /EHsc /std:c++17 /LD padextend_encoder.cpp /Fe:PadExtend.Encoder.dll
if errorlevel 1 exit /b 1
echo BUILD_OK
