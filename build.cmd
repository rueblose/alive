@echo off
setlocal
rem Build with the C# compiler shipped inside .NET Framework 4.x - nothing to install.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

if not exist "%~dp0bin" mkdir "%~dp0bin"

echo Building Alive.exe...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:"%~dp0src\app.manifest" /win32icon:"%~dp0src\icons\icon256.ico" /out:"%~dp0bin\Alive.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "%~dp0src\*.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED: Alive.exe
  exit /b 1
)
echo OK: %~dp0bin\Alive.exe

if exist "%~dp0proto\build-proto.cmd" (
  echo.
  echo Building AliveReel.exe...
  call "%~dp0proto\build-proto.cmd"
  if errorlevel 1 (
    echo.
    echo BUILD FAILED: AliveReel.exe
    exit /b 1
  )
)

endlocal
