using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;

namespace NdiAudioRouter;

public partial class MainWindow : Window
{
    public ObservableCollection<RouteVM> Routes { get; } = new();
    public ObservableCollection<DeviceInfo> InputDevices { get; } = new();
    public ObservableCollection<DeviceInfo> OutputDevices { get; } = new();
    public ObservableCollection<ChannelVM> ChannelList { get; } = new();
    public List<string> AsioDrivers { get; } = new();
    public List<string> AsioMonitorOptions { get; } = new()
    {
        "Cap (monitor per WASAPI)", "Sortides 1-2", "Sortides 3-4", "Sortides 5-6", "Sortides 7-8",
        "Sortides 9-10", "Sortides 11-12", "Sortides 13-14", "Sortides 15-16"
    };
    public List<int> ToneFreqs { get; } = new() { 100, 440, 1000, 2000, 5000 };
    public int[] OutputChannels { get; } = Enumerable.Range(1, AudioEngine.Channels).ToArray();

    readonly AudioEngine _engine = new();
    readonly AppSettings _settings = AppSettings.Load();
    readonly float[] _held = new float[AudioEngine.Channels];
    readonly float[] _peakBuf = new float[AudioEngine.Channels];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly RenderTargetBitmap _rtb = new(AudioEngine.VideoW, AudioEngine.VideoH, 96, 96, PixelFormats.Pbgra32);
    readonly byte[] _pix = new byte[AudioEngine.VideoW * AudioEngine.VideoH * 4];
    DeviceInfo? _asioInfo;
    DateTime _startedAt = DateTime.Now;
    string _lastError = "";
    int _tick;
    bool _ready, _bulk, _forceClose;
    System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow()
    {
        RefreshDevices();
        try { AsioDrivers.AddRange(AsioOut.GetDriverNames()); } catch { }

        for (int c = 0; c < AudioEngine.Channels; c++)
        {
            var ch = new ChannelVM(c);
            if (c < _settings.ChannelNames.Length && !string.IsNullOrWhiteSpace(_settings.ChannelNames[c]))
                ch.Name = _settings.ChannelNames[c];
            ch.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChannelVM.Solo)) ApplyMonitor();
                else if (e.PropertyName is nameof(ChannelVM.Mute) or nameof(ChannelVM.Gen)) { ApplyChannelFlags(); UpdateAllButtons(); }
            };
            ChannelList.Add(ch);
        }

        DataContext = this;
        InitializeComponent();

        // Configuració desada
        NdiName.Text = _settings.NdiName;
        ChkV2.IsChecked = _settings.AudioV2;
        MonVol.Value = _settings.MonitorVolume;
        if (_settings.GenMode == 1) RbOsc.IsChecked = true; else RbId.IsChecked = true;
        CmbFreq.SelectedItem = ToneFreqs.Contains((int)_settings.GenFreq) ? (int)_settings.GenFreq : 1000;
        SldLevel.Value = Math.Clamp(_settings.GenLevelDb, -40, 0);
        MonDevice.SelectedItem = OutputDevices.FirstOrDefault(d => d.Id == _settings.MonitorDeviceId) ?? OutputDevices.FirstOrDefault();
        AsioDriverBox.SelectedItem = AsioDrivers.Contains(_settings.AsioDriver) ? _settings.AsioDriver : AsioDrivers.FirstOrDefault();
        AsioMonitorBox.SelectedIndex = Math.Clamp(_settings.AsioMonitorIndex, 0, AsioMonitorOptions.Count - 1);
        if (AsioDrivers.Count == 0) { AsioStatus.Text = "No s'ha detectat cap driver ASIO"; BtnAsio.IsEnabled = false; }
        else if (_settings.AsioOpen) TryOpenAsio();

        Routes.CollectionChanged += Routes_CollectionChanged;
        foreach (var rs in _settings.Routes)
        {
            var dev = InputDevices.FirstOrDefault(d => d.Id == rs.InputId);
            if (dev == null) continue;
            Routes.Add(new RouteVM { Input = dev, SourceChannel = rs.SourceChannel, OutputChannel = rs.OutputChannel, GainDb = rs.GainDb, Enabled = rs.Enabled });
        }
        if (Routes.Count == 0 && InputDevices.Count > 0) AddRoute();

        _engine.Error += msg => Dispatcher.BeginInvoke(() => _lastError = msg);
        _engine.AsioFailed += msg => Dispatcher.BeginInvoke(() => { CloseAsio(); AsioStatus.Text = msg; _lastError = msg; });
        _timer.Tick += (_, _) =>
        {
            UpdateMeters();
            if (_tick % 5 == 0 && _engine.IsRunning) RenderVideoFrame();
            if (++_tick % 12 == 0)
            {
                DiagText.Text = _engine.Diagnostics() + (_lastError.Length > 0 ? "\nÚltim error: " + _lastError : "");
                VoiceText.Text = _engine.VoiceStatus;
            }
        };
        _timer.Start();

        Application.Current.SessionEnding += (_, _) => _forceClose = true;   // tancament de Windows: no preguntis
        _ready = true;
        ApplyGenerator();
        UpdateAllButtons();
        _engine.PrepareVoices();
    }

    // ---------------- Vúmetres ----------------
    void UpdateMeters()
    {
        _engine.ReadPeaks(_peakBuf);
        for (int c = 0; c < AudioEngine.Channels; c++)
        {
            _held[c] = Math.Max(_peakBuf[c], _held[c] * 0.85f);
            double db = _held[c] > 1e-6 ? 20 * Math.Log10(_held[c]) : -60;
            ChannelList[c].SetLevel(db);
        }
    }

    // ---------------- Dispositius ----------------
    void RefreshDevices()
    {
        var inputs = DeviceCatalog.Inputs();
        if (_asioInfo != null) inputs.Add(_asioInfo);
        SyncList(InputDevices, inputs);
        SyncList(OutputDevices, DeviceCatalog.Outputs());
        if (MonDevice != null && MonDevice.SelectedItem == null) MonDevice.SelectedIndex = 0;
    }

    // Actualitza sense buidar la col·lecció (buidar-la faria que els ComboBox posessin Input = null)
    static void SyncList(ObservableCollection<DeviceInfo> col, List<DeviceInfo> fresh)
    {
        foreach (var d in col.Where(d => !fresh.Contains(d)).ToList()) col.Remove(d);
        foreach (var d in fresh) if (!col.Contains(d)) col.Add(d);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    // ---------------- ASIO ----------------
    void BtnAsio_Click(object sender, RoutedEventArgs e)
    {
        if (_asioInfo == null) TryOpenAsio(); else CloseAsio();
    }

    bool TryOpenAsio()
    {
        if (AsioDriverBox.SelectedItem is not string drv) { AsioStatus.Text = "Tria un driver"; return false; }
        int idx = AsioMonitorBox.SelectedIndex;
        int offset = idx <= 0 ? -1 : (idx - 1) * 2;
        try
        {
            int ch = _engine.OpenAsio(drv, offset);
            _asioInfo = new DeviceInfo("asio:" + drv, "ASIO: " + drv, false, ch);
            InputDevices.Add(_asioInfo);
            BtnAsio.Content = "Desconnecta";
            AsioStatus.Text = $"Connectat · {ch} entrades" + (offset >= 0 ? $" · monitor a sortides {offset + 1}-{offset + 2}" : "");
            AsioDriverBox.IsEnabled = AsioMonitorBox.IsEnabled = false;
            return true;
        }
        catch (Exception ex)
        {
            AsioStatus.Text = "Error: " + ex.Message;
            return false;
        }
    }

    void CloseAsio()
    {
        _engine.CloseAsio();
        if (_asioInfo != null) InputDevices.Remove(_asioInfo);
        _asioInfo = null;
        BtnAsio.Content = "Connecta";
        AsioStatus.Text = "Desconnectat";
        AsioDriverBox.IsEnabled = AsioMonitorBox.IsEnabled = true;
    }

    // ---------------- Rutes ----------------
    void AddRoute()
    {
        var used = Routes.Select(r => r.OutputChannel).ToHashSet();
        int next = Enumerable.Range(1, AudioEngine.Channels).FirstOrDefault(c => !used.Contains(c));
        Routes.Add(new RouteVM { Input = InputDevices.FirstOrDefault(), OutputChannel = next == 0 ? 1 : next });
    }

    void AddRoute_Click(object sender, RoutedEventArgs e) => AddRoute();

    void RemoveRoute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteVM r) Routes.Remove(r);
    }

    void Routes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (RouteVM r in e.OldItems) r.PropertyChanged -= Route_Changed;
        if (e.NewItems != null) foreach (RouteVM r in e.NewItems) r.PropertyChanged += Route_Changed;
        PushRoutes();
    }

    void Route_Changed(object? sender, PropertyChangedEventArgs e) => PushRoutes();

    void PushRoutes() =>
        _engine.SetRoutes(Routes.Where(r => r.Enabled && r.Input != null)
            .Select(r => new RouteSnapshot(r.Input!.Id, r.Input.Name, r.Input.IsLoopback, r.SourceChannel - 1, r.OutputChannel - 1, r.Gain))
            .ToArray());

    // ---------------- Emissió ----------------
    void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsRunning)
        {
            _lastError = "";
            _startedAt = DateTime.Now;
            _engine.UseAudioV2 = ChkV2.IsChecked == true;
            PushRoutes();
            ApplyMonitor();
            var status = _engine.Start(NdiName.Text.Trim());
            bool ok = status == "En marxa";
            StatusText.Text = status;
            StatusDot.Fill = ok ? new SolidColorBrush(Color.FromRgb(0x19, 0xC3, 0x7D)) : Brushes.Orange;
            BtnStart.Content = "Atura";
            NdiName.IsEnabled = false;
        }
        else
        {
            if (MessageBox.Show(this, "Vols aturar l'stream NDI?\n\nEls receptors deixaran de rebre àudio.",
                    "Atura l'stream", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _engine.Stop();
            StatusText.Text = "Aturat";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            BtnStart.Content = "Inicia";
            NdiName.IsEnabled = true;
        }
    }

    void Fmt_Changed(object sender, RoutedEventArgs e) =>
        _engine.UseAudioV2 = (sender as CheckBox)?.IsChecked == true;

    // ---------------- Monitor (SOLO) ----------------
    void ApplyMonitor()
    {
        if (MonVol == null) return;
        int mask = 0;
        foreach (var c in ChannelList) if (c.Solo) mask |= 1 << c.Index;
        _engine.SetMonitor(mask, (float)MonVol.Value);
    }

    void Monitor_VolChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyMonitor();

    void MonDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MonDevice.SelectedItem is DeviceInfo d) _engine.SetMonitorDevice(d.Id);
    }

    // ---------------- Mute i ID/OSC ----------------
    void ApplyChannelFlags()
    {
        if (_bulk) return;
        int mute = 0, gen = 0;
        foreach (var c in ChannelList)
        {
            if (c.Mute) mute |= 1 << c.Index;
            if (c.Gen) gen |= 1 << c.Index;
        }
        _engine.SetChannelFlags(mute, gen);
    }

    void UpdateAllButtons()
    {
        if (_bulk || BtnMuteAll == null || BtnGenAll == null) return;
        BtnMuteAll.IsChecked = ChannelList.All(c => c.Mute);
        BtnGenAll.IsChecked = ChannelList.All(c => c.Gen);
    }

    void MuteAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = BtnMuteAll.IsChecked == true;
        _bulk = true;
        foreach (var c in ChannelList) c.Mute = on;
        _bulk = false;
        ApplyChannelFlags();
        UpdateAllButtons();
    }

    void GenAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = BtnGenAll.IsChecked == true;
        _bulk = true;
        foreach (var c in ChannelList) c.Gen = on;
        _bulk = false;
        ApplyChannelFlags();
        UpdateAllButtons();
    }

    void ApplyGenerator()
    {
        if (!_ready) return;
        int mode = RbOsc.IsChecked == true ? 1 : 0;
        double freq = CmbFreq.SelectedItem is int f ? f : 1000;
        double level = SldLevel.Value;
        LevelText.Text = $"Nivell: {level:F0} dBFS";
        CmbFreq.IsEnabled = mode == 1;
        _engine.SetGenerator(mode, freq, level);
    }

    void GenConfig_Changed(object sender, RoutedEventArgs e) => ApplyGenerator();

    // ---------------- Imatge informativa (vídeo NDI) ----------------
    static void DrawText(DrawingContext dc, string t, double size, double x, double y, Brush b, bool bold = false, bool center = false)
    {
        var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, b, 1.0);
        dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y));
    }

    void RenderVideoFrame()
    {
        int W = AudioEngine.VideoW, H = AudioEngine.VideoH;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 18, 24)), null, new Rect(0, 0, W, H));
            DrawText(dc, "AUDIO ROUTER", 32, 24, 10, Brushes.White, true);
            DrawText(dc, NdiName.Text, 16, 26, 52, Brushes.LightGray);

            const double bw = 58, gap = 14, top = 86, hgt = 140;
            double x0 = (W - (8 * bw + 7 * gap)) / 2;
            var panel = new SolidColorBrush(Color.FromRgb(40, 44, 54));
            var gold = new Pen(Brushes.Gold, 3);
            for (int c = 0; c < AudioEngine.Channels; c++)
            {
                var ch = ChannelList[c];
                double x = x0 + c * (bw + gap);
                double frac = Math.Clamp((ch.Db + 60) / 60, 0, 1);
                dc.DrawRectangle(panel, null, new Rect(x, top, bw, hgt));
                var col = ch.Mute ? Brushes.SlateGray : ch.Db > -3 ? Brushes.Red : ch.Db > -12 ? Brushes.Orange : Brushes.LimeGreen;
                dc.DrawRectangle(col, null, new Rect(x, top + hgt * (1 - frac), bw, hgt * frac));
                if (ch.Solo) dc.DrawRectangle(null, gold, new Rect(x - 2, top - 2, bw + 4, hgt + 4));

                string nm = ch.Name.Length > 10 ? ch.Name[..10] : ch.Name;
                DrawText(dc, $"{ch.Number}", 14, x + bw / 2, top + hgt + 4, Brushes.White, true, true);
                DrawText(dc, nm, 12, x + bw / 2, top + hgt + 22, Brushes.LightGray, false, true);
                DrawText(dc, ch.Db <= -59 ? "-∞" : ch.Db.ToString("F0", CultureInfo.InvariantCulture), 11, x + bw / 2, top + hgt + 40, Brushes.Gray, false, true);
                if (ch.Gen) DrawText(dc, RbOsc.IsChecked == true ? "OSC" : "ID", 11, x + bw / 2, top + hgt + 52, Brushes.DeepSkyBlue, true, true);
                if (ch.Mute) DrawText(dc, "MUTE", 11, x + bw / 2, top + hgt + 64, Brushes.IndianRed, true, true);
            }

            var up = DateTime.Now - _startedAt;
            DrawText(dc, $"Receptors: {_engine.Connections}   NDI: {(_engine.NdiOk ? "OK" : "NO")}   Àudio: {(_engine.UseAudioV2 ? "v2" : "v3")}   Temps: {up:hh\\:mm\\:ss}",
                14, 26, 306, Brushes.White);
            var inputs = string.Join("  |  ", _engine.InputStats().Select(i => $"{(i.Name.Length > 22 ? i.Name[..22] : i.Name)}: {i.KB} KB"));
            DrawText(dc, inputs.Length > 0 ? inputs : "Cap entrada oberta", 12, 26, 330, Brushes.Gray);
        }
        _rtb.Clear();
        _rtb.Render(dv);
        _rtb.CopyPixels(_pix, W * 4, 0);
        _engine.SetVideoFrame(_pix);
    }

    // ---------------- Tancament ----------------
    void SaveSettings()
    {
        _settings.NdiName = NdiName.Text;
        _settings.ChannelNames = ChannelList.Select(c => c.Name).ToArray();
        _settings.MonitorVolume = MonVol.Value;
        _settings.AudioV2 = ChkV2.IsChecked == true;
        _settings.MonitorDeviceId = (MonDevice.SelectedItem as DeviceInfo)?.Id ?? "";
        _settings.AsioDriver = AsioDriverBox.SelectedItem as string ?? "";
        _settings.AsioOpen = _asioInfo != null;
        _settings.AsioMonitorIndex = AsioMonitorBox.SelectedIndex;
        _settings.GenMode = RbOsc.IsChecked == true ? 1 : 0;
        _settings.GenFreq = CmbFreq.SelectedItem is int gf ? gf : 1000;
        _settings.GenLevelDb = SldLevel.Value;
        _settings.Routes = Routes.Where(r => r.Input != null).Select(r => new RouteSettings
        {
            InputId = r.Input!.Id, SourceChannel = r.SourceChannel, OutputChannel = r.OutputChannel, GainDb = r.GainDb, Enabled = r.Enabled
        }).ToList();
        _settings.Save();
    }

    // ---------------- Tancament / safata ----------------
    void EnsureTray()
    {
        if (_tray != null) { _tray.Visible = true; return; }
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }

        _tray = new System.Windows.Forms.NotifyIcon { Text = "NDI Audio Router", Icon = icon, Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mostra la finestra", null, (_, _) => ShowFromTray());
        menu.Items.Add("Tanca el programa", null, (_, _) => { _forceClose = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_tray != null) _tray.Visible = false;
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_forceClose)
        {
            var r = MessageBox.Show(this,
                "Què vols fer?\n\n" +
                "Sí: tanca el programa (s'atura l'stream)\n" +
                "No: amaga la finestra a la safata (l'stream continua emetent)\n" +
                "Cancel·la: torna al programa",
                "Tanca NDI Audio Router", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.No)
            {
                e.Cancel = true;
                EnsureTray();
                Hide();
                _tray?.ShowBalloonTip(2000, "NDI Audio Router", "Continua en marxa en segon pla.", System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
        }

        _timer.Stop();
        SaveSettings();
        _engine.Shutdown();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }
}
