@echo off
rem Builds the 64-bit devread.dll (GUI helper) into ..\..\app\devread.dll
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /LD /O2 /MT /EHsc /W3 /D_CRT_SECURE_NO_WARNINGS devread.cpp /Fo:out\ /Fe:out\devread.dll /link user32.lib
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
copy /y out\devread.dll ..\..\app\devread.dll >nul
echo BUILD OK: ..\..\app\devread.dll
