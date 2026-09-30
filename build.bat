@echo off
rem Rebuild FreebuffController.exe (output lands in this folder as
rem FreebuffController.exe; rename it to whatever display name you like).
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
rem The session-handover script is embedded into the exe as Base64, so refresh
rem it before compiling (needs python3).
python "%~dp0tools\embed-handover.py" || (
  echo EMBED FAILED ^(needs python3^)
  exit /b 1
)
"%CSC%" -nologo -target:winexe -platform:anycpu -optimize+ -codepage:65001 ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Management.dll ^
  -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll ^
  -win32icon:"%~dp0app.ico" -out:"%~dp0FreebuffController.exe" "%~dp0FreebuffController.cs"
rem ...and propagate the exit code so CI smoke builds actually fail on error.
if %errorlevel%==0 (echo BUILD OK) else (
  echo BUILD FAILED
  exit /b 1
)
