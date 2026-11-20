@echo off
rem Executable petit (requereix .NET 8 Desktop Runtime instal.lat). Resultat: publish-lleuger\NdiAudioRouter.exe
dotnet publish NdiAudioRouter.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o publish-lleuger
echo.
echo Fet: publish-lleuger\NdiAudioRouter.exe
pause
