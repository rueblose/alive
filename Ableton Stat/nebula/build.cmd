@echo off
setlocal
rem Nebula - built the same way as Alive: the C# compiler shipped inside .NET Framework 4.x.
rem The data layer and the look are pulled in AS SOURCES from ..\src, so .als parsing and the
rem theme stay identical in both programs instead of drifting apart in a copy.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..
set SHARED=
rem --- data: .als parsing, folder walking, Live plugin db, settings
set SHARED=%SHARED% "%ROOT%\src\AlsFile.cs"
set SHARED=%SHARED% "%ROOT%\src\Scales.cs"
set SHARED=%SHARED% "%ROOT%\src\ProjectIndex.cs"
set SHARED=%SHARED% "%ROOT%\src\FolderScan.cs"
set SHARED=%SHARED% "%ROOT%\src\RefResolver.cs"
set SHARED=%SHARED% "%ROOT%\src\RenderIndex.cs"
set SHARED=%SHARED% "%ROOT%\src\PluginInventory.cs"
set SHARED=%SHARED% "%ROOT%\src\LiveEnvironment.cs"
set SHARED=%SHARED% "%ROOT%\src\Settings.cs"
set SHARED=%SHARED% "%ROOT%\src\Diag.cs"
set SHARED=%SHARED% "%ROOT%\src\RowListView.cs"
set SHARED=%SHARED% "%ROOT%\src\RootsDialog.cs"
rem --- look: theme, acrylic, controls, icons
set SHARED=%SHARED% "%ROOT%\src\Theme.cs"
set SHARED=%SHARED% "%ROOT%\src\Glass.cs"
set SHARED=%SHARED% "%ROOT%\src\GlassDialog.cs"
set SHARED=%SHARED% "%ROOT%\src\Controls.cs"
set SHARED=%SHARED% "%ROOT%\src\Icons.cs"
set SHARED=%SHARED% "%ROOT%\src\Loc.cs"

if not exist "%~dp0bin" mkdir "%~dp0bin"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:"%ROOT%\src\app.manifest" /win32icon:"%ROOT%\src\icons\icon256.ico" /out:"%~dp0bin\Nebula.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "%~dp0src\*.cs" %SHARED%

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo OK: %~dp0bin\Nebula.exe
endlocal
