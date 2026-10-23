@echo off
rem Builds RazerChargerChroma.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Uses the WebView2 SDK DLLs in ..\lib and copies them next to the exe (one folder up).
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
copy /y ..\lib\*.dll .. >nul
"%FW%\csc.exe" -nologo -target:winexe -platform:x64 -optimize+ -codepage:65001 ^
  -out:..\RazerChargerChroma.exe -win32icon:app.ico ^
  -r:..\lib\Microsoft.Web.WebView2.Core.dll -r:..\lib\Microsoft.Web.WebView2.WinForms.dll ^
  -r:"%FW%\System.Runtime.dll" -r:"%FW%\System.Threading.Tasks.dll" -r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  -r:"%WM%\Windows.Foundation.winmd" -r:"%WM%\Windows.Devices.winmd" -r:"%WM%\Windows.Storage.winmd" ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Web.Extensions.dll ^
  Program.cs UsbLink.cs BleLink.cs Proto.cs
if errorlevel 1 (echo BUILD FAILED) else (echo BUILD OK)
endlocal
pause
