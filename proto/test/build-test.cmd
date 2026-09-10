@echo off
setlocal
rem Development tools for the prototype. They are not placed into bin - these are for
rem checking the thing, not for shipping it.
rem
rem   build-test.cmd [output folder]      default: %TEMP%\reel-test
rem
rem   ReelTest.exe  "project folder" "store folder"
rem       runs the whole engine into the console: import, history, diff of every
rem       adjacent pair, parsed model of the newest snapshot, dependency report.
rem
rem   Shot.exe  "app.exe" "out.png" [args]
rem       starts an app, waits for it to settle, saves its window to PNG.
rem
rem ASCII only: cmd.exe reads batch files in the OEM codepage, and angle brackets in a
rem rem-line are still parsed as redirection.

set OUT=%~1
if "%OUT%"=="" set OUT=%TEMP%\reel-test
if not exist "%OUT%" mkdir "%OUT%"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..\..
set REFS=/reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll

rem Same source list as the app: the history window is built on Alive's own controls,
rem so the console harness needs all of src too. proto\Program.cs is dropped - ProtoTest
rem brings its own Main. (src\Program.cs is gone since the two builds became one; the
rem filter over src stays harmless and guards against a Main reappearing there.)
set LIST=%TEMP%\reel-test-sources.rsp
if exist "%LIST%" del "%LIST%"
for /f "delims=" %%F in ('dir /b "%ROOT%\src\*.cs" ^| findstr /v /i /x "Program.cs"') do (
  echo "%ROOT%\src\%%F">>"%LIST%"
)
for /f "delims=" %%F in ('dir /b "%ROOT%\proto\*.cs" ^| findstr /v /i /x "Program.cs"') do (
  echo "%ROOT%\proto\%%F">>"%LIST%"
)

"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /main:Reel.ProtoTest ^
  /out:"%OUT%\ReelTest.exe" %REFS% @"%LIST%" "%ROOT%\proto\test\ProtoTest.cs"
if errorlevel 1 goto fail

"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /main:Reel.Shot ^
  /out:"%OUT%\Shot.exe" /reference:System.dll /reference:System.Drawing.dll ^
  "%ROOT%\proto\test\Shot.cs"
if errorlevel 1 goto fail

echo OK: %OUT%\ReelTest.exe, %OUT%\Shot.exe
endlocal
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
