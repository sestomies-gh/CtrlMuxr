@echo off
rem Builds the proxy DLLs from core.cpp + an entry point.
rem Usage: build.bat [x86|x64] [dinput|dinput8|msacm32|all]   (defaults: x86 all)   -> out\<arch>\dinput.dll / dinput8.dll / msacm32.dll
set ARCH=%1
if "%ARCH%"=="" set ARCH=x86
set WHAT=%2
if "%WHAT%"=="" set WHAT=all
if "%ARCH%"=="x64" (set VC=vcvars64.bat) else (set VC=vcvars32.bat)
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\%VC%" >nul
cd /d "%~dp0"
if not exist out\%ARCH%\o1 mkdir out\%ARCH%\o1
if not exist out\%ARCH%\o8 mkdir out\%ARCH%\o8
if "%WHAT%"=="dinput" goto b1
if "%WHAT%"=="all" goto b1
goto b2
:b1
cl /nologo /LD /O2 /MT /EHsc /W3 /D_CRT_SECURE_NO_WARNINGS core.cpp dinput_entry.cpp /Fo:out\%ARCH%\o1\ /Fe:out\%ARCH%\dinput.dll /link /DEF:dinput.def user32.lib
if errorlevel 1 (echo BUILD FAILED dinput & exit /b 1)
echo BUILD OK: %~dp0out\%ARCH%\dinput.dll
:b2
if "%WHAT%"=="dinput" goto end
if "%WHAT%"=="msacm32" goto b3
cl /nologo /LD /O2 /MT /EHsc /W3 /D_CRT_SECURE_NO_WARNINGS core.cpp dinput8_entry.cpp /Fo:out\%ARCH%\o8\ /Fe:out\%ARCH%\dinput8.dll /link /DEF:dinput8.def user32.lib
if errorlevel 1 (echo BUILD FAILED dinput8 & exit /b 1)
echo BUILD OK: %~dp0out\%ARCH%\dinput8.dll
:b3
if "%WHAT%"=="dinput" goto end
if "%WHAT%"=="dinput8" goto end
if not exist out\%ARCH%\o3 mkdir out\%ARCH%\o3
cl /nologo /LD /O2 /MT /EHsc /W3 /D_CRT_SECURE_NO_WARNINGS core.cpp msacm32_entry.cpp /Fo:out\%ARCH%\o3\ /Fe:out\%ARCH%\msacm32.dll /link user32.lib
if errorlevel 1 (echo BUILD FAILED msacm32 & exit /b 1)
echo BUILD OK: %~dp0out\%ARCH%\msacm32.dll
:end
rem copy the finished DLLs where CtrlMuxr looks for them (Add game / Uninstall proxy)
if not exist "%~dp0..\..\app\data\proxy\%ARCH%" mkdir "%~dp0..\..\app\data\proxy\%ARCH%"
copy /y "%~dp0out\%ARCH%\*.dll" "%~dp0..\..\app\data\proxy\%ARCH%\" >nul
