@echo off
setlocal
cd /d "%~dp0"
title Glasspane - save a version

if not exist ".git" (
    echo GitHub isn't set up for this folder yet. Run GitHub-Setup.bat first.
    pause
    exit /b 1
)

rem ---- Anything changed?
set "CHANGED="
for /f "delims=" %%c in ('git status --porcelain') do set "CHANGED=1"
if not defined CHANGED (
    echo Nothing has changed since the last saved version.
    pause
    exit /b 0
)

echo Changed files:
git status --short
echo.
set "MSG="
set /p "MSG=Describe this version (e.g. Added audio widget), then press Enter: "
if not defined MSG set "MSG=Update %date% %time:~0,5%"
set "MSG=%MSG:"='%"

git add -A
git commit -q -m "%MSG%"
if errorlevel 1 goto failed
git push -q
if errorlevel 1 goto failed

echo.
echo Saved and backed up to GitHub.
pause
exit /b 0

:failed
echo.
echo Something went wrong - the message above says what. Your files are untouched.
pause
exit /b 1
