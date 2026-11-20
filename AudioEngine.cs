using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NdiAudioRouter;

public sealed record RouteSnapshot(string DeviceId, string DeviceName, bool Loopback, int SourceChannel, int OutputChannel, float Gain);

internal interface IAudioSource : IDisposable
{
    int Channels { get; }
    string Name { get; }
    string Description { get; }
    long TotalBytes { get; }
    /// <summary>'frames' mostres entrellaçades (Channels per frame) a 48 kHz.</summary>
    float[] Read(int frames);
}

/// <summary>Captura WASAPI (micro/línia o loopback) convertida a float 48 kHz.</summary>
sealed class AudioInput : IAudioSource
{
    readonly WasapiCapture _cap;
    readonly BufferedWaveProvider _buf;
    readonly ISampleProvider _sp;
    readonly int _blockAlign, _srcRate;
    float[] _scratch = Array.Empty<float>();
    bool _primed;
    long _totalBytes;

    public int Channels { get; }
    public string Name { get; }
    public string Description { get; }
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public AudioInput(string id, string name, bool loopback)
    {
        Name = name;
        var dev = new MMDeviceEnumerator().GetDevice(id);
        _cap = loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, false, 20);
        var fmt = _cap.WaveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : _cap.WaveFormat;
        Channels = fmt.Channels;
        _srcRate = fmt.SampleRate;
        _blockAlign = fmt.BlockAlign;
        Description = $"{fmt.SampleRate} Hz, {fmt.Channels} can., {fmt.BitsPerSample} bit {fmt.Encoding}";
        _buf = new BufferedWaveProvider(fmt)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        ISampleProvider sp = _buf.ToSampleProvider();
        if (fmt.SampleRate != AudioEngine.Rate) sp = new WdlResamplingSampleProvider(sp, AudioEngine.Rate);
        _sp = sp;
        _cap.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;
            _buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref _totalBytes, e.BytesRecorded);
        };
        _cap.StartRecording();
    }

    public float[] Read(int frames)
    {
        int n = frames * Channels;
        if (_scratch.Length != n) _scratch = new float[n];

        AudioEngine.Trim(_buf, 120, 50);
        int need = ((int)Math.Ceiling(frames * (double)_srcRate / AudioEngine.Rate) + 2) * _blockAlign;
        int prime = _buf.WaveFormat.AverageBytesPerSecond * 40 / 1000;
        int have = _buf.BufferedBytes;

        if (!_primed) { if (have >= prime) _primed = true; }
        else if (have < need) _primed = false;

        if (!_primed) { Array.Clear(_scratch); return _scratch; }

        int got = _sp.Read(_scratch, 0, n);
        if (got < n) Array.Clear(_scratch, got, n - got);
        return _scratch;
    }

    public void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        _cap.Dispose();
    }
}

/// <summary>Buffer circular de floats, segur entre fils.</summary>
sealed class FloatRing
{
    readonly float[] _d;
    int _r, _count;
    readonly object _l = new();
    public FloatRing(int capacity) { _d = new float[Math.Max(capacity, 1024)]; }
    public int Count { get { lock (_l) return _count; } }

    public void Write(float[] src, int n)
    {
        lock (_l)
        {
            int off = 0;
            if (n > _d.Length) { off = n - _d.Length; n = _d.Length; }
            int over = _count + n - _d.Length;
            if (over > 0) { _r = (_r + over) % _d.Length; _count -= over; }
            int w = (_r + _count) % _d.Length;
            int first = Math.Min(n, _d.Length - w);
            Array.Copy(src, off, _d, w, first);
            if (n > first) Array.Copy(src, off + first, _d, 0, n - first);
            _count += n;
        }
    }

    public int Read(float[] dst, int n)
    {
        lock (_l)
        {
            n = Math.Min(n, _count);
            int first = Math.Min(n, _d.Length - _r);
            Array.Copy(_d, _r, dst, 0, first);
            if (n > first) Array.Copy(_d, 0, dst, first, n - first);
            _r = (_r + n) % _d.Length; _count -= n;
            return n;
        }
    }

    public void Skip(int n)
    {
        lock (_l) { n = Math.Min(n, _count); _r = (_r + n) % _d.Length; _count -= n; }
    }
}

