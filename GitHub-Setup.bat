@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
title Glasspane - GitHub setup

echo ============================================================
echo  Glasspane: back up to GitHub
echo  Creates a PRIVATE repository called "Glasspane" on your
echo  GitHub account and uploads this project to it.
echo ============================================================
echo.

rem ---- 1. Tools: Git and the GitHub command-line tool
where git >nul 2>nul
if errorlevel 1 (
    echo Installing Git...
    winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements
    set "PATH=%PATH%;%ProgramFiles%\Git\cmd"
)
where gh >nul 2>nul
if errorlevel 1 (
    echo Installing GitHub CLI...
    winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements
    set "PATH=%PATH%;%ProgramFiles%\GitHub CLI"
)
where git >nul 2>nul || goto restart
where gh >nul 2>nul || goto restart

rem ---- 2. Sign in to GitHub (opens your browser the first time)
gh auth status >nul 2>nul
if errorlevel 1 (
    echo.
    echo Signing in to GitHub. A code will appear below; your browser will open
    echo so you can paste it in and approve.
    echo.
    gh auth login --hostname github.com --git-protocol https --web
    if errorlevel 1 goto failed
)
gh auth setup-git >nul 2>nul

rem ---- 3. Name and email on your saved versions (uses your GitHub details if not set)
set "GITNAME="
for /f "delims=" %%n in ('git config --global user.name 2^>nul') do set "GITNAME=%%n"
if not defined GITNAME (
    for /f "delims=" %%n in ('gh api user --jq ".login"') do git config --global user.name "%%n"
)
set "GITEMAIL="
for /f "delims=" %%e in ('git config --global user.email 2^>nul') do set "GITEMAIL=%%e"
if not defined GITEMAIL (
    set "GHID="
    set "GHLOGIN="
    for /f "delims=" %%i in ('gh api user --jq ".id"') do set "GHID=%%i"
    for /f "delims=" %%l in ('gh api user --jq ".login"') do set "GHLOGIN=%%l"
    rem GitHub's private "noreply" address, so your real email isn't published
    git config --global user.email "!GHID!+!GHLOGIN!@users.noreply.github.com"
)

rem ---- 4. Tidy up a leftover file from an undone change
if exist "Native\ScreenCapture.cs" del "Native\ScreenCapture.cs"

rem ---- 5. Start version control and save the first version
if not exist ".git" (
    git init -b main
    if errorlevel 1 goto failed
)
git add -A
git commit -q -m "Glasspane: clipboard and audio widgets, desktop mode, settings window" >nul 2>nul
echo Saved the current version.

rem ---- 6. Create the private GitHub repository and upload
git remote get-url origin >nul 2>nul
if errorlevel 1 (
    gh repo create Glasspane --private --source . --remote origin --push
    if errorlevel 1 goto failed
) else (
    git push -u origin main
    if errorlevel 1 goto failed
)

echo.
echo ============================================================
echo  Done! Your project is backed up privately on GitHub:
for /f "delims=" %%u in ('gh repo view --json url --jq ".url"') do echo  %%u
echo.
echo  From now on, double-click Save-Version.bat whenever you
echo  want to back up a new version.
echo ============================================================
pause
exit /b 0

:restart
echo.
echo Git / GitHub CLI were just installed. Close this window and
echo double-click GitHub-Setup.bat again to finish.
pause
exit /b 1

:failed
echo.
echo Something went wrong - the message above says what.
echo If it says the name "Glasspane" already exists on your GitHub,
echo rename or delete that repository and run this again.
pause
exit /b 1
