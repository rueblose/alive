@echo off
setlocal
rem Build with the C# compiler shipped inside .NET Framework 4.x - nothing to install.
rem
rem One executable. src is the catalog, proto adds the version history (Forks) and the
rem library scatter plot (Stat), nebula adds its own view. Main lives in proto\Program.cs
rem and is the only one in the tree - see the comment there for why the extra windows
rem attach from outside instead of being wired into MainForm.
rem
rem Comments in .cmd files stay ASCII on purpose: cmd.exe reads batch files in the OEM
rem codepage, and UTF-8 text turns into commands it then tries to run.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

if not exist "%~dp0bin" mkdir "%~dp0bin"

echo Building Alive.exe...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32manifest:"%~dp0src\app.manifest" ^
  /win32icon:"%~dp0src\icons\icon256.ico" ^
  /out:"%~dp0bin\Alive.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Xml.dll ^
  "%~dp0src\*.cs" "%~dp0proto\*.cs" "%~dp0nebula\*.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED: Alive.exe
  exit /b 1
)
echo OK: %~dp0bin\Alive.exe

endlocal
