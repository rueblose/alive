@echo off
setlocal
rem Alive + Reel in one executable.
rem
rem The whole of Alive goes in as-is: every src\*.cs except Program.cs, whose Main is
rem replaced by proto\AliveReelProgram.cs. Nothing under src\ is edited - the version
rem history is attached from the outside, see AliveReelProgram.AttachHistory.
rem
rem Because all of src is compiled here, the history window uses Alive's own Theme,
rem Chrome, Controls and RowListView rather than a look-alike of them.
rem
rem Comments in .cmd files stay ASCII on purpose: cmd.exe reads batch files in the OEM
rem codepage, and UTF-8 text turns into commands it then tries to run.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..
if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"

rem csc has no "all except one" switch, so the file list is built here. dir /b keeps
rem it to names; findstr drops Program.cs, whose Main would collide with ours.
set LIST=%TEMP%\alive-reel-sources.rsp
if exist "%LIST%" del "%LIST%"
for /f "delims=" %%F in ('dir /b "%ROOT%\src\*.cs" ^| findstr /v /i /x "Program.cs"') do (
  echo "%ROOT%\src\%%F">>"%LIST%"
)
echo "%ROOT%\proto\*.cs">>"%LIST%"
echo "%ROOT%\nebula\*.cs">>"%LIST%"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32manifest:"%ROOT%\src\app.manifest" ^
  /win32icon:"%ROOT%\src\icons\icon256.ico" ^
  /out:"%ROOT%\bin\AliveReel.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Xml.dll ^
  @"%LIST%"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo OK: %ROOT%\bin\AliveReel.exe
endlocal
