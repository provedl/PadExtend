@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /EHsc /std:c++17 probe2.cpp /Fe:probe2.exe
if errorlevel 1 exit /b 1
echo ---RUN---
probe2.exe
