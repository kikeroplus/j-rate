@echo off
rem Build J-Rate with the built-in .NET Framework 4 compiler (no SDK needed)
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /out:J-Rate.exe ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  /r:Microsoft.CSharp.dll ^
  /resource:MainWindow.xaml,MainWindow.xaml ^
  /resource:ExportDialog.xaml,ExportDialog.xaml ^
  /resource:GridWindow.xaml,GridWindow.xaml ^
  /resource:J-Rate.ico,J-Rate.ico ^
  /win32icon:J-Rate.ico ^
  JRate.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: J-Rate.exe
