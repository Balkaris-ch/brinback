@echo off
REM Builds dist/Brinback.exe and checks that everything agrees on the version before a release:
REM the exe (AppInfo.Version in src/Brinback.cs) and the Mac source (appVersion in
REM mac/Brinback.swift). The updater compares the running version with the release tag, so the
REM tag must be v + that version, and the exe must be attached under the name Brinback.exe.
REM Every release gets a new version: never replace the exe on a release that is already out.
setlocal
cd /d "%~dp0.."

call "%~dp0..\build.cmd"
if errorlevel 1 exit /b 1

set "V="
for /f "usebackq delims=" %%v in (`dist\Brinback.exe --version`) do set "V=%%v"
if "%V%"=="" (
  echo dist\Brinback.exe did not report a version.
  exit /b 1
)

findstr /c:"let appVersion = \"%V%\"" mac\Brinback.swift >nul
if errorlevel 1 (
  echo The exe says %V%, but mac\Brinback.swift has a different appVersion. Make them match.
  exit /b 1
)

echo.
echo Brinback %V% is ready. Commit and push, then:
echo   gh release create v%V% dist\Brinback.exe --title "Brinback %V%" --notes-file NOTES.md
endlocal
