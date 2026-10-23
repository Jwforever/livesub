using System.Diagnostics;
using System.Threading.Channels;

namespace LiveSub.Services;

public sealed class SubtitlePipeline : IAsyncDisposable
{
    public event Action<string, string, string>? CaptionChanged;
    public event Action<string, string>? TranslationPartial;
    public event Action<float>? LevelChanged;
    public event Action<string>? Error;
    public event Action<AudioCaptureStatus>? AudioStatusChanged;

    private AudioCaptureService? _audio;
    private WindowsCaptionsService? _captions;
    private WhisperAsrService? _asr;
    private TranslationService? _translator;
    private Channel<float[]>? _segments;
    private Channel<(string Text, long QueuedAt)>? _captionText;
    private CancellationTokenSource? _cts;
    private Task? _processTask;

    public bool TranslationReady { get; private set; }
    public string? ActiveDeviceName => _audio?.ActiveDeviceName;
    public bool IsCaptionsWindowShown => _captions?.IsWindowShownForUser ?? false;

    public void ShowCaptionsWindow() => _captions?.ShowWindowForUser();
    public void HideCaptionsWindow() => _captions?.HideWindowForUser();

    public async Task StartAsync(
        string? deviceId,
        string sourceLanguage = "en",
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        await StopAsync();

        status?.Report("正在加载识别模型…");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _asr = await Task.Run(
            () => new WhisperAsrService(AppPaths.WhisperModelPath, sourceLanguage), _cts.Token);
        _translator = new TranslationService { SourceLanguage = sourceLanguage };
        try
        {
            status?.Report("正在加载本地翻译模型…");
            await _translator.LoadAsync(AppPaths.TranslationModelPath, _cts.Token);
            TranslationReady = true;
        }
        catch (Exception ex)
        {
            TranslationReady = false;
            AppPaths.Log("Translation load failed: " + ex);
            status?.Report("翻译模型未加载，先显示原文");
        }

        _segments = Channel.CreateBounded<float[]>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _audio = new AudioCaptureService();
        _audio.SegmentReady += OnSegment;
        _audio.LevelChanged += level => LevelChanged?.Invoke(level);
        _audio.Error += message => Error?.Invoke(message);
        _audio.StatusChanged += status => AudioStatusChanged?.Invoke(status);
        _audio.Start(deviceId);

        _processTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        status?.Report($"正在聆听：{_audio.ActiveDeviceName ?? "系统默认输出"}");
    }

    private void OnSegment(float[] samples)
    {
        if (_cts?.IsCancellationRequested != false) return;
        AppPaths.Log($"Segment queued: {samples.Length / 16000.0:0.0}s");
        _segments?.Writer.TryWrite(samples);
    }

    public async Task StartCaptionsAsync(
        string sourceLanguage = "en",
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        await StopAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _translator = new TranslationService { SourceLanguage = sourceLanguage };
        try
        {
            status?.Report("正在加载本地翻译模型…");
            await _translator.LoadAsync(AppPaths.TranslationModelPath, _cts.Token);
            TranslationReady = true;
        }
        catch (Exception ex)
        {
            TranslationReady = false;
            AppPaths.Log("Translation load failed: " + ex);
            status?.Report("翻译模型未加载，先显示原文");
        }

        _captionText = Channel.CreateBounded<(string Text, long QueuedAt)>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _captions = new WindowsCaptionsService();
        _captions.CaptionReady += text =>
        {
            if (_cts?.IsCancellationRequested == false)
                _captionText?.Writer.TryWrite((text, Stopwatch.GetTimestamp()));
        };
        _captions.Error += message => Error?.Invoke(message);

        status?.Report("正在启动 Windows 实时字幕…");
        await _captions.StartAsync(_cts.Token);
        _processTask = Task.Run(() => ProcessCaptionsLoopAsync(_cts.Token));
        status?.Report("正在聆听 · Windows 实时字幕");
    }

    private async Task ProcessCaptionsLoopAsync(CancellationToken ct)
    {
        if (_captionText == null || _translator == null) return;
        await foreach (var request in _captionText.Reader.ReadAllAsync(ct))
        {
            var english = request.Text;
            var watch = Stopwatch.StartNew();
            AppPaths.Log($"Caption translation start ticks={Stopwatch.GetTimestamp()} queueMs={Stopwatch.GetElapsedTime(request.QueuedAt).TotalMilliseconds:0}: {english}");
            try
            {
                if (string.IsNullOrWhiteSpace(english)) continue;
                CaptionChanged?.Invoke(english, "", "翻译中…");
                if (TranslationReady)
                {
                    var chinese = await TranslateStreamingAsync(english, ct);
                    CaptionChanged?.Invoke(english, chinese, "Windows 实时字幕 → 中文");
                }
                else
                {
                    CaptionChanged?.Invoke(english, "（翻译模型未加载）", "Windows 实时字幕");
                }
                AppPaths.Log($"Caption segment done in {watch.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppPaths.Log("Captions pipeline error: " + ex);
                Error?.Invoke(ex.Message);
            }
        }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        if (_segments == null || _asr == null) return;
        await foreach (var samples in _segments.Reader.ReadAllAsync(ct))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                CaptionChanged?.Invoke("", "", "识别中…");
                var english = await _asr.TranscribeAsync(samples, ct);
                if (string.IsNullOrWhiteSpace(english)) continue;

                if (TranslationReady && _translator != null)
                {
                    CaptionChanged?.Invoke(english, "", "翻译中…");
                    var chinese = await TranslateStreamingAsync(english, ct);
                    CaptionChanged?.Invoke(english, chinese, "实时翻译 · 原文 → 中文");
                }
                else
                {
                    CaptionChanged?.Invoke(english, "（翻译模型未加载）", "本地 EN");
                }

                AppPaths.Log($"Segment done in {watch.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppPaths.Log("Pipeline error: " + ex);
                Error?.Invoke(ex.Message);
                CaptionChanged?.Invoke("", "", "处理失败，请查看日志");
            }
        }
    }

    private async Task<string> TranslateStreamingAsync(string english, CancellationToken ct)
    {
        var firstPartial = 0L;
        var chinese = await _translator!.TranslateAsync(english, partial =>
        {
            if (Interlocked.CompareExchange(ref firstPartial, Stopwatch.GetTimestamp(), 0) == 0)
                AppPaths.Log($"Caption first translation token ticks={firstPartial}: {english}");
            TranslationPartial?.Invoke(english, partial);
        }, ct);
        return chinese;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _audio?.Stop(); } catch { }
        try { _captions?.Stop(); } catch { }
        _segments?.Writer.TryComplete();
        _captionText?.Writer.TryComplete();
        if (_processTask != null)
        {
            try { await _processTask; }
            catch (OperationCanceledException) { }
            _processTask = null;
        }

        if (_audio != null)
        {
            _audio.SegmentReady -= OnSegment;
            _audio.Dispose();
            _audio = null;
        }
        if (_captions != null)
        {
            _captions.Dispose();
            _captions = null;
        }
        _asr?.Dispose();
        _asr = null;
        _translator?.Dispose();
        _translator = null;
        TranslationReady = false;
        _cts?.Dispose();
        _cts = null;
        _segments = null;
        _captionText = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
