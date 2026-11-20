# NDI Audio Router (Windows)

Emet un stream NDI d'àudio de 8 canals amb enrutament lliure des de qualsevol entrada
(WASAPI o ASIO), monitor SOLO i una imatge de vídeo amb vúmetres, noms de canal i estadístiques.

## Requisits
- Windows 10/11 x64, .NET 8 SDK per compilar
- NDI Runtime / NDI Tools (variable `NDI_RUNTIME_DIR_V6/V5/V4`) o la DLL `Processing.NDI.Lib.x64.dll` al costat de l'exe

## Executable
- `publish.bat` → `publish\NdiAudioRouter.exe` (autònom, no cal instal·lar .NET)
- `publish-lleuger.bat` → executable petit (cal el .NET 8 Desktop Runtime)
Desenvolupament: `dotnet run -c Release`

## Ús
- Nom de l'stream + **Inicia**. Rutes: entrada → canal origen → canal NDI 1-8 → guany.
- **Canals**: escriu el nom a cada canal (surt a la imatge de l'stream). **SOLO** escolta el canal
  (se'n poden activar diversos; sense cap SOLO, el monitor és mut).
- **ASIO**: tria driver, opcionalment un parell de sortides per al monitor, i **Connecta**. Les entrades
  del driver apareixen a la llista d'entrades com "ASIO: <driver>" (canal origen = número d'entrada).
  El driver ASIO ha de funcionar a 48 kHz. La configuració es desa a `%AppData%\NdiAudioRouter\settings.json`.

## Novetats
- Confirmació en aturar l'stream; en tancar la finestra pregunta: tancar el programa, amagar-lo a la safata o cancel·lar.
- Cada canal té **SOLO**, **MUTE** (el SOLO segueix sonant amb MUTE) i **ID/OSC**.
- Panell ID/OSC: mode ID (veu "Channel number N", Windows TTS; els canals parlen un darrere l'altre, en ordre, sense trepitjar-se) o OSC (to d'ajust; per defecte 1 kHz a -18 dBFS). Botons MUTE TOTS i ID/OSC TOTS.
- La veu necessita una veu SAPI en anglès (en-US) instal·lada a Windows; si no, usa la veu per defecte.

## Instal·lador i Releases
- Instal·lador (Inno Setup): `installer\NdiAudioRouter.iss`. En local: executa `publish.bat` i després
  `iscc /DMyAppVersion=1.0.0 installer\NdiAudioRouter.iss` → `dist\NdiAudioRouter-Setup-1.0.0.exe`.
- Automàtic: cada cop que es puja codi a `main`, GitHub Actions (`.github/workflows/release.yml`)
  compila, genera l'instal·lador i l'executable portable i publica una Release nova (`v1.0.N`).
  També es pot forçar una versió concreta pujant una etiqueta `vX.Y.Z`.
  Les Releases surten a la dreta de la pàgina del repositori, a l'apartat *Releases*.
