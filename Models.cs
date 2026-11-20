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
        var list = new List<DeviceInfo> { new("", "Dispositiu per defecte", false, 2) };
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

/// <summary>Canal de sortida NDI (1..8): nom editable, SOLO i nivell.</summary>
public class ChannelVM : INotifyPropertyChanged
{
    public const double MeterHeight = 150;
    string _name;
    bool _solo, _mute, _gen;
    double _mask = MeterHeight;
    string _dbText = "-∞";

    public ChannelVM(int index) { Index = index; _name = $"Canal {index + 1}"; }

    public int Index { get; }
    public int Number => Index + 1;
    public double Db { get; private set; } = -60;

    public string Name { get => _name; set { _name = value ?? ""; Raise(); } }
    public bool Solo { get => _solo; set { if (_solo == value) return; _solo = value; Raise(); } }
    public bool Mute { get => _mute; set { if (_mute == value) return; _mute = value; Raise(); } }
    public bool Gen { get => _gen; set { if (_gen == value) return; _gen = value; Raise(); } }
    public double MaskHeight { get => _mask; private set { if (Math.Abs(_mask - value) < 0.3) return; _mask = value; Raise(); } }
    public string DbText { get => _dbText; private set { if (_dbText == value) return; _dbText = value; Raise(); } }

    public void SetLevel(double db)
    {
        Db = db;
        MaskHeight = MeterHeight * (1 - Math.Clamp((db + 60) / 60, 0, 1));
        DbText = db <= -59 ? "-∞" : db.ToString("F0", CultureInfo.InvariantCulture) + " dB";
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