/// <summary>
/// Sessió ASIO: totes les entrades del driver com a font, i opcionalment un parell de sortides
/// del mateix driver per al monitor (així monitor i captura comparteixen rellotge).
/// Cal crear-la i destruir-la des del fil de la UI (STA).
/// </summary>
sealed class AsioInput : IAudioSource
{
    readonly AsioOut _asio;
    readonly FloatRing _ring;
    float[] _tmp = Array.Empty<float>();
    float[] _scratch = Array.Empty<float>();
    bool _primed;
    long _bytes;

    public string Key { get; }
    public int Channels { get; }
    public string Name { get; }
    public string Description { get; }
    public long TotalBytes => Interlocked.Read(ref _bytes);
    public BufferedWaveProvider? MonitorBuf { get; }
    public event Action<string>? Failed;

    public AsioInput(string driver, int monitorOffset)
    {
        Key = "asio:" + driver;
        Name = "ASIO: " + driver;
        _asio = new AsioOut(driver);
        try
        {
            Channels = Math.Clamp(_asio.DriverInputChannelCount, 0, 64);
            if (Channels == 0) throw new InvalidOperationException("El driver no té entrades d'àudio.");
            _ring = new FloatRing(AudioEngine.Rate * Channels / 2);   // 500 ms

            if (monitorOffset >= 0)
            {
                if (monitorOffset + 2 > _asio.DriverOutputChannelCount)
                    throw new InvalidOperationException($"El driver només té {_asio.DriverOutputChannelCount} sortides.");
                _asio.ChannelOffset = monitorOffset;
                MonitorBuf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.Rate, 2))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(400),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                int pre = AudioEngine.Rate * 8 * 40 / 1000;
                MonitorBuf.AddSamples(new byte[pre], 0, pre);
                _asio.InitRecordAndPlayback(MonitorBuf, Channels, AudioEngine.Rate);
            }
            else
            {
                _asio.InitRecordAndPlayback(null, Channels, AudioEngine.Rate);
            }

            _asio.AudioAvailable += OnAudio;
            _asio.DriverResetRequest += (_, _) =>
                Failed?.Invoke("El driver ASIO demana reiniciar (canvi de freqüència o de buffer). Torna a connectar.");
            _asio.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) Failed?.Invoke("ASIO aturat: " + e.Exception.Message);
            };
            _asio.Play();
        }
        catch
        {
            _asio.Dispose();
            throw;
        }
        Description = $"{AudioEngine.Rate} Hz, {Channels} entrades";
    }

    void OnAudio(object? sender, AsioAudioAvailableEventArgs e)
    {
        int ch = e.InputBuffers.Length;
        int total = e.SamplesPerBuffer * ch;
        if (_tmp.Length < total) _tmp = new float[total];
        e.GetAsInterleavedSamples(_tmp);
        _ring.Write(_tmp, total);
        Interlocked.Add(ref _bytes, (long)total * sizeof(float));
    }

    public float[] Read(int frames)
    {
        int n = frames * Channels;
        if (_scratch.Length != n) _scratch = new float[n];

        int maxFrames = AudioEngine.Rate * 120 / 1000, target = AudioEngine.Rate * 50 / 1000, prime = AudioEngine.Rate * 40 / 1000;
        int have = _ring.Count / Channels;
        if (have > maxFrames) { _ring.Skip((have - target) * Channels); have = target; }

        if (!_primed) { if (have >= prime) _primed = true; }
        else if (have < frames) _primed = false;

        if (!_primed) { Array.Clear(_scratch); return _scratch; }
        int got = _ring.Read(_scratch, n);
        if (got < n) Array.Clear(_scratch, got, n - got);
        return _scratch;
    }

    public void Dispose()
    {
        try { _asio.Stop(); } catch { }
        _asio.Dispose();
    }
}

