using System.ComponentModel;
using System.Globalization;

namespace NdiAudioRouter;

/// <summary>
/// Traduccions (català / anglès). Des de XAML: {Binding [Clau], Source={x:Static local:Loc.I}}
/// Des de codi: Loc.T("Clau") o Loc.F("Clau", arguments). En canviar d'idioma tota la interfície s'actualitza sola.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();
    public static string Lang { get; private set; } = "ca";
    public static bool IsEnglish => Lang == "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => T(key);

    /// <summary>"ca" o "en". Qualsevol altre valor = català.</summary>
    public static void SetLanguage(string? lang)
    {
        Lang = lang == "en" ? "en" : "ca";
        I.PropertyChanged?.Invoke(I, new PropertyChangedEventArgs("Item[]"));   // refresca tots els {Binding [Clau]}
    }

    /// <summary>Idioma inicial si l'usuari encara no n'ha triat cap: català si Windows és en català o castellà, si no anglès.</summary>
    public static string DefaultFromSystem()
    {
        var c = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return c is "ca" or "es" ? "ca" : "en";
    }

    public static string T(string key) =>
        D.TryGetValue(key, out var v) ? (Lang == "en" ? v.en : v.ca) : key;

    public static string F(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    static readonly Dictionary<string, (string ca, string en)> D = new()
    {
        // ---- Capçalera ----
        ["AppTitle"] = ("NDI Audio Router", "NDI Audio Router"),
        ["AppSubtitle"] = ("8 canals · enrutament lliure · WASAPI i ASIO", "8 channels · free routing · WASAPI & ASIO"),
        ["StreamName"] = ("Nom de l'stream NDI", "NDI stream name"),
        ["Start"] = ("Inicia", "Start"),
        ["Stop"] = ("Atura", "Stop"),
        ["NdiV2"] = ("Àudio NDI v2 (compatibilitat)", "NDI audio v2 (compatibility)"),
        ["LanguageTip"] = ("Idioma de la interfície", "Interface language"),
        ["StatusStopped"] = ("Aturat", "Stopped"),
        ["StatusRunning"] = ("En marxa", "Running"),
        ["StatusNdiInit"] = ("NDI no s'ha pogut inicialitzar (només monitor)", "NDI could not be initialized (monitor only)"),
        ["StatusNdiCreate"] = ("No s'ha pogut crear l'emissor NDI (només monitor)", "Could not create the NDI sender (monitor only)"),
        ["StatusNdiMissing"] = ("No es troba NDI Runtime x64 (només monitor)", "NDI Runtime x64 not found (monitor only)"),

        // ---- ASIO ----
        ["AsioTitle"] = ("DRIVER ASIO", "ASIO DRIVER"),
        ["AsioDriver"] = ("Driver", "Driver"),
        ["AsioMonitorOut"] = ("Sortida de monitor ASIO", "ASIO monitor output"),
        ["AsioConnect"] = ("Connecta", "Connect"),
        ["AsioDisconnect"] = ("Desconnecta", "Disconnect"),
        ["AsioDisconnected"] = ("Desconnectat", "Disconnected"),
        ["AsioNone"] = ("No s'ha detectat cap driver ASIO", "No ASIO driver detected"),
        ["AsioPick"] = ("Tria un driver", "Choose a driver"),
        ["AsioConnected"] = ("Connectat · {0} entrades", "Connected · {0} inputs"),
        ["AsioConnectedMon"] = (" · monitor a sortides {0}-{1}", " · monitor on outputs {0}-{1}"),
        ["AsioError"] = ("Error: {0}", "Error: {0}"),
        ["AsioMonNone"] = ("Cap (monitor per WASAPI)", "None (monitor via WASAPI)"),
        ["AsioMonPair"] = ("Sortides {0}-{1}", "Outputs {0}-{1}"),
        ["AsioNoInputs"] = ("El driver no té entrades d'àudio.", "The driver has no audio inputs."),
        ["AsioFewOutputs"] = ("El driver només té {0} sortides.", "The driver only has {0} outputs."),
        ["AsioReset"] = ("El driver ASIO demana reiniciar (canvi de freqüència o de buffer). Torna a connectar.",
                         "The ASIO driver requests a reset (sample rate or buffer change). Reconnect."),
        ["AsioStopped"] = ("ASIO aturat: {0}", "ASIO stopped: {0}"),
        ["AsioInputDesc"] = ("{0} Hz, {1} entrades", "{0} Hz, {1} inputs"),

        // ---- Enrutament ----
        ["RoutesTitle"] = ("ENRUTAMENT · ENTRADA → CANAL NDI", "ROUTING · INPUT → NDI CHANNEL"),
        ["AddRoute"] = ("+ Afegeix ruta", "+ Add route"),
        ["RefreshDevices"] = ("Actualitza dispositius", "Refresh devices"),
        ["ColActive"] = ("Actiu", "On"),
        ["ColInput"] = ("Entrada", "Input"),
        ["ColSrcCh"] = ("Canal origen", "Source ch."),
        ["ColNdiCh"] = ("Canal NDI", "NDI channel"),
        ["ColGain"] = ("Guany (dB)", "Gain (dB)"),

        // ---- Canals ----
        ["ChannelsTitle"] = ("SORTIDA NDI · 8 CANALS", "NDI OUTPUT · 8 CHANNELS"),
        ["ChannelsHint"] = ("Els noms de canal també surten a la imatge de l'stream", "Channel names also appear in the stream picture"),
        ["ChannelDefault"] = ("Canal {0}", "Channel {0}"),
        ["NamePlaceholder"] = ("Nom", "Name"),
        ["Solo"] = ("SOLO", "SOLO"),
        ["Mute"] = ("MUTE", "MUTE"),
        ["IdOsc"] = ("ID/OSC", "ID/OSC"),
        ["GenTitle"] = ("ID / OSC", "ID / OSC"),
        ["GenId"] = ("ID · veu «Channel number N»", "ID · voice “Channel number N”"),
        ["GenOsc"] = ("OSC · to d'ajust", "OSC · alignment tone"),
        ["GenFreq"] = ("Freqüència del to (Hz)", "Tone frequency (Hz)"),
        ["GenLevel"] = ("Nivell: {0} dBFS", "Level: {0} dBFS"),
        ["MuteAll"] = ("MUTE TOTS", "MUTE ALL"),
        ["GenAll"] = ("ID/OSC TOTS", "ID/OSC ALL"),
        ["VoiceNotReady"] = ("Veu: no preparada", "Voice: not ready"),
        ["VoiceGenerating"] = ("Veu: generant…", "Voice: generating…"),
        ["VoiceReady"] = ("Veu: {0}", "Voice: {0}"),
        ["VoiceNoEnglish"] = (" (cap veu en anglès instal·lada: pronunciació aproximada)", " (no English voice installed: approximate pronunciation)"),
        ["VoiceFail"] = ("Veu no disponible: {0}", "Voice unavailable: {0}"),

        // ---- Monitor i diagnòstic ----
        ["MonitorTitle"] = ("MONITOR I DIAGNÒSTIC", "MONITOR & DIAGNOSTICS"),
        ["MonDevice"] = ("Sortida de monitor (WASAPI, si no s'usa ASIO)", "Monitor output (WASAPI, when not using ASIO)"),
        ["MonVolume"] = ("Volum de monitor", "Monitor volume"),
        ["DefaultDevice"] = ("Dispositiu per defecte", "Default device"),
        ["DiagStopped"] = ("Aturat", "Stopped"),
        ["DiagNdiOk"] = ("NDI: OK", "NDI: OK"),
        ["DiagNdiNo"] = ("NDI: NO (només monitor)", "NDI: NO (monitor only)"),
        ["DiagAudioRecv"] = (" | àudio {0} | receptors: {1}", " | audio {0} | receivers: {1}"),
        ["DiagCycles"] = (" | cicles: {0}", " | cycles: {0}"),
        ["DiagRoutes"] = (" | rutes: {0} | entrades obertes: {1}", " | routes: {0} | open inputs: {1}"),
        ["DiagMonAsio"] = (" | monitor: ASIO", " | monitor: ASIO"),
        ["DiagMonWasapi"] = (" | monitor: WASAPI", " | monitor: WASAPI"),
        ["DiagMonFail"] = (" | monitor: NO s'ha pogut obrir", " | monitor: could not open"),
        ["DiagMonNoSolo"] = (" | monitor: sense SOLO", " | monitor: no SOLO"),
        ["DiagInput"] = ("\n• {0} [{1}] — rebut {2} KB", "\n• {0} [{1}] — received {2} KB"),
        ["DiagLastError"] = ("\nÚltim error: {0}", "\nLast error: {0}"),
        ["InputDesc"] = ("{0} Hz, {1} can., {2} bit {3}", "{0} Hz, {1} ch., {2} bit {3}"),
        ["ErrOpenInput"] = ("No es pot obrir '{0}': {1}", "Cannot open '{0}': {1}"),
        ["ErrMonitor"] = ("Monitor: {0}", "Monitor: {0}"),
        ["ErrEngine"] = ("Motor d'àudio: {0}", "Audio engine: {0}"),

        // ---- Diàlegs i safata ----
        ["StopConfirmTitle"] = ("Atura l'stream", "Stop the stream"),
        ["StopConfirmText"] = ("Vols aturar l'stream NDI?\n\nEls receptors deixaran de rebre àudio.",
                               "Stop the NDI stream?\n\nReceivers will stop getting audio."),
        ["CloseTitle"] = ("Tanca NDI Audio Router", "Close NDI Audio Router"),
        ["CloseText"] = ("Què vols fer?\n\nTancar el programa atura l'stream.\nAmagar-lo a la safata deixa l'stream emetent.",
                         "What do you want to do?\n\nClosing the program stops the stream.\nHiding it to the tray keeps the stream running."),
        ["BtnCloseProgram"] = ("Tanca el programa", "Close program"),
        ["BtnHideTray"] = ("Amaga a la safata", "Hide to tray"),
        ["BtnCancel"] = ("Cancel·la", "Cancel"),
        ["BtnStopStream"] = ("Atura l'stream", "Stop stream"),
        ["TrayShow"] = ("Mostra la finestra", "Show window"),
        ["TrayClose"] = ("Tanca el programa", "Close program"),
        ["TrayBalloon"] = ("Continua en marxa en segon pla.", "Still running in the background."),

        // ---- Imatge de l'stream (vídeo NDI) ----
        ["VidStats"] = ("Receptors: {0}   NDI: {1}   Àudio: {2}   Temps: {3}", "Receivers: {0}   NDI: {1}   Audio: {2}   Time: {3}"),
        ["VidNoInput"] = ("Cap entrada oberta", "No input open"),
    };
}
