using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveSub.Services;

public sealed record RenderDevice(string Id, string Name, bool IsDefault = false, bool HasAudio = false)
{
    public override string ToString() => Name;
}

public readonly record struct AudioCaptureStatus(
    string DeviceName,
    long TotalBytes,
    float Peak,
    TimeSpan SinceLastData);

public sealed class AudioCaptureService : IDisposable
{
    public event Action<float[]>? SegmentReady;
    public event Action<float>? LevelChanged;
    public event Action<string>? Error;
    public event Action<AudioCaptureStatus>? StatusChanged;

    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private WdlResamplingSampleProvider? _resampler;
    private CancellationTokenSource? _cts;
    private Task? _processTask;
    private Timer? _statusTimer;
    private long _packets;
    private long _totalBytes;
    private long _lastDataTicks;
    private float _peak;

    public string? ActiveDeviceName { get; private set; }

    public static IReadOnlyList<RenderDevice> GetRenderDevices()
    {
        var result = new List<RenderDevice>
        {
            new("", "系统默认（跟随当前输出）", true),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = defaultDevice.ID;
            }
            catch (Exception ex)
            {
                AppPaths.Log("Default render endpoint unavailable: " + ex.Message);
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var name = device.FriendlyName;
                    var isDefault = string.Equals(device.ID, defaultId, StringComparison.Ordinal);
                    var hasAudio = HasActiveSessions(device);
                    if (isDefault) name += " · 默认";
                    if (hasAudio) name += " · 正在播放";
                    result.Add(new RenderDevice(device.ID, name, isDefault, hasAudio));
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Enumerate audio devices failed: " + ex);
        }

        return result;
    }

