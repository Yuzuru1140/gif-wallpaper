@echo off
rem Compile GIF Wallpaper + installer pakai compiler C# bawaan Windows (.NET Framework 4.x).
rem Hasil:  GifWallpaper.exe            (aplikasi)
rem         dist\GIF-Wallpaper-Setup.exe (installer berisi aplikasi + semua GIF di folder gifs\)
setlocal enabledelayedexpansion
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

%CSC% /nologo /target:winexe /optimize+ /unsafe /codepage:65001 /win32manifest:app.manifest /win32icon:app.ico ^
  /out:GifWallpaper.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll GifWallpaper.cs GifDecoder.cs Shared.cs
if errorlevel 1 (echo Build aplikasi GAGAL & exit /b 1)
echo Build OK: GifWallpaper.exe

set RES="/resource:GifWallpaper.exe,payload/GifWallpaper.exe"
for %%F in (gifs\*.gif) do set RES=!RES! "/resource:%%F,payload/gifs/%%~nxF"
if not exist dist mkdir dist
%CSC% /nologo /target:winexe /optimize+ /codepage:65001 /win32manifest:app.manifest /win32icon:app.ico ^
  /out:dist\GIF-Wallpaper-Setup.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll %RES% Setup.cs Shared.cs
if errorlevel 1 (echo Build installer GAGAL & exit /b 1)
echo Build OK: dist\GIF-Wallpaper-Setup.exe
