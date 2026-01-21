@echo off
echo ====================================
echo  LWhisper - Build Script
echo ====================================
echo.

SET CONFIGURATION=%1
IF "%CONFIGURATION%"=="" SET CONFIGURATION=Release

echo Building %CONFIGURATION% configuration...
echo.

dotnet publish LWhisper.UI.WPF\LWhisper.UI.WPF.csproj ^
  --configuration %CONFIGURATION% ^
  --runtime win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  --output publish\%CONFIGURATION%\win-x64

echo.
echo ====================================
echo  Build Complete!
echo  Output: publish\%CONFIGURATION%\win-x64\LWhisper.UI.WPF.exe
echo ====================================
echo.
pause


