@echo off
setlocal
rem Снимает окна программы в export\ — по .png и .svg на окно.
rem Компилируется вместе с исходниками, поэтому окна выходят ровно те, что в коде
rem сейчас: после правок интерфейса достаточно перезапустить этот файл.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..
set OUT=%ROOT%\export

"%CSC%" /nologo /target:exe /main:AbletonManager.SvgExport /platform:anycpu /codepage:65001 /out:"%TEMP%\AliveSvgExport.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "%ROOT%\src\*.cs" "%~dp0SvgExport.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

"%TEMP%\AliveSvgExport.exe" "%OUT%"
endlocal
