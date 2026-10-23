using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiveSub.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveSub;

public sealed record AsrEngineOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private static readonly Brush ReadyBrush = new SolidColorBrush(Color.FromRgb(48, 209, 88));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(255, 159, 10));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(48, 209, 88));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(97, 113, 129));

    private readonly OverlayWindow _overlay = new();
    private SubtitlePipeline? _pipeline;
    private bool _running;
    private bool _starting;
    private bool _adjusting;
    private bool _initializing = true;
    private bool _noAudioNotified;
    private IReadOnlyList<RenderDevice> _devices = Array.Empty<RenderDevice>();

    public ObservableCollection<HistoryItem> History { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        HistoryList.ItemsSource = History;
        _overlay.IsVisibleChanged += (_, _) => UpdateOverlayToggleButton();

        OpacitySlider.Value = Math.Round(_overlay.BackgroundOpacity * 100);
        TextOpacitySlider.Value = Math.Round(_overlay.TextOpacity * 100);
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        TextOpacityValue.Text = $"{TextOpacitySlider.Value:0}%";
        FontSizeSlider.Value = Math.Round(_overlay.FontScale);
        FontSizeValue.Text = $"{FontSizeSlider.Value:0}%";
        EngineCombo.ItemsSource = new[]
        {
            new AsrEngineOption("whisper", "本地 Whisper"),
            new AsrEngineOption("captions", "Windows 实时字幕"),
        };
        EngineCombo.SelectedIndex = _overlay.AsrEngine == "captions" ? 1 : 0;
        SourceLanguageCombo.ItemsSource = SourceLanguages.All;
        SourceLanguageCombo.SelectedItem = SourceLanguages.Get(_overlay.SourceLanguage);
        _initializing = false;
        _overlay.SetBackgroundOpacity(OpacitySlider.Value / 100.0);
        _overlay.SetTextOpacity(TextOpacitySlider.Value / 100.0);
        LanguageButton.Content = _overlay.ShowEnglish ? "关闭原文" : "显示原文";
        UpdateOverlayToggleButton();
        ApplyEngineState();
        UpdateLanguageUi();
        UpdateModelBadge();
    }

    private SourceLanguage CurrentSourceLanguage =>
        SourceLanguageCombo.SelectedItem as SourceLanguage ?? SourceLanguages.Get(_overlay.SourceLanguage);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyDarkFrame(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        RefreshDevices(preserveSelection: false);

        var preferredName = Environment.GetEnvironmentVariable("LIVESUB_AUDIO_DEVICE");
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var match = _devices.ToList().FindIndex(d =>
                d.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match >= 0) DeviceCombo.SelectedIndex = match;
        }

        _overlay.SetCaption("", "", "准备就绪 · 点击开始");

        var wavArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--wav=", StringComparison.OrdinalIgnoreCase));
        if (wavArg != null)
        {
            await RunWavTestAsync(wavArg["--wav=".Length..]);
            return;
        }

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)))
            await StartAsync();
    }

    private void FitToWorkArea()
    {
        var work = WindowEffects.GetWorkArea(this);
        AppPaths.Log($"Work area: {work.Left},{work.Top} {work.Width}x{work.Height}; dpi={VisualTreeHelper.GetDpi(this).PixelsPerDip:0.##}");
        Width = Math.Min(Width, work.Width - 16);
        Height = Math.Min(Height, work.Height - 16);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        AppPaths.Log($"Main window placed at {Left:0},{Top:0} size {Width:0}x{Height:0}");
    }

    private void RefreshDevices(bool preserveSelection)
    {
        var current = DeviceCombo.SelectedItem as RenderDevice;
        _devices = AudioCaptureService.GetRenderDevices();
        foreach (var device in _devices) AppPaths.Log($"Render device: {device.Name} [{device.Id}]");
        DeviceCombo.ItemsSource = _devices;

        if (preserveSelection && current != null)
        {
            var index = string.IsNullOrEmpty(current.Id)
                ? 0
                : _devices.ToList().FindIndex(d => d.Id == current.Id);
            DeviceCombo.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            DeviceCombo.SelectedIndex = 0;
        }

        UpdateDeviceHint();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices(preserveSelection: true);
        StatusText.Text = "设备列表已刷新";
    }

    private void DeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateDeviceHint();
    }

    private void EngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || EngineCombo == null) return;
        var option = EngineCombo.SelectedItem as AsrEngineOption;
        if (option == null) return;
        _overlay.SetAsrEngine(option.Id);
        ApplyEngineState();
        UpdateModelBadge();
        if (!_running)
            StatusText.Text = option.Id == "captions"
                ? "识别引擎：Windows 实时字幕（本机识别，需 Win11 22H2+ 和对应语言包）"
                : "识别引擎：本地 Whisper";
    }

    private void SourceLanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || SourceLanguageCombo == null) return;
        var language = CurrentSourceLanguage;
        _overlay.SetSourceLanguage(language.Code);
        UpdateLanguageUi();
        UpdateModelBadge();
        if (!_running && !_starting)
            StatusText.Text = language.Code == "en"
                ? "原文语言：英语"
                : $"原文语言：{language.DisplayName} · Whisper 需多语言模型，Windows 字幕需对应语言包";
    }

    private void UpdateLanguageUi()
    {
        var language = CurrentSourceLanguage;
        LanguageHintText.Text = $"系统声音 · {language.DisplayName} → 中文";
        if (IsCaptionsEngine)
        {
            AudioStatusText.Text = $"Windows 实时字幕 · 本机识别 · 系统字幕语言需为{language.DisplayName}";
            AudioStatusText.ToolTip =
                $"需要 Windows 11 22H2 及以上；请在 Windows 实时字幕设置中把识别语言设为{language.DisplayName}，本应用不会自动安装系统语言包。开始翻译后可独立显示或隐藏系统字幕。";
        }
    }

    private bool IsCaptionsEngine => EngineCombo.SelectedItem is AsrEngineOption { Id: "captions" };

    private void ApplyEngineState()
    {
        if (EngineCombo == null) return;
        var captions = IsCaptionsEngine;
        DeviceCombo.IsEnabled = !captions;
        RefreshDevicesButton.IsEnabled = !captions;
        SystemCaptionsButton.Visibility = captions ? Visibility.Visible : Visibility.Collapsed;
        UpdateSystemCaptionsButton();
        if (captions)
        {
            AudioStatusText.Foreground = MutedBrush;
            UpdateLanguageUi();
        }
        else
        {
            UpdateDeviceHint();
        }
    }

    private bool? UpdateSystemCaptionsButton()
    {
        var available = IsCaptionsEngine && _running && _pipeline != null;
        SystemCaptionsButton.IsEnabled = available;
        bool? shown = false;
        try
        {
            if (available) shown = _pipeline!.IsCaptionsWindowShown;
        }
        catch (InvalidOperationException)
        {
            shown = null;
        }
        SystemCaptionsButton.Content = shown switch
        {
            true => "隐藏系统字幕",
            false => "显示系统字幕",
            _ => "系统字幕状态未知",
        };
        return shown;
    }

    private void UpdateDeviceHint()
    {
        if (AudioStatusText == null) return;
        if (IsCaptionsEngine) return;
        var device = DeviceCombo.SelectedItem as RenderDevice;
        AudioStatusText.Foreground = MutedBrush;
        AudioStatusText.Text = device == null || string.IsNullOrEmpty(device.Id)
            ? "输出设备：系统默认（推荐，跟随当前播放设备）"
            : $"输出设备：{device.Name}";
        AudioStatusText.ToolTip = AudioStatusText.Text;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || OpacityValue == null) return;
        OpacityValue.Text = $"{e.NewValue:0}%";
        _overlay.SetBackgroundOpacity(e.NewValue / 100.0);
        if (!_running && !_starting) _overlay.ShowPreview(showOverlay: false);
    }

    private void TextOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || TextOpacityValue == null) return;
        TextOpacityValue.Text = $"{e.NewValue:0}%";
        _overlay.SetTextOpacity(e.NewValue / 100.0);
        if (!_running && !_starting) _overlay.ShowPreview(showOverlay: false);
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || FontSizeValue == null) return;
        FontSizeValue.Text = $"{e.NewValue:0}%";
        _overlay.SetFontScale(e.NewValue);
        if (!_running && !_starting) _overlay.ShowPreview(showOverlay: false);
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.ShowPreview();
        StatusText.Text = "已显示预览效果，可在悬浮字幕上查看调整结果";
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        _adjusting = !_adjusting;
        _overlay.SetAdjusting(_adjusting);
        MoveButton.Content = _adjusting ? "完成调整" : "调整位置";
        StatusText.Text = _adjusting ? "拖动字幕调整位置，完成后点“完成调整”" : "字幕位置已保存";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.ResetPosition();
        StatusText.Text = "字幕位置已恢复默认";
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var showEnglish = !_overlay.ShowEnglish;
        _overlay.SetShowEnglish(showEnglish);
        LanguageButton.Content = showEnglish ? "关闭原文" : "显示原文";
        StatusText.Text = showEnglish ? "已显示原文" : "已关闭原文";
        if (!_running && !_starting) _overlay.ShowPreview(showOverlay: false);
    }

    private void UpdateOverlayToggleButton()
    {
        OverlayToggleButton.Content = _overlay.IsOverlayVisible ? "隐藏字幕" : "显示字幕";
    }

    private void OverlayToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var visible = !_overlay.IsOverlayVisible;
        _overlay.SetOverlayVisible(visible);
        UpdateOverlayToggleButton();
        StatusText.Text = visible ? "字幕已显示" : "字幕已隐藏";
    }

    private void SystemCaptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCaptionsEngine || _pipeline == null || !_running)
        {
            UpdateSystemCaptionsButton();
            StatusText.Text = "请先开始 Windows 实时字幕识别";
            return;
        }

        string? error = null;
        try
        {
            if (_pipeline.IsCaptionsWindowShown) _pipeline.HideCaptionsWindow();
            else _pipeline.ShowCaptionsWindow();
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }

        var state = UpdateSystemCaptionsButton() switch
        {
            true => "系统实时字幕窗口已显示",
            false => "系统实时字幕窗口已隐藏",
            _ => "系统实时字幕窗口状态暂不可用",
        };
        StatusText.Text = error == null ? state : $"{state} · 操作未完成：{error}";
    }

    private void ModelBadge_Click(object sender, RoutedEventArgs e)
    {
        var before = ModelSelectionStore.Load();
        var window = new ModelsWindow { Owner = this };
        window.ShowDialog();
        var after = ModelSelectionStore.Load();
        UpdateModelBadge();
        if (before.AsrId != after.AsrId || before.TranslationId != after.TranslationId)
        {
            StatusText.Text = _running ? "模型已切换 · 停止后重新开始生效" : "模型已切换";
        }
    }

    private void UpdateModelBadge()
    {
        var selection = ModelSelectionStore.Load();
        var asr = ModelCatalog.GetAsr(selection.AsrId);
        var translation = ModelCatalog.GetTranslation(selection.TranslationId);

        if (IsCaptionsEngine)
        {
            if (translation.IsDownloaded)
            {
                ModelBadgeText.Text = $"Windows 实时字幕 + {translation.ShortName} · 已就绪";
                ModelDot.Fill = ReadyBrush;
            }
            else
            {
                ModelBadgeText.Text = "Windows 实时字幕 · 翻译模型未下载";
                ModelDot.Fill = WarningBrush;
            }
            ModelBadgeButton.ToolTip =
                $"识别：Windows 实时字幕（本机识别，系统字幕语言需为{CurrentSourceLanguage.DisplayName}）   ·   翻译：{translation.DisplayName}   ·   {AppPaths.ModelsRoot}";
            return;
        }

        if (!asr.IsDownloaded)
        {
            ModelBadgeText.Text = "识别模型未下载 · 点击下载";
            ModelDot.Fill = WarningBrush;
        }
        else if (!translation.IsDownloaded)
        {
            ModelBadgeText.Text = $"{asr.ShortName} · 翻译模型未下载";
            ModelDot.Fill = WarningBrush;
        }
        else
        {
            ModelBadgeText.Text = $"{asr.ShortName} + {translation.ShortName} · 已就绪";
            ModelDot.Fill = ReadyBrush;
        }

        ModelBadgeButton.ToolTip =
            $"识别：{asr.DisplayName}   ·   翻译：{translation.DisplayName}   ·   {AppPaths.ModelsRoot}";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        History.Clear();
        HistoryCount.Text = "0 条";
        HistoryEmpty.Visibility = Visibility.Visible;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            await StopAsync();
            return;
        }
        await StartAsync();
    }

    private async Task StartAsync()
    {
        if (_starting || _running) return;

        var isCaptions = IsCaptionsEngine;
        var source = CurrentSourceLanguage;

        if (isCaptions)
        {
            var nativeCode = SourceLanguages.GetWindowsCaptionCode();
            if (nativeCode != null && nativeCode != source.Code)
            {
                var current = SourceLanguages.Get(nativeCode).DisplayName;
                MessageBox.Show(
                    this,
                    $"Windows 实时字幕当前识别语言是“{current}”，应用选择的是“{source.DisplayName}”。\n\n" +
                    $"请先按 Win+Ctrl+L 打开 Windows 实时字幕 → 设置 → 更改语言，选择“{source.DisplayName}”并完成语言包后，再开始翻译。\n\n" +
                    "LiveSub 不会自动安装或切换系统语言包。",
                    "原文语言不一致",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = $"请先把 Windows 实时字幕语言改为{source.DisplayName}";
                return;
            }
            if (nativeCode == null && source.Code != "en")
            {
                MessageBox.Show(
                    this,
                    $"无法确认 Windows 实时字幕的识别语言。请先按 Win+Ctrl+L 打开系统字幕，把语言设为“{source.DisplayName}”后再开始。",
                    "请先设置系统字幕语言",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = $"请在 Windows 实时字幕中设置{source.DisplayName}语言";
                return;
            }
        }

        if (!isCaptions)
        {
            var asrModel = ModelCatalog.GetAsr(ModelSelectionStore.Load().AsrId);
            if (!asrModel.IsDownloaded)
            {
                StatusText.Text = "请先下载识别模型";
                var dialog = new ModelsWindow { Owner = this };
                dialog.ShowDialog();
                UpdateModelBadge();
                asrModel = ModelCatalog.GetAsr(ModelSelectionStore.Load().AsrId);
                if (!asrModel.IsDownloaded)
                {
                    StatusText.Text = "识别模型未下载";
                    return;
                }
            }

            if (source.Code != "en" && IsEnglishOnlyWhisper(asrModel))
            {
                MessageBox.Show(
                    this,
                    $"当前 Whisper 模型“{asrModel.DisplayName}”只支持英文。\n\n" +
                    $"请先在模型管理中下载并切换到多语言模型（Whisper base 或 small，文件名不带 .en），再翻译{source.DisplayName}。",
                    "需要多语言识别模型",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = $"翻译{source.DisplayName}需要多语言 Whisper 模型";
                return;
            }

            var translationModel = ModelCatalog.GetTranslation(ModelSelectionStore.Load().TranslationId);
            if (!translationModel.IsDownloaded)
                AppPaths.Log("Translation model missing; starting without translation.");
            AppPaths.Log($"Starting pipeline; asr={asrModel.FullPath}; mt={translationModel.FullPath}; source={source.Code}");
        }
        else
        {
            var translationModel = ModelCatalog.GetTranslation(ModelSelectionStore.Load().TranslationId);
            if (!translationModel.IsDownloaded)
                AppPaths.Log("Translation model missing; captions will show source text only.");
            if (!WindowsCaptionsService.IsSupported)
            {
                StatusText.Text = "Windows 实时字幕需要 Win11 22H2 及以上";
                MessageBox.Show(
                    "当前系统不支持 Windows 实时字幕（需要 Win11 22H2+）。\n请改用“本地 Whisper”引擎。",
                    "LiveSub", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _starting = true;
        _noAudioNotified = false;
        StartButton.IsEnabled = false;
        DeviceCombo.IsEnabled = false;
        RefreshDevicesButton.IsEnabled = false;
        EngineCombo.IsEnabled = false;
        SourceLanguageCombo.IsEnabled = false;
        UpdateSystemCaptionsButton();
        _overlay.Clear();
        if (isCaptions)
        {
            _overlay.SetCaption("", "", "正在启动 Windows 实时字幕…");
            StatusText.Text = "正在启动 Windows 实时字幕…";
            AudioStatusText.Foreground = MutedBrush;
            AudioStatusText.Text = $"正在启动 Windows 实时字幕（本机识别，需{source.DisplayName}语言包）…";
        }
        else
        {
            _overlay.SetCaption("", "", "正在加载本地模型…");
            StatusText.Text = "正在加载本地模型…";
            AudioStatusText.Foreground = MutedBrush;
            AudioStatusText.Text = "正在连接音频设备…";
        }

        try
        {
            _pipeline = new SubtitlePipeline();
            _pipeline.CaptionChanged += OnCaption;
            _pipeline.TranslationPartial += OnTranslationPartial;
            _pipeline.LevelChanged += level => Dispatcher.BeginInvoke(() => LevelBar.Value = level);
            _pipeline.Error += message => Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = "错误：" + message;
                AppPaths.Log("Pipeline reported: " + message);
            });
            _pipeline.AudioStatusChanged += OnAudioStatus;

            var progress = new Progress<string>(text =>
            {
                StatusText.Text = text;
                _overlay.SetCaption("", "", text);
            });

            if (isCaptions)
            {
                await _pipeline.StartCaptionsAsync(source.Code, progress);
                _running = true;
                StartButton.Content = "停止翻译";
                StartButton.IsEnabled = true;
                StatusText.Text = "正在聆听 · Windows 实时字幕";
                AudioStatusText.Foreground = SuccessBrush;
                AudioStatusText.Text = "Windows 实时字幕 · 本机识别中";
                _overlay.SetCaption("", "", "正在聆听 · Windows 实时字幕");
            }
            else
            {
                var device = DeviceCombo.SelectedItem as RenderDevice;
                await _pipeline.StartAsync(device?.Id, source.Code, progress);
                _running = true;
                StartButton.Content = "停止翻译";
                StartButton.IsEnabled = true;
                var captureName = _pipeline.ActiveDeviceName ?? "系统默认输出";
                StatusText.Text = $"正在聆听：{captureName}";
                _overlay.SetCaption("", "", $"正在聆听：{captureName}");
            }
            UpdateSystemCaptionsButton();
        }
        catch (Exception ex)
        {
            _running = false;
            UpdateSystemCaptionsButton();
            AppPaths.Log("Start failed: " + ex);
            StatusText.Text = "启动失败：" + ex.Message;
            _overlay.SetCaption("", "", "启动失败：" + ex.Message);
            if (_pipeline != null)
            {
                await _pipeline.DisposeAsync();
                _pipeline = null;
            }
            StartButton.Content = "开始翻译";
            StartButton.IsEnabled = true;
            DeviceCombo.IsEnabled = true;
            RefreshDevicesButton.IsEnabled = true;
            EngineCombo.IsEnabled = true;
            SourceLanguageCombo.IsEnabled = true;
            ApplyEngineState();
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnAudioStatus(AudioCaptureStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_running && !_starting) return;
            var name = string.IsNullOrWhiteSpace(status.DeviceName) ? "系统默认输出" : status.DeviceName;

            if (status.TotalBytes == 0)
            {
                AudioStatusText.Foreground = WarningBrush;
                if (status.SinceLastData.TotalSeconds >= 4)
                {
                    AudioStatusText.Text = $"未收到音频 · {name} · 请确认浏览器正在播放并输出到此设备";
                    if (status.SinceLastData.TotalSeconds >= 6 && !_noAudioNotified)
                    {
                        _noAudioNotified = true;
                        _overlay.SetCaption("", "", "未收到系统音频 · 请检查输出设备与系统音量");
                    }
                }
                else
                {
                    AudioStatusText.Text = $"正在连接 {name}…";
                }
                return;
            }

            if (status.SinceLastData.TotalSeconds > 2.5)
            {
                AudioStatusText.Foreground = MutedBrush;
                AudioStatusText.Text = $"{name} · 已连接，等待声音…";
                return;
            }

            AudioStatusText.Foreground = SuccessBrush;
            var db = status.Peak > 0.00005f ? 20 * Math.Log10(status.Peak) : -100;
            AudioStatusText.Text = $"{name} · {db:0} dB · 正在接收";
            if (_noAudioNotified)
            {
                _noAudioNotified = false;
                _overlay.SetCaption("", "", "正在聆听系统声音…");
            }
        });
    }

    private async Task RunWavTestAsync(string path)
    {
        _overlay.Clear();
        _overlay.SetCaption("", "", "读取音频文件…");
        StatusText.Text = "读取音频文件…";
        try
        {
            var samples = await Task.Run(() => ReadWav16k(path));
            _overlay.SetCaption("", "", "加载本地模型…");
            var asr = new WhisperAsrService(AppPaths.WhisperModelPath, CurrentSourceLanguage.Code);
            var translator = new TranslationService { SourceLanguage = CurrentSourceLanguage.Code };
            await translator.LoadAsync(AppPaths.TranslationModelPath);
            _overlay.SetCaption("", "", "识别中…");
            var english = await asr.TranscribeAsync(samples, CancellationToken.None);
            _overlay.SetCaption(english, "", "翻译中…");
            var chinese = await translator.TranslateAsync(english, ct: CancellationToken.None);
            OnCaption(english, chinese, "文件测试完成");
            asr.Dispose();
            translator.Dispose();
        }
        catch (Exception ex)
        {
            AppPaths.Log("WAV test failed: " + ex);
            StatusText.Text = "测试失败：" + ex.Message;
            _overlay.SetCaption("", "", "测试失败：" + ex.Message);
        }
    }

    private static float[] ReadWav16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider samples = reader;
        if (reader.WaveFormat.Channels > 1)
        {
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        var resampled = new WdlResamplingSampleProvider(samples, 16000);
        var result = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++) result.Add(buffer[i]);
        }
        return result.ToArray();
    }

    private async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        UpdateSystemCaptionsButton();
        StartButton.IsEnabled = false;
        StatusText.Text = "正在停止…";
        if (_pipeline != null)
        {
            await _pipeline.DisposeAsync();
            _pipeline = null;
        }
        _overlay.Clear();
        StartButton.Content = "开始翻译";
        StartButton.IsEnabled = true;
        DeviceCombo.IsEnabled = true;
        RefreshDevicesButton.IsEnabled = true;
        EngineCombo.IsEnabled = true;
        SourceLanguageCombo.IsEnabled = true;
        LevelBar.Value = 0;
        AudioStatusText.Foreground = MutedBrush;
        ApplyEngineState();
        StatusText.Text = "已停止";
        AppPaths.Log("Pipeline stopped");
    }

    private static bool IsEnglishOnlyWhisper(ModelDescriptor model)
    {
        var name = Path.GetFileNameWithoutExtension(model.FileName);
        return name.EndsWith(".en", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".en-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".en_", StringComparison.OrdinalIgnoreCase);
    }

    private void OnTranslationPartial(string english, string chinese)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _overlay.SetPartialTranslation(english, chinese);
            var last = History.LastOrDefault();
            if (last != null && last.English == english && !last.Final)
                last.Chinese = chinese;
        });
    }

    private void OnCaption(string english, string chinese, string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = status;
            _overlay.SetCaption(english, chinese, status);

            if (string.IsNullOrWhiteSpace(english)) return;
            var last = History.LastOrDefault();

            if (string.IsNullOrWhiteSpace(chinese))
            {
                if (last != null && last.English == english && !last.Final) return;
                AddHistory(english, "", final: false);
                return;
            }

            if (last != null && last.English == english && !last.Final)
            {
                last.Chinese = chinese;
                last.Final = true;
                ScrollHistory();
                AppPaths.Log($"Caption UI applied ticks={Stopwatch.GetTimestamp()}: {english}");
                return;
            }
            if (last != null && last.English == english && last.Chinese == chinese) return;
            AddHistory(english, chinese, final: true);
            AppPaths.Log($"Caption UI applied ticks={Stopwatch.GetTimestamp()}: {english}");
        });
    }

    private void AddHistory(string english, string chinese, bool final)
    {
        History.Add(new HistoryItem(DateTime.Now.ToString("HH:mm:ss"), english, chinese) { Final = final });
        if (History.Count > 200) History.RemoveAt(0);
        HistoryCount.Text = $"{History.Count} 条";
        HistoryEmpty.Visibility = Visibility.Collapsed;
        ScrollHistory();
    }

    private void ScrollHistory()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            HistoryScroll.UpdateLayout();
            HistoryScroll.ScrollToEnd();
        }), DispatcherPriority.Background);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true }); } catch { }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            if (_pipeline != null) await _pipeline.DisposeAsync();
        }
        catch { }
        _overlay.Close();
    }
}
