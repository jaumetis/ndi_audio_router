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
    bool _ready, _bulk, _forceClose, _suppressMon;
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripItem? _trayShow, _trayClose;

    // Textos d'estat que es tornen a generar en canviar d'idioma
    Func<string> _statusFn = () => Loc.T("StatusStopped");
    Func<string> _asioStatusFn = () => Loc.T("AsioDisconnected");

    static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static SolidColorBrush Rgb(byte r, byte g, byte b) => Fz(new SolidColorBrush(Color.FromRgb(r, g, b)));
    static readonly Brush OkBrush = Rgb(0x22, 0xC5, 0x5E);
    static readonly Brush IdleBrush = Rgb(0x6B, 0x72, 0x80);
    static readonly Brush WarnBrush = Rgb(0xF5, 0x9E, 0x0B);

    public MainWindow()
    {
        Loc.SetLanguage(_settings.Language ?? Loc.DefaultFromSystem());
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
        if (Loc.IsEnglish) LangEn.IsChecked = true; else LangCa.IsChecked = true;
        NdiName.Text = _settings.NdiName;
        ChkV2.IsChecked = _settings.AudioV2;
        MonVol.Value = _settings.MonitorVolume;
        if (_settings.GenMode == 1) RbOsc.IsChecked = true; else RbId.IsChecked = true;
        CmbFreq.SelectedItem = ToneFreqs.Contains((int)_settings.GenFreq) ? (int)_settings.GenFreq : 1000;
        SldLevel.Value = Math.Clamp(_settings.GenLevelDb, -40, 0);
        MonDevice.SelectedItem = OutputDevices.FirstOrDefault(d => d.Id == _settings.MonitorDeviceId) ?? OutputDevices.FirstOrDefault();
        AsioDriverBox.SelectedItem = AsioDrivers.Contains(_settings.AsioDriver) ? _settings.AsioDriver : AsioDrivers.FirstOrDefault();
        AsioMonitorBox.ItemsSource = BuildAsioOptions();
        AsioMonitorBox.SelectedIndex = Math.Clamp(_settings.AsioMonitorIndex, 0, 8);
        if (AsioDrivers.Count == 0) { _asioStatusFn = () => Loc.T("AsioNone"); BtnAsio.IsEnabled = false; }
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
        _engine.AsioFailed += msg => Dispatcher.BeginInvoke(() => { CloseAsio(); _asioStatusFn = () => msg; AsioStatus.Text = msg; _lastError = msg; });
        _timer.Tick += (_, _) =>
        {
            UpdateMeters();
            if (_tick % 5 == 0 && _engine.IsRunning) RenderVideoFrame();
            if (++_tick % 12 == 0)
            {
                DiagText.Text = _engine.Diagnostics() + (_lastError.Length > 0 ? Loc.F("DiagLastError", _lastError) : "");
                VoiceText.Text = _engine.VoiceStatus;
            }
        };
        _timer.Start();

        Application.Current.SessionEnding += (_, _) => _forceClose = true;   // tancament de Windows: no preguntis
        _ready = true;
        ApplyGenerator();
        UpdateAllButtons();
        _engine.PrepareVoices();
        RefreshTexts();
    }

    // ---------------- Idioma ----------------
    static List<string> BuildAsioOptions()
    {
        var l = new List<string> { Loc.T("AsioMonNone") };
        for (int p = 1; p <= 15; p += 2) l.Add(Loc.F("AsioMonPair", p, p + 1));
        return l;
    }

    void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyLanguage(sender == LangEn ? "en" : "ca");
    }

    void ApplyLanguage(string lang)
    {
        Loc.SetLanguage(lang);                       // refresca tots els textos del XAML
        _settings.Language = lang;
        foreach (var c in ChannelList) c.RefreshLanguage();

        int asioIdx = AsioMonitorBox.SelectedIndex;
        AsioMonitorBox.ItemsSource = BuildAsioOptions();
        AsioMonitorBox.SelectedIndex = Math.Clamp(asioIdx, 0, 8);

        // El "Dispositiu per defecte" de la llista de monitor canvia de text: es refà la llista sense tocar el monitor
        _suppressMon = true;
        var selId = (MonDevice.SelectedItem as DeviceInfo)?.Id ?? "";
        RefreshDevices();
        MonDevice.SelectedItem = OutputDevices.FirstOrDefault(d => d.Id == selId) ?? OutputDevices.FirstOrDefault();
        _suppressMon = false;

        RefreshTexts();
        SaveSettings();
    }

    /// <summary>Textos que s'assignen des de codi (botons, estats, safata…).</summary>
    void RefreshTexts()
    {
        BtnStart.Content = Loc.T(_engine.IsRunning ? "Stop" : "Start");
        BtnAsio.Content = Loc.T(_asioInfo != null ? "AsioDisconnect" : "AsioConnect");
        StatusText.Text = _statusFn();
        AsioStatus.Text = _asioStatusFn();
        LevelText.Text = Loc.F("GenLevel", SldLevel.Value.ToString("F0", CultureInfo.CurrentCulture));
        VoiceText.Text = _engine.VoiceStatus;
        if (_trayShow != null) _trayShow.Text = Loc.T("TrayShow");
        if (_trayClose != null) _trayClose.Text = Loc.T("TrayClose");
    }

    void SetStatus(Func<string> f, Brush dot)
    {
        _statusFn = f;
        StatusText.Text = f();
        StatusDot.Fill = dot;
    }

    void SetAsioStatus(Func<string> f)
    {
        _asioStatusFn = f;
        AsioStatus.Text = f();
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

    // Actualitza la col·lecció sense buidar-la (buidar-la faria que els ComboBox posessin Input = null) i en manté l'ordre
    static void SyncList(ObservableCollection<DeviceInfo> col, List<DeviceInfo> fresh)
    {
        foreach (var d in col.Where(d => !fresh.Contains(d)).ToList()) col.Remove(d);
        for (int i = 0; i < fresh.Count; i++)
        {
            if (i < col.Count && col[i].Equals(fresh[i])) continue;
            int j = col.IndexOf(fresh[i]);
            if (j >= 0) col.Move(j, i); else col.Insert(i, fresh[i]);
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    // ---------------- ASIO ----------------
    void BtnAsio_Click(object sender, RoutedEventArgs e)
    {
        if (_asioInfo == null) TryOpenAsio(); else CloseAsio();
    }

    bool TryOpenAsio()
    {
        if (AsioDriverBox.SelectedItem is not string drv) { SetAsioStatus(() => Loc.T("AsioPick")); return false; }
        int idx = AsioMonitorBox.SelectedIndex;
        int offset = idx <= 0 ? -1 : (idx - 1) * 2;
        try
        {
            int ch = _engine.OpenAsio(drv, offset);
            _asioInfo = new DeviceInfo("asio:" + drv, "ASIO: " + drv, false, ch);
            InputDevices.Add(_asioInfo);
            BtnAsio.Content = Loc.T("AsioDisconnect");
            SetAsioStatus(() => Loc.F("AsioConnected", ch) + (offset >= 0 ? Loc.F("AsioConnectedMon", offset + 1, offset + 2) : ""));
            AsioDriverBox.IsEnabled = AsioMonitorBox.IsEnabled = false;
            return true;
        }
        catch (Exception ex)
        {
            string m = ex.Message;
            SetAsioStatus(() => Loc.F("AsioError", m));
            return false;
        }
    }

    void CloseAsio()
    {
        _engine.CloseAsio();
        if (_asioInfo != null) InputDevices.Remove(_asioInfo);
        _asioInfo = null;
        BtnAsio.Content = Loc.T("AsioConnect");
        SetAsioStatus(() => Loc.T("AsioDisconnected"));
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
            string key = _engine.Start(NdiName.Text.Trim());
            string detail = _engine.StartDetail;
            SetStatus(() => Loc.T(key) + (detail.Length > 0 ? ": " + detail : ""), key == "StatusRunning" ? OkBrush : WarnBrush);
            BtnStart.Style = (Style)FindResource("DangerButton");
            BtnStart.Content = Loc.T("Stop");
            NdiName.IsEnabled = false;
        }
        else
        {
            int r = PromptWindow.Ask(this, Loc.T("StopConfirmTitle"), Loc.T("StopConfirmText"),
                new[] { Loc.T("BtnStopStream"), Loc.T("BtnCancel") }, danger: 0, defaultIdx: 1);
            if (r != 0) return;
            _engine.Stop();
            SetStatus(() => Loc.T("StatusStopped"), IdleBrush);
            BtnStart.Style = (Style)FindResource("PrimaryButton");
            BtnStart.Content = Loc.T("Start");
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
        if (_suppressMon) return;
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
        LevelText.Text = Loc.F("GenLevel", level.ToString("F0", CultureInfo.CurrentCulture));
        CmbFreq.IsEnabled = mode == 1;
        _engine.SetGenerator(mode, freq, level);
    }

    void GenConfig_Changed(object sender, RoutedEventArgs e) => ApplyGenerator();

    // ---------------- Imatge de l'stream (vídeo NDI) ----------------
    static readonly Brush VBg = Rgb(0x0E, 0x10, 0x14);
    static readonly Brush VTrack = Rgb(0x1B, 0x1F, 0x29);
    static readonly Brush VText = Rgb(0xEE, 0xF0, 0xF5);
    static readonly Brush VDim = Rgb(0xB3, 0xBA, 0xC8);
    static readonly Brush VFaint = Rgb(0x8A, 0x92, 0xA3);
    static readonly Brush VAmber = Rgb(0xFF, 0xB8, 0x1C);
    static readonly Brush VSky = Rgb(0x38, 0xBD, 0xF8);
    static readonly Brush VDanger = Rgb(0xEF, 0x4B, 0x53);
    static readonly Pen VSoloPen = Fz(new Pen(VAmber, 2));
    static readonly FontFamily VFont = new("Segoe UI Variable Text, Segoe UI");

    static void DrawText(DrawingContext dc, string t, double size, double x, double y, Brush b, bool bold = false, bool center = false)
    {
        var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(VFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, 1.0);
        dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y));
    }

    static void DrawBar(DrawingContext dc, double x, double y, double w, double h, double level, double peak, bool dim)
    {
        var track = new Rect(x, y, w, h);
        double r = w / 2;
        dc.DrawRoundedRectangle(VTrack, null, track, r, r);
        if (level > 0.002)
        {
            double fy = y + h * (1 - level);
            if (dim) dc.PushOpacity(0.3);
            dc.PushClip(new RectangleGeometry(track, r, r));
            dc.PushClip(new RectangleGeometry(new Rect(x, fy, w, y + h - fy)));
            dc.DrawRectangle(MeterBar.FillBrush, null, track);
            dc.Pop();
            dc.Pop();
            if (dim) dc.Pop();
        }
        if (peak > 0.002)
        {
            double py = y + h * (1 - peak);
            dc.DrawRoundedRectangle(peak > 0.95 ? VDanger : VText, null, new Rect(x - 2, py - 1, w + 4, 2), 1, 1);
        }
    }

    void RenderVideoFrame()
    {
        int W = AudioEngine.VideoW, H = AudioEngine.VideoH;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(VBg, null, new Rect(0, 0, W, H));
            DrawText(dc, "AUDIO ROUTER", 26, 30, 12, VText, true);
            DrawText(dc, NdiName.Text, 15, 32, 48, VDim);

            const double colW = 72, top = 86, hgt = 130, barW = 12;
            double x0 = (W - AudioEngine.Channels * colW) / 2;
            for (int c = 0; c < AudioEngine.Channels; c++)
            {
                var ch = ChannelList[c];
                double cx = x0 + c * colW + colW / 2;
                DrawBar(dc, cx - barW / 2, top, barW, hgt, ch.Level, ch.Peak, ch.Mute);
                if (ch.Solo) dc.DrawRoundedRectangle(null, VSoloPen, new Rect(cx - barW / 2 - 7, top - 7, barW + 14, hgt + 14), 9, 9);

                string nm = ch.ShownName.Length > 10 ? ch.ShownName[..10] : ch.ShownName;
                DrawText(dc, ch.Number.ToString(CultureInfo.InvariantCulture), 15, cx, top + hgt + 12, VText, true, true);
                DrawText(dc, nm, 12.5, cx, top + hgt + 32, VDim, false, true);
                DrawText(dc, ch.Db <= -59 ? "-∞" : ch.Db.ToString("F0", CultureInfo.InvariantCulture), 11, cx, top + hgt + 50, VFaint, false, true);
                if (ch.Gen) DrawText(dc, RbOsc.IsChecked == true ? "OSC" : "ID", 11, cx, top + hgt + 65, VSky, true, true);
                if (ch.Mute) DrawText(dc, "MUTE", 11, cx, top + hgt + (ch.Gen ? 80 : 65), VDanger, true, true);
            }

            var up = DateTime.Now - _startedAt;
            DrawText(dc, Loc.F("VidStats", _engine.Connections, _engine.NdiOk ? "OK" : "NO", _engine.UseAudioV2 ? "v2" : "v3",
                up.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)), 13, 30, 316, VText);
            var inputs = string.Join("  |  ", _engine.InputStats().Select(i => $"{(i.Name.Length > 22 ? i.Name[..22] : i.Name)}: {i.KB} KB"));
            DrawText(dc, inputs.Length > 0 ? inputs : Loc.T("VidNoInput"), 11.5, 30, 338, VFaint);
        }
        _rtb.Clear();
        _rtb.Render(dv);
        _rtb.CopyPixels(_pix, W * 4, 0);
        _engine.SetVideoFrame(_pix);
    }

    // ---------------- Configuració ----------------
    void SaveSettings()
    {
        _settings.Language = Loc.Lang;
        _settings.NdiName = NdiName.Text;
        _settings.ChannelNames = ChannelList.Select(c => c.CustomName).ToArray();
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
        _trayShow = menu.Items.Add(Loc.T("TrayShow"), null, (_, _) => ShowFromTray());
        _trayClose = menu.Items.Add(Loc.T("TrayClose"), null, (_, _) => { _forceClose = true; Close(); });
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
            int r = PromptWindow.Ask(this, Loc.T("CloseTitle"), Loc.T("CloseText"),
                new[] { Loc.T("BtnCloseProgram"), Loc.T("BtnHideTray"), Loc.T("BtnCancel") },
                danger: 0, accent: 1, defaultIdx: 1);
            if (r == 2) { e.Cancel = true; return; }                 // Cancel·la
            if (r == 1)                                              // Amaga a la safata
            {
                e.Cancel = true;
                EnsureTray();
                Hide();
                _tray?.ShowBalloonTip(2000, "NDI Audio Router", Loc.T("TrayBalloon"), System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
        }

        _timer.Stop();
        SaveSettings();
        _engine.Shutdown();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }
}