public sealed class AudioEngine : IDisposable
{
    public const int Channels = 8;
    public const int Rate = 48000;
    public const int Block = 480;          // 10 ms
    public const int VideoW = 640;
    public const int VideoH = 360;

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    readonly object _lock = new();
    readonly Dictionary<string, IAudioSource> _inputs = new();
    readonly float[] _peaks = new float[Channels];
    RouteSnapshot[] _routes = Array.Empty<RouteSnapshot>();
    Thread? _thread;
    volatile bool _run;
    IntPtr _send;
    bool _ndiOk;
    long _cycles;
    int _connections;
    readonly object _videoLock = new();
    byte[]? _videoCopy;
    AsioInput? _asio;

    // Mute i generadors (ID/OSC)
    volatile int _muteMask, _genMask, _genMode;   // _genMode: 0 = ID (veu), 1 = OSC (to)
    double _genFreq = 1000, _genLevelDb = -18;
    readonly float[] _muteGain = Enumerable.Repeat(1f, Channels).ToArray();
    readonly float[] _genGain = new float[Channels];
    long _idClock;                 // rellotge de la seqüència de veus (en mostres)
    int _idSlot = Rate * 2;        // durada d'un torn de veu (clip més llarg + pausa)
    double _tonePhase;
    float[][]? _idClips;
    string _voiceStatus = "Veu: no preparada";
    Thread? _voiceThread;

    // Monitor (mescla dels canals en SOLO, mono a L i R)
    volatile int _monMask;
    volatile float _monVol = 1f;
    string _monDeviceId = "";
    WasapiOut? _monOut;
    BufferedWaveProvider? _monBuf;
    bool _monFailed;

    public volatile bool UseAudioV2 = true;
    public bool IsRunning => _run;
    public int Connections => _connections;
    public bool NdiOk => _ndiOk;
    public event Action<string>? Error;
    public event Action<string>? AsioFailed;

    public string VoiceStatus => _voiceStatus;

    /// <summary>mute / gen: bit n = canal n.</summary>
    public void SetChannelFlags(int muteMask, int genMask) { _muteMask = muteMask; _genMask = genMask; }

    /// <summary>mode: 0 = ID (veu), 1 = OSC (to). levelDb = pic en dBFS.</summary>
    public void SetGenerator(int mode, double freqHz, double levelDb)
    {
        _genMode = mode; _genFreq = freqHz; _genLevelDb = levelDb;
    }

