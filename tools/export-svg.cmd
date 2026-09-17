@echo off
setlocal
rem Shoots the program's windows into export\ - one .png, .svg and .emf per window.
rem Compiled together with the sources (src+proto+nebula, same set as build.cmd), so
rem the windows come out exactly as they are in the code right now: after UI changes,
rem just rerun this file.
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
set OUT=%ROOT%\export

"%CSC%" /nologo /target:exe /main:AbletonManager.SvgExport /platform:anycpu /codepage:65001 /out:"%TEMP%\AliveSvgExport.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "%ROOT%\src\*.cs" "%ROOT%\nebula\*.cs" "%~dp0SvgExport.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

"%TEMP%\AliveSvgExport.exe" "%OUT%"
endlocal
