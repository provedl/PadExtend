@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /EHsc /std:c++17 enc_file.cpp /Fe:enc_file.exe
if errorlevel 1 exit /b 1
echo ---RUN---
enc_file.exe "C:\ProgramData\PadExtend\frame.bmp" out.h264 30
