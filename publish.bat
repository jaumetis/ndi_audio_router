@echo off
rem Executable unic i autonom (no cal instal.lar .NET). Resultat: publish\NdiAudioRouter.exe
dotnet publish NdiAudioRouter.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -o publish
echo.
echo Fet: publish\NdiAudioRouter.exe
pause
