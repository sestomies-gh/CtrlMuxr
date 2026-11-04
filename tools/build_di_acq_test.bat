@echo off
rem Builds tools\di_acq_test.exe for x86 and x64 (out\di_acq_test_x86.exe / out\di_acq_test_x64.exe) and copies them to ..\app\tools\ (the app's
rem "exclusive access test" runs them from there). Needs the Visual Studio Build Tools (same as src\proxy\build.bat).
cd /d "%~dp0"
if not exist out mkdir out
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars32.bat" >nul
cl /nologo /EHsc /Fo:out\ /Fe:out\di_acq_test_x86.exe di_acq_test.cpp
if errorlevel 1 exit /b 1
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
cl /nologo /EHsc /Fo:out\ /Fe:out\di_acq_test_x64.exe di_acq_test.cpp
if errorlevel 1 exit /b 1
if not exist ..\app\tools mkdir ..\app\tools
copy /y out\di_acq_test_x86.exe ..\app\tools\ >nul
copy /y out\di_acq_test_x64.exe ..\app\tools\ >nul
echo BUILD OK: %~dp0out and ..\app\tools
