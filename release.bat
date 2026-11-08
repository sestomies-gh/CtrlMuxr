@echo off
rem Builds dist\CtrlMuxr-<version>.zip: the minimal distribution (top folder "CtrlMuxr", no version in it).
rem Does not rebuild anything: run app\build.bat and src\proxy\build.bat first. Personal INIs and data\games are never included.
setlocal
cd /d "%~dp0"
set VER=1.15
set STAGE=dist\stage\CtrlMuxr
set ZIP=dist\CtrlMuxr-%VER%.zip

if exist dist rmdir /s /q dist
mkdir "%STAGE%\data\proxy\x64" "%STAGE%\data\proxy\x86" "%STAGE%\lang" "%STAGE%\help\images" "%STAGE%\tools" || goto fail

copy /y app\CtrlMuxr.exe "%STAGE%\" >nul || goto fail
copy /y app\devread.dll "%STAGE%\" >nul || goto fail
copy /y LICENSE "%STAGE%\" >nul || goto fail
copy /y release\README.txt "%STAGE%\" >nul || goto fail
copy /y app\lang\*.ini "%STAGE%\lang\" >nul || goto fail
copy /y app\help\*.html "%STAGE%\help\" >nul || goto fail
copy /y app\help\images\* "%STAGE%\help\images\" >nul || goto fail
for %%A in (x64 x86) do for %%D in (dinput dinput8 msacm32) do copy /y app\data\proxy\%%A\%%D.dll "%STAGE%\data\proxy\%%A\" >nul || goto fail
copy /y app\tools\di_acq_test_x64.exe "%STAGE%\tools\" >nul || goto fail
copy /y app\tools\di_acq_test_x86.exe "%STAGE%\tools\" >nul || goto fail

pushd dist\stage
tar -a -c -f "..\CtrlMuxr-%VER%.zip" CtrlMuxr || (popd & goto fail)
popd

rem cold-start check: unpack the zip to a clean folder and run the app's own UI test there
mkdir dist\test && tar -x -f "%ZIP%" -C dist\test || goto fail
mkdir dist\test\empty
start /wait "" dist\test\CtrlMuxr\CtrlMuxr.exe --uitest "%CD%\dist\test\empty" "%CD%\dist\test\uitest.txt"
powershell -NoProfile -Command "$l=(Get-Content dist\test\uitest.txt | Select-Object -Last 1); Write-Host \"uitest: $l\"; if ($l -notmatch 'ALL OK') { exit 1 }" || goto fail

echo.
echo RELEASE OK: %CD%\%ZIP%
tar -t -f "%ZIP%"
exit /b 0
:fail
echo RELEASE FAILED
exit /b 1
