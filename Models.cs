using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace NdiAudioRouter;

public record DeviceInfo(string Id, string Name, bool IsLoopback, int Channels)
{
    public override string ToString() => Name;
}

public static class DeviceCatalog
{
    public static List<DeviceInfo> Inputs()
    {
        var list = new List<DeviceInfo>();
        var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) Add(list, d, false, "");
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) Add(list, d, true, "Loopback: ");
        return list;
    }

    public static List<DeviceInfo> Outputs()
    {
        var list = new List<DeviceInfo> { new("", Loc.T("DefaultDevice"), false, 2) };
        var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) Add(list, d, false, "");
        return list;
    }

    static void Add(List<DeviceInfo> list, MMDevice d, bool loop, string prefix)
    {
        try { list.Add(new DeviceInfo(d.ID, prefix + d.FriendlyName, loop, d.AudioClient.MixFormat.Channels)); }
        catch { /* dispositiu no disponible */ }
    }
}

/// <summary>Una ruta: canal d'una entrada -> canal (1..8) de l'stream NDI.</summary>
public class RouteVM : INotifyPropertyChanged
{
    DeviceInfo? _input;
    int _src = 1, _out = 1;
    double _gainDb;
    bool _enabled = true;

    public DeviceInfo? Input
    {
        get => _input;
        set
        {
            if (value == null) return;   // el ComboBox escriu null quan es refresca la llista: ho ignorem
            _input = value;
            if (_src > _input.Channels) _src = 1;
            Raise(); Raise(nameof(SourceChannels)); Raise(nameof(SourceChannel));
        }
    }
    public IEnumerable<int> SourceChannels => Enumerable.Range(1, Math.Max(1, _input?.Channels ?? 1));
    public int SourceChannel { get => _src; set { _src = value; Raise(); } }
    public int OutputChannel { get => _out; set { _out = value; Raise(); } }
    public double GainDb { get => _gainDb; set { _gainDb = value; Raise(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(); } }
    public float Gain => _gainDb <= -60 ? 0f : (float)Math.Pow(10, _gainDb / 20.0);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Canal de sortida NDI (1..8): nom editable, SOLO / MUTE / ID-OSC i nivell amb retenció de pic.</summary>
public class ChannelVM : INotifyPropertyChanged
{
    string? _custom;                       // null = nom per defecte ("Canal N" / "Channel N", segons l'idioma)
    bool _solo, _mute, _gen;
    double _level, _peak, _hold;
    string _peakText = "-∞";

    public ChannelVM(int index) { Index = index; }

    public int Index { get; }
    public int Number => Index + 1;
    public double Db { get; private set; } = -60;

    public string DefaultName => Loc.F("ChannelDefault", Number);

    /// <summary>Text editable. Buit = es fa servir el nom per defecte.</summary>
    public string Name
    {
        get => _custom ?? DefaultName;
        set
        {
            value ??= "";
            if (value == _custom) return;
            // Si l'usuari deixa el nom per defecte (en qualsevol idioma), seguirà l'idioma de la interfície
            _custom = value == $"Canal {Number}" || value == $"Channel {Number}" ? null : value;
            Raise();
            Raise(nameof(ShownName));
        }
    }

    /// <summary>Nom que es mostra a la imatge de l'stream (mai buit).</summary>
    public string ShownName => string.IsNullOrWhiteSpace(_custom) ? DefaultName : _custom!;

    /// <summary>Per desar a la configuració: només si s'ha personalitzat.</summary>
    public string CustomName => _custom ?? "";

    public bool Solo { get => _solo; set { if (_solo == value) return; _solo = value; Raise(); } }
    public bool Mute { get => _mute; set { if (_mute == value) return; _mute = value; Raise(); } }
    public bool Gen { get => _gen; set { if (_gen == value) return; _gen = value; Raise(); } }

    /// <summary>Nivell actual normalitzat (0 = -60 dB, 1 = 0 dB).</summary>
    public double Level { get => _level; private set { if (Math.Abs(_level - value) < 0.002) return; _level = value; Raise(); } }
    /// <summary>Pic amb retenció normalitzat.</summary>
    public double Peak { get; private set; }
    public string PeakText { get => _peakText; private set { if (_peakText == value) return; _peakText = value; Raise(); } }

    public void RefreshLanguage() { Raise(nameof(Name)); Raise(nameof(ShownName)); }

    /// <summary>Es crida cada 40 ms amb el nivell actual en dBFS.</summary>
    public void SetLevel(double db)
    {
        Db = db;
        double n = Math.Clamp((db + 60) / 60, 0, 1);
        Level = n;

        if (n >= _peak) { _peak = n; _hold = 1.2; }                       // retén el pic 1,2 s
        else if (_hold > 0) _hold -= 0.04;
        else _peak = Math.Max(n, _peak - 0.04 * (24.0 / 60.0));           // després cau a 24 dB/s

        if (Math.Abs(Peak - _peak) >= 0.002) { Peak = _peak; Raise(nameof(Peak)); }
        PeakText = _peak <= 0.002 ? "-∞" : (_peak * 60 - 60).ToString("F1", CultureInfo.CurrentCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class RouteSettings
{
    public string InputId { get; set; } = "";
    public int SourceChannel { get; set; } = 1;
    public int OutputChannel { get; set; } = 1;
    public double GainDb { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Configuració persistent a %AppData%\NdiAudioRouter\settings.json</summary>
public class AppSettings
{
    public string NdiName { get; set; } = "Audio PC";
    public string? Language { get; set; }          // "ca" / "en" (null = segons Windows)
    public string[] ChannelNames { get; set; } = Array.Empty<string>();
    public double MonitorVolume { get; set; } = 1;
    public bool AudioV2 { get; set; } = true;
    public string MonitorDeviceId { get; set; } = "";
    public string AsioDriver { get; set; } = "";
    public bool AsioOpen { get; set; }
    public int AsioMonitorIndex { get; set; }
    public int GenMode { get; set; }              // 0 = ID (veu), 1 = OSC (to)
    public double GenFreq { get; set; } = 1000;
    public double GenLevelDb { get; set; } = -18;
    public List<RouteSettings> Routes { get; set; } = new();

    static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NdiAudioRouter", "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
