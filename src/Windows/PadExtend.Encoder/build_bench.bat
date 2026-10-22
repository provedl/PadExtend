@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /EHsc /O2 /std:c++17 bench_dll.cpp PadExtend.Encoder.lib /Fe:bench_dll.exe
if errorlevel 1 exit /b 1
echo ---RUN---
bench_dll.exe "C:\ProgramData\PadExtend\frame.bmp" 120
