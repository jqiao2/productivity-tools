@echo off
rem Builds BatteryBar.exe with the C# compiler bundled with Windows (.NET Framework 4.x). No installs needed.
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set MD=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /target:winexe /optimize /out:"%~dp0BatteryBar.exe" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%MD%\Windows.Foundation.winmd" /r:"%MD%\Windows.Devices.winmd" /r:"%MD%\Windows.Storage.winmd" ^
  "%~dp0BatteryBar.cs"
