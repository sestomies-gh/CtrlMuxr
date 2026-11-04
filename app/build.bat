@echo off
rem Builds CtrlMuxr.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK or NuGet packages needed.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:..\brand\CtrlMuxr.ico /out:CtrlMuxr.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll CtrlMuxr.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK: %~dp0CtrlMuxr.exe