    /// <summary>Sintetitza "Channel number N" (N = 1..8) en un fil de fons.</summary>
    public void PrepareVoices()
    {
        if (_voiceThread != null) return;
        _voiceStatus = "Veu: generant…";
        _voiceThread = new Thread(() =>
        {
            try
            {
                using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
                string note = "";
                try
                {
                    synth.SelectVoiceByHints(System.Speech.Synthesis.VoiceGender.NotSet,
                        System.Speech.Synthesis.VoiceAge.NotSet, 0, new System.Globalization.CultureInfo("en-US"));
                }
                catch { note = " (cap veu en anglès instal·lada: pronunciació aproximada)"; }
                string voice = synth.Voice.Name;

                var clips = new float[Channels][];
                for (int c = 0; c < Channels; c++)
                {
                    using var ms = new MemoryStream();
                    synth.SetOutputToAudioStream(ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                        Rate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                    synth.Speak($"Channel number {c + 1}");
                    synth.SetOutputToNull();

                    var bytes = ms.ToArray();
                    int n = Math.Min(bytes.Length / 2, Rate * 4 - 1);      // màxim 4 s
                    var f = new float[n];
                    float peak = 0;
                    for (int i = 0; i < n; i++)
                    {
                        f[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                        peak = Math.Max(peak, Math.Abs(f[i]));
                    }
                    if (peak > 0) for (int i = 0; i < n; i++) f[i] /= peak;   // pic = 1.0 (després s'aplica el nivell)
                    int fade = Math.Min(n / 2, Rate / 50);
                    for (int i = 0; i < fade; i++) { float k = i / (float)fade; f[i] *= k; f[n - 1 - i] *= k; }
                    clips[c] = f;
                }
                int maxLen = 0;
                foreach (var cl in clips) maxLen = Math.Max(maxLen, cl.Length);
                _idSlot = Math.Max(Rate, maxLen + Rate * 3 / 10);       // pausa de 0,3 s entre canals
                _idClips = clips;
                _voiceStatus = "Veu: " + voice + note;
            }
            catch (Exception ex) { _voiceStatus = "Veu no disponible: " + ex.Message; }
        }) { IsBackground = true, Name = "TTS" };
        _voiceThread.Start();
    }

    /// <summary>Substitueix (amb fonedissa) el senyal dels canals marcats per la veu d'ID o el to d'ajust.</summary>
    void ApplyGenerators(float[] mix)
    {
        int gmask = _genMask;
        bool active = gmask != 0;
        for (int c = 0; c < Channels && !active; c++) if (_genGain[c] > 0f) active = true;
        if (!active) { _idClock = 0; return; }

        int mode = _genMode;
        double inc = 2 * Math.PI * _genFreq / Rate;
        float amp = (float)Math.Pow(10, _genLevelDb / 20.0);
        var clips = _idClips;
        int slot = _idSlot;
        int nOn = System.Numerics.BitOperations.PopCount((uint)gmask);
        long cycleLen = (long)Math.Max(1, nOn) * slot;   // les veus parlen una darrere l'altra, en ordre de canal
        if (mode != 0) _idClock = 0;

        for (int c = 0; c < Channels; c++)
        {
            float target = (gmask & (1 << c)) != 0 ? 1f : 0f;
            float g0 = _genGain[c];
            if (target == 0f && g0 == 0f) continue;
            float step = (target - g0) / Block, g = g0;
            double ph = _tonePhase;
            var clip = mode == 0 && clips != null ? clips[c] : null;
            int rank = System.Numerics.BitOperations.PopCount((uint)(gmask & ((1 << c) - 1)));   // torn d'aquest canal
            int o = c * Block;
            for (int i = 0; i < Block; i++)
            {
                g += step;
                float sv = 0f;
                if (mode == 1) { sv = (float)Math.Sin(ph) * amp; ph += inc; }
                else if (clip != null)
                {
                    long p = (_idClock + i) % cycleLen - (long)rank * slot;
                    if (p >= 0 && p < clip.Length) sv = clip[p] * amp;
                }
                mix[o + i] = mix[o + i] * (1f - g) + sv * g;
            }
            _genGain[c] = target;
        }
        _tonePhase = (_tonePhase + Block * inc) % (2 * Math.PI);
        if (mode == 0) _idClock += Block;
    }

    /// <summary>Mute dels canals de sortida NDI (el monitor SOLO ja s'ha pres abans, així que segueix sonant).</summary>
    void ApplyMute(float[] mix)
    {
        int mm = _muteMask;
        for (int c = 0; c < Channels; c++)
        {
            float target = (mm & (1 << c)) != 0 ? 0f : 1f;
            float g0 = _muteGain[c];
            if (g0 == 1f && target == 1f) continue;
            int o = c * Block;
            if (g0 == 0f && target == 0f) { Array.Clear(mix, o, Block); continue; }
            float step = (target - g0) / Block, g = g0;
            for (int i = 0; i < Block; i++) { g += step; mix[o + i] *= g; }
            _muteGain[c] = target;
        }
    }

    public void SetVideoFrame(byte[] bgra)
    {
        lock (_videoLock)
        {
            if (_videoCopy == null || _videoCopy.Length != bgra.Length) _videoCopy = new byte[bgra.Length];
            Buffer.BlockCopy(bgra, 0, _videoCopy, 0, bgra.Length);
        }
    }

    public List<(string Name, long KB)> InputStats()
    {
        lock (_lock) return _inputs.Values.Select(i => (i.Name, i.TotalBytes / 1024)).ToList();
    }

    public void ReadPeaks(float[] dest)
    {
        for (int c = 0; c < Channels; c++) { dest[c] = _peaks[c]; _peaks[c] = 0; }
    }

    internal static void Trim(BufferedWaveProvider b, int maxMs, int targetMs)
    {
        int bps = b.WaveFormat.AverageBytesPerSecond, al = b.WaveFormat.BlockAlign;
        if (b.BufferedBytes <= bps * maxMs / 1000) return;
        int drop = b.BufferedBytes - bps * targetMs / 1000;
        drop -= drop % al;
        if (drop > 0) b.Read(new byte[drop], 0, drop);
    }

    public void SetRoutes(RouteSnapshot[] routes)
    {
        lock (_lock)
        {
            _routes = routes;
            if (_run) SyncInputs();
        }
    }

    /// <summary>mask: bit n = canal n en SOLO (0 = monitor mut).</summary>
    public void SetMonitor(int mask, float volume)
    {
        _monVol = volume;
        if (_monMask != mask)
        {
            _monMask = mask;
            lock (_lock) { _monFailed = false; }
        }
    }

    public void SetMonitorDevice(string deviceId)
    {
        lock (_lock)
        {
            _monDeviceId = deviceId;
            DisposeMonitor();
            _monFailed = false;
        }
    }

    // ---------- ASIO (cridar des del fil de la UI) ----------
    /// <summary>Obre el driver ASIO. monitorOffset: primera sortida del parell de monitor (0-based) o -1 si no n'hi ha.</summary>
    public int OpenAsio(string driver, int monitorOffset)
    {
        CloseAsio();
        var inp = new AsioInput(driver, monitorOffset);
        inp.Failed += msg => AsioFailed?.Invoke(msg);
        lock (_lock) { _asio = inp; _inputs[inp.Key] = inp; }
        return inp.Channels;
    }

    public void CloseAsio()
    {
        AsioInput? a;
        lock (_lock)
        {
            a = _asio;
            if (a != null) _inputs.Remove(a.Key);
            _asio = null;
        }
        a?.Dispose();
    }

    void SyncInputs()   // cridar amb _lock agafat
    {
        var needed = _routes.Select(r => r.DeviceId).ToHashSet();
        foreach (var id in _inputs.Keys.Where(k => !k.StartsWith("asio:") && !needed.Contains(k)).ToList())
        {
            _inputs[id].Dispose();
            _inputs.Remove(id);
        }
        foreach (var r in _routes)
        {
            if (r.DeviceId.StartsWith("asio:") || _inputs.ContainsKey(r.DeviceId)) continue;
            try { _inputs[r.DeviceId] = new AudioInput(r.DeviceId, r.DeviceName, r.Loopback); }
            catch (Exception ex) { Error?.Invoke($"No es pot obrir '{r.DeviceName}': {ex.Message}"); }
        }
    }

    public string Diagnostics()
    {
        if (!_run) return "Aturat";
        var sb = new StringBuilder();
        sb.Append(_ndiOk ? "NDI: OK" : "NDI: NO (només monitor)");
        sb.Append($" | àudio {(UseAudioV2 ? "v2" : "v3 FLTP")} | receptors: {_connections}");
        sb.Append($" | cicles: {Interlocked.Read(ref _cycles)}");
        lock (_lock)
        {
            sb.Append($" | rutes: {_routes.Length} | entrades obertes: {_inputs.Count}");
            sb.Append(_asio?.MonitorBuf != null ? " | monitor: ASIO"
                : _monOut != null ? " | monitor: WASAPI"
                : _monMask != 0 ? " | monitor: NO s'ha pogut obrir" : " | monitor: sense SOLO");
            foreach (var i in _inputs.Values)
                sb.Append($"\n• {i.Name} [{i.Description}] — rebut {i.TotalBytes / 1024} KB");
        }
        return sb.ToString();
    }

    public string Start(string ndiName)
    {
        if (_run) return "";
        string status = "En marxa";
        try
        {
            _ndiOk = Ndi.NDIlib_initialize();
            if (_ndiOk)
            {
                var namePtr = Marshal.StringToCoTaskMemUTF8(string.IsNullOrWhiteSpace(ndiName) ? "Audio PC" : ndiName);
                var create = new Ndi.SendCreate { NdiName = namePtr, ClockVideo = false, ClockAudio = false };
                _send = Ndi.NDIlib_send_create(ref create);
                Marshal.FreeCoTaskMem(namePtr);
                if (_send == IntPtr.Zero) { _ndiOk = false; status = "No s'ha pogut crear l'emissor NDI (només monitor)"; }
            }
            else status = "NDI no s'ha pogut inicialitzar (només monitor)";
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _ndiOk = false;
            status = "No es troba NDI Runtime x64 (només monitor): " + ex.Message;
        }

        timeBeginPeriod(1);
        Interlocked.Exchange(ref _cycles, 0);
        lock (_lock) { _monFailed = false; SyncInputs(); }
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "NDI audio" };
        _thread.Start();
        return status;
    }

    /// <summary>Atura l'emissió. La sessió ASIO (si n'hi ha) es manté oberta.</summary>
    public void Stop()
    {
        if (!_run) return;
        _run = false;
        _thread?.Join();
        lock (_lock)
        {
            foreach (var k in _inputs.Keys.Where(k => !k.StartsWith("asio:")).ToList())
            {
                _inputs[k].Dispose();
                _inputs.Remove(k);
            }
            DisposeMonitor();
        }
        if (_send != IntPtr.Zero) { Ndi.NDIlib_send_destroy(_send); _send = IntPtr.Zero; }
        if (_ndiOk) { Ndi.NDIlib_destroy(); _ndiOk = false; }
        timeEndPeriod(1);
        Array.Clear(_peaks);
    }

    public void Shutdown() { Stop(); CloseAsio(); }

    void DisposeMonitor()   // cridar amb _lock agafat
    {
        try { _monOut?.Stop(); } catch { }
        _monOut?.Dispose();
        _monOut = null; _monBuf = null;
    }

    void EnsureMonitor()    // cridar amb _lock agafat
    {
        if (_monMask == 0) { if (_monOut != null) DisposeMonitor(); return; }
        if (_monOut != null || _monFailed) return;
        try
        {
            var en = new MMDeviceEnumerator();
            var dev = string.IsNullOrEmpty(_monDeviceId)
                ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : en.GetDevice(_monDeviceId);
            _monBuf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(400),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _monBuf.AddSamples(new byte[Rate * 8 * 40 / 1000], 0, Rate * 8 * 40 / 1000);
            _monOut = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            _monOut.Init(_monBuf);
            _monOut.Play();
        }
        catch (Exception ex)
        {
            _monFailed = true;
            DisposeMonitor();
            Error?.Invoke("Monitor: " + ex.Message);
        }
    }

    void Loop()
    {
        var mix = new float[Channels * Block];
        var monFloats = new float[Block * 2];
        var monBytes = new byte[Block * 2 * 4];
        var soloList = new int[Channels];
        var cache = new Dictionary<string, float[]>();
        IntPtr native = Marshal.AllocHGlobal(Channels * Block * sizeof(float));
        IntPtr nativeVideo = Marshal.AllocHGlobal(VideoW * VideoH * 4);
        var sw = Stopwatch.StartNew();
        long produced = 0;

        try
        {
            while (_run)
            {
                Array.Clear(mix);
                cache.Clear();

                lock (_lock)
                {
                    foreach (var r in _routes)
                    {
                        if (r.OutputChannel < 0 || r.OutputChannel >= Channels) continue;
                        if (!_inputs.TryGetValue(r.DeviceId, out var inp)) continue;
                        if (!cache.TryGetValue(r.DeviceId, out var blk)) { blk = inp.Read(Block); cache[r.DeviceId] = blk; }
                        int ch = inp.Channels, s = Math.Clamp(r.SourceChannel, 0, ch - 1), o = r.OutputChannel * Block;
                        for (int i = 0; i < Block; i++) mix[o + i] += blk[i * ch + s] * r.Gain;
                    }

                    ApplyGenerators(mix);   // ID / OSC (abans del monitor: el SOLO també els sent)

                    // Monitor: destí ASIO (si n'hi ha) o WASAPI
                    int mask = _monMask;
                    BufferedWaveProvider? mb;
                    var asioMon = _asio?.MonitorBuf;
                    if (asioMon != null) { if (_monOut != null) DisposeMonitor(); mb = asioMon; }
                    else { EnsureMonitor(); mb = _monOut != null ? _monBuf : null; }

                    if (mb != null && mask != 0)
                    {
                        int ns = 0;
                        for (int c = 0; c < Channels; c++) if ((mask & (1 << c)) != 0) soloList[ns++] = c;
                        float v = _monVol;
                        for (int i = 0; i < Block; i++)
                        {
                            float x = 0;
                            for (int k = 0; k < ns; k++) x += mix[soloList[k] * Block + i];
                            x = Math.Clamp(x * v, -1f, 1f);
                            monFloats[2 * i] = x; monFloats[2 * i + 1] = x;
                        }
                        Buffer.BlockCopy(monFloats, 0, monBytes, 0, monBytes.Length);
                        mb.AddSamples(monBytes, 0, monBytes.Length);
                        Trim(mb, 150, 60);
                    }
                }

                // Vúmetres: pic màxim acumulat fins que la UI el llegeix
                for (int c = 0; c < Channels; c++)
                {
                    float p = 0;
                    for (int i = 0; i < Block; i++) { float a = Math.Abs(mix[c * Block + i]); if (a > p) p = a; }
                    if (p > _peaks[c]) _peaks[c] = p;
                }

                ApplyMute(mix);   // després del monitor i dels vúmetres: el SOLO funciona igualment amb MUTE

                long n = Interlocked.Increment(ref _cycles);

                if (_ndiOk && _send != IntPtr.Zero)
                {
                    Marshal.Copy(mix, 0, native, mix.Length);
                    if (UseAudioV2)
                    {
                        var f2 = new Ndi.AudioFrameV2
                        {
                            SampleRate = Rate, NoChannels = Channels, NoSamples = Block,
                            Timecode = long.MaxValue, Data = native,
                            ChannelStrideInBytes = Block * sizeof(float),
                            Metadata = IntPtr.Zero, Timestamp = long.MaxValue
                        };
                        Ndi.NDIlib_send_send_audio_v2(_send, ref f2);
                    }
                    else
                    {
                        var f3 = new Ndi.AudioFrameV3
                        {
                            SampleRate = Rate, NoChannels = Channels, NoSamples = Block,
                            Timecode = long.MaxValue, FourCC = Ndi.FourCC_FLTP, Data = native,
                            ChannelStrideInBytes = Block * sizeof(float),
                            Metadata = IntPtr.Zero, Timestamp = long.MaxValue
                        };
                        Ndi.NDIlib_send_send_audio_v3(_send, ref f3);
                    }

                    if (n % 20 == 0)   // vídeo informatiu ~5 fps
                    {
                        bool have = false;
                        lock (_videoLock)
                        {
                            if (_videoCopy != null && _videoCopy.Length == VideoW * VideoH * 4)
                            { Marshal.Copy(_videoCopy, 0, nativeVideo, _videoCopy.Length); have = true; }
                        }
                        if (have)
                        {
                            var vf = new Ndi.VideoFrameV2
                            {
                                Xres = VideoW, Yres = VideoH, FourCC = Ndi.FourCC_BGRA,
                                FrameRateN = 5, FrameRateD = 1,
                                PictureAspectRatio = (float)VideoW / VideoH,
                                FrameFormatType = 1,
                                Timecode = long.MaxValue, Data = nativeVideo,
                                LineStrideInBytes = VideoW * 4,
                                Metadata = IntPtr.Zero, Timestamp = long.MaxValue
                            };
                            Ndi.NDIlib_send_send_video_v2(_send, ref vf);
                        }
                    }
                    if (n % 50 == 0) _connections = Ndi.NDIlib_send_get_no_connections(_send, 0);
                }

                // Ritme propi: un bloc de 10 ms cada 10 ms de rellotge real
                produced += Block;
                long due = produced * TimeSpan.TicksPerSecond / Rate;
                long now = sw.Elapsed.Ticks;
                if (now - due > 100 * TimeSpan.TicksPerMillisecond)
                    produced = now * Rate / TimeSpan.TicksPerSecond;
                while (_run)
                {
                    long rem = due - sw.Elapsed.Ticks;
                    if (rem <= 0) break;
                    if (rem > 2 * TimeSpan.TicksPerMillisecond) Thread.Sleep(1); else Thread.SpinWait(100);
                }
            }
        }
        catch (Exception ex) { Error?.Invoke("Motor d'àudio: " + ex.Message); }
        finally { Marshal.FreeHGlobal(native); Marshal.FreeHGlobal(nativeVideo); }
    }

    public void Dispose() => Shutdown();
}
