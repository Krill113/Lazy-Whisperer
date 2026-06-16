@echo off
echo ====================================
echo  LWhisper - Build Script
echo ====================================
echo.

SET CONFIGURATION=%1
IF "%CONFIGURATION%"=="" SET CONFIGURATION=Release

echo Building %CONFIGURATION% configuration (self-contained, single exe + loose native runtimes)...
echo.

REM Clean previous output so no stale files remain in the shared package
IF EXIST "publish\%CONFIGURATION%\win-x64" rmdir /s /q "publish\%CONFIGURATION%\win-x64"

dotnet publish LWhisper.UI.WPF\LWhisper.UI.WPF.csproj ^
  --configuration %CONFIGURATION% ^
  --runtime win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=false ^
  -p:EnableCompressionInSingleFile=true ^
  --output publish\%CONFIGURATION%\win-x64

REM Whisper.net probes runtimes\{rid}\ itself, so its DLLs work loose. But WebRtcVadSharp uses a
REM plain DllImport("WebRtcVad.dll") which, in single-file mode, is NOT resolved from
REM runtimes\win-x64\native\. Copy it next to the exe so the streaming VAD mode keeps working.
IF EXIST "publish\%CONFIGURATION%\win-x64\runtimes\win-x64\native\WebRtcVad.dll" copy /Y "publish\%CONFIGURATION%\win-x64\runtimes\win-x64\native\WebRtcVad.dll" "publish\%CONFIGURATION%\win-x64\WebRtcVad.dll" >nul

REM Ship third-party license notices alongside the binaries (OSS attribution: BSD-3, Apache-2.0).
IF EXIST "THIRD-PARTY-NOTICES.txt" copy /Y "THIRD-PARTY-NOTICES.txt" "publish\%CONFIGURATION%\win-x64\THIRD-PARTY-NOTICES.txt" >nul

echo.
echo ====================================
echo  Build Complete!
echo  Output: publish\%CONFIGURATION%\win-x64\LWhisper.UI.WPF.exe
echo.
echo  IMPORTANT: share the WHOLE win-x64 folder (exe + runtimes\ + WebRtcVad.dll + THIRD-PARTY-NOTICES.txt),
echo  not just the .exe - the native libraries live alongside it.
echo ====================================
echo.
pause