    private static bool HasActiveSessions(MMDevice device)
    {
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateActive) return true;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Audio session probe failed for {device.FriendlyName}: {ex.Message}");
        }
        return false;
    }

    public void Start(string? deviceId)
    {
        Stop();

        var enumerator = new MMDeviceEnumerator();
        try
        {
            _device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
        }
        finally
        {
            enumerator.Dispose();
        }

        ActiveDeviceName = _device.FriendlyName;
        _capture = new WasapiLoopbackCapture(_device);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        AppPaths.Log($"Loopback endpoint: {_device.FriendlyName}; state={_device.State}; format={_capture.WaveFormat}");

        ISampleProvider samples = _buffer.ToSampleProvider();
        if (_capture.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
        }
        else if (_capture.WaveFormat.Channels > 2)
        {
            var downmix = new MultiplexingSampleProvider(new[] { samples }, 1);
            downmix.ConnectInputToOutput(0, 0);
            samples = downmix;
        }

        _resampler = new WdlResamplingSampleProvider(samples, 16000);
        _cts = new CancellationTokenSource();
        _packets = 0;
        _totalBytes = 0;
        _lastDataTicks = Environment.TickCount64;
        _peak = 0;
        _capture.StartRecording();
        _processTask = Task.Run(() => ProcessLoop(_cts.Token));
        _statusTimer = new Timer(_ => ReportStatus(), null, 1000, 1000);
        AppPaths.Log($"Capture started: {_device.FriendlyName} / {_capture.WaveFormat}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        try
        {
            Interlocked.Add(ref _totalBytes, e.BytesRecorded);
            Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
            if (Interlocked.Increment(ref _packets) % 200 == 0)
                AppPaths.Log($"Audio packets={_packets} totalBytes={Interlocked.Read(ref _totalBytes)} lastBytes={e.BytesRecorded}");
            _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Audio buffer add failed: " + ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AppPaths.Log("Capture stopped with error: " + e.Exception);
            Error?.Invoke(e.Exception.Message);
        }
    }

    private void ReportStatus()
    {
        try
        {
            var total = Interlocked.Read(ref _totalBytes);
            var last = Interlocked.Read(ref _lastDataTicks);
            var since = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - last));
            var peak = Volatile.Read(ref _peak);
            Volatile.Write(ref _peak, 0);
            StatusChanged?.Invoke(new AudioCaptureStatus(ActiveDeviceName ?? "", total, peak, since));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Audio status report failed: " + ex.Message);
        }
    }

    private void ProcessLoop(CancellationToken ct)
    {
        var readBuffer = new float[1600];
        var pending = new List<float>(4096);
        var silenceFrame = new float[Segmenter.FrameSamples];
        var segmenter = new Segmenter();
        var levelTick = 0;
        var idleFlushed = false;
        var lastProcessed = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = _resampler?.Read(readBuffer, 0, readBuffer.Length) ?? 0;
            }
            catch (Exception ex)
            {
                AppPaths.Log("Audio read failed: " + ex);
                Error?.Invoke(ex.Message);
                return;
            }

            if (read <= 0)
            {
                if (segmenter.IsSpeaking && !idleFlushed &&
                    Environment.TickCount64 - lastProcessed >= 650)
                {
                    idleFlushed = true;
                    AppPaths.Log("Audio idle: flushing pending segment");
                    for (var i = 0; i < 24; i++)
                    {
                        var segment = segmenter.Add(silenceFrame);
                        if (segment != null) SegmentReady?.Invoke(segment);
                    }
                }
                Thread.Sleep(12);
                continue;
            }

            idleFlushed = false;
            lastProcessed = Environment.TickCount64;

            double sum = 0;
            float peak = 0;
            for (var i = 0; i < read; i++)
            {
                var value = readBuffer[i];
                sum += value * value;
                var absolute = Math.Abs(value);
                if (absolute > peak) peak = absolute;
            }
            if (peak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, peak);

            var level = (float)Math.Sqrt(sum / read);
            if (++levelTick >= 4)
            {
                levelTick = 0;
                LevelChanged?.Invoke(Math.Clamp(level * 12f, 0f, 1f));
                if (level > 0.01f) AppPaths.Log($"Audio level={level:0.0000}");
            }

            for (var i = 0; i < read; i++) pending.Add(readBuffer[i]);

            var offset = 0;
            while (pending.Count - offset >= Segmenter.FrameSamples)
            {
                var frame = new float[Segmenter.FrameSamples];
                pending.CopyTo(offset, frame, 0, Segmenter.FrameSamples);
                offset += Segmenter.FrameSamples;
                var segment = segmenter.Add(frame);
                if (segment != null) SegmentReady?.Invoke(segment);
            }
            if (offset > 0) pending.RemoveRange(0, offset);
        }
    }

    public void Stop()
    {
        try { _statusTimer?.Dispose(); } catch { }
        _statusTimer = null;
        try { _cts?.Cancel(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        try { _processTask?.Wait(1500); } catch { }
        _processTask = null;
        _cts?.Dispose();
        _cts = null;

        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        try { _device?.Dispose(); } catch { }
        _device = null;
        ActiveDeviceName = null;
        _buffer = null;
        _resampler = null;
    }

    public void Dispose() => Stop();

    private sealed class Segmenter
    {
        public const int FrameSamples = 480; // 30 ms at 16 kHz
        private const int PreRollFrames = 8;
        private const int MinSpeechFrames = 10;
        private const int SilenceFramesToEnd = 18; // 540 ms
        private const int MaxFrames = 333;         // about 10 s

        private readonly List<float> _current = new();
        private readonly Queue<float[]> _preRoll = new();
        private bool _speaking;
        private int _speechFrames;
        private int _silenceFrames;
        private float _noise = 0.0015f;

        public bool IsSpeaking => _speaking;

        public float[]? Add(float[] frame)
        {
            double sum = 0;
            for (var i = 0; i < frame.Length; i++) sum += frame[i] * frame[i];
            var rms = (float)Math.Sqrt(sum / frame.Length);

            if (!_speaking)
            {
                _noise = rms < _noise ? _noise * 0.97f + rms * 0.03f : _noise * 0.999f + rms * 0.001f;
            }
            var voiced = rms > Math.Max(0.003f, _noise * 2.6f);

            if (!_speaking)
            {
                if (!voiced)
                {
                    _preRoll.Enqueue(frame);
                    while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                    return null;
                }

                _speaking = true;
                _speechFrames = 1;
                _silenceFrames = 0;
                foreach (var pre in _preRoll) Append(pre);
                _preRoll.Clear();
                Append(frame);
                return null;
            }

            Append(frame);
            if (voiced)
            {
                _speechFrames++;
                _silenceFrames = 0;
            }
            else
            {
                _silenceFrames++;
            }

            if (_silenceFrames < SilenceFramesToEnd && _current.Count / FrameSamples < MaxFrames) return null;
            return Finish();
        }

        private void Append(float[] frame) => _current.AddRange(frame);

        private float[]? Finish()
        {
            var result = _speechFrames >= MinSpeechFrames ? _current.ToArray() : null;
            AppPaths.Log($"VAD finish: speechFrames={_speechFrames} samples={_current.Count} emitted={result != null}");
            _current.Clear();
            _speaking = false;
            _speechFrames = 0;
            _silenceFrames = 0;
            _preRoll.Clear();
            return result;
        }
    }
}
