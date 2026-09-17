@echo off
setlocal
rem Console harness for the sample model. Not shipped - this checks the thing,
rem it is not part of the thing.
rem
rem   build-sample-test.cmd [output folder]     default: %TEMP%\alive-sample-test
rem
rem ASCII only: cmd.exe reads batch files in the OEM codepage.

set OUT=%~1
if "%OUT%"=="" set OUT=%TEMP%\alive-sample-test
if not exist "%OUT%" mkdir "%OUT%"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..
set REFS=/reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll

rem Whole src except Program.cs - SampleTest brings its own Main.
set LIST=%TEMP%\alive-sample-sources.rsp
if exist "%LIST%" del "%LIST%"
for /f "delims=" %%F in ('dir /b "%ROOT%\src\*.cs" ^| findstr /v /i /x "Program.cs"') do (
  echo "%ROOT%\src\%%F">>"%LIST%"
)

"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /main:AliveTools.SampleTest ^
  /out:"%OUT%\SampleTest.exe" %REFS% @"%LIST%" "%ROOT%\tools\SampleTest.cs"
if errorlevel 1 goto fail

echo OK: %OUT%\SampleTest.exe
endlocal
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
