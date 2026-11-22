@echo off
REM Builds dist/Brinback.exe with the C# compiler that ships with Windows (.NET Framework 4).
REM Nothing to install. The UI page is embedded into the exe, so the exe is the whole program.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%/Microsoft.NET/Framework/v4.0.30319/csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 C# compiler. It ships with Windows 10 and 11.
  exit /b 1
)

if not exist "assets/app.ico" powershell -NoProfile -ExecutionPolicy Bypass -File "tools/make-icon.ps1"
if not exist "obj/Interop.UIAutomationClient.dll" powershell -NoProfile -ExecutionPolicy Bypass -File "tools/make-interop.ps1"
if not exist "obj/Interop.UIAutomationClient.dll" (
  echo Could not generate the UI Automation interop from Windows' own type library.
  exit /b 1
)
if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:dist\Brinback.exe ^
  /win32icon:assets\app.ico ^
  /resource:src\ui\index.html,Brinback.index.html ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /link:obj\Interop.UIAutomationClient.dll ^
  src\Brinback.cs
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)
echo Built dist/Brinback.exe
endlocal
