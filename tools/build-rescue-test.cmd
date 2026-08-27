@echo off
setlocal
rem Console harness for the project rescue helper. Not shipped - this checks the thing,
rem it is not part of the thing.
rem
rem   build-rescue-test.cmd [output folder]     default: %TEMP%\alive-rescue-test
rem
rem   RescueTest.exe logs                every broken document load in every Live log
rem   RescueTest.exe log   "set.als"     what Live's log remembers about one set
rem   RescueTest.exe patch "set.als"     disable every plugin in a copy and diff the XML
rem
rem ASCII only: cmd.exe reads batch files in the OEM codepage.

set OUT=%~1
if "%OUT%"=="" set OUT=%TEMP%\alive-rescue-test
if not exist "%OUT%" mkdir "%OUT%"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..
set REFS=/reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll

rem Whole src except Program.cs - RescueTest brings its own Main, and the parsing it
rem checks is the very same code the app ships.
set LIST=%TEMP%\alive-rescue-sources.rsp
if exist "%LIST%" del "%LIST%"
for /f "delims=" %%F in ('dir /b "%ROOT%\src\*.cs" ^| findstr /v /i /x "Program.cs"') do (
  echo "%ROOT%\src\%%F">>"%LIST%"
)

"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /main:AliveTools.RescueTest ^
  /out:"%OUT%\RescueTest.exe" %REFS% @"%LIST%" "%ROOT%\tools\RescueTest.cs"
if errorlevel 1 goto fail

rem RescueShow just opens the window so Shot.exe can photograph it - winexe, no console.
"%CSC%" /nologo /target:winexe /platform:anycpu /codepage:65001 /main:AliveTools.RescueShow ^
  /out:"%OUT%\RescueShow.exe" %REFS% @"%LIST%" "%ROOT%\tools\RescueShow.cs"
if errorlevel 1 goto fail

rem DialogShow opens Settings / Options the same way, for the same reason.
"%CSC%" /nologo /target:winexe /platform:anycpu /codepage:65001 /main:AliveTools.DialogShow ^
  /out:"%OUT%\DialogShow.exe" %REFS% @"%LIST%" "%ROOT%\tools\DialogShow.cs"
if errorlevel 1 goto fail

rem Shot itself lives with the Reel prototype - same job, no reason for a second copy.
"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /main:Reel.Shot ^
  /out:"%OUT%\Shot.exe" /reference:System.dll /reference:System.Drawing.dll ^
  "%ROOT%\proto\test\Shot.cs"
if errorlevel 1 goto fail

echo OK: %OUT%\RescueTest.exe, %OUT%\RescueShow.exe, %OUT%\DialogShow.exe, %OUT%\Shot.exe
endlocal
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
