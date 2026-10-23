using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LiveSub.Services;

namespace LiveSub;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int MaxCaptions = 3;

    private static readonly Brush EnglishOutlineBrush =
        new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly Brush ChineseOutlineBrush =
        new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45));
    private static readonly Thickness[] OutlineOffsets =
    {
        new(-0.4, 0, 0, 0), new(0.4, 0, 0, 0), new(0, -0.4, 0, 0), new(0, 0.4, 0, 0),
        new(-0.35, -0.35, 0, 0), new(0.35, -0.35, 0, 0), new(-0.35, 0.35, 0, 0), new(0.35, 0.35, 0, 0),
    };

    private readonly OverlaySettings _settings;
    private readonly List<OverlayCaption> _captions = new();
    private bool _adjusting;
    private bool _overlayVisible = true; // 用户显隐意图，不随字幕内容更新改变。

    public OverlayWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        ApplyBackgroundOpacity(_settings.BackgroundOpacity, save: false);
        ApplyTextOpacity(_settings.TextOpacity, save: false);
        RebuildCaptions();
        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseDown;
        LocationChanged += (_, _) =>
        {
            if (_adjusting && IsLoaded) SavePosition();
        };
    }

    public double BackgroundOpacity => _settings.BackgroundOpacity;
    public double TextOpacity => _settings.TextOpacity;
    public double FontScale => _settings.FontScale;
    public bool ShowEnglish => _settings.ShowEnglish;
    public string AsrEngine => _settings.AsrEngine;
    public string SourceLanguage => _settings.SourceLanguage;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        PositionFromSettings();
        Topmost = true;
        ApplyClickThrough(!_adjusting);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
        var work = WindowEffects.GetWorkArea(this);
        UpdateLayout();
        UpdateAutoHeight();
        AppPaths.Log($"Overlay shown: visible={IsVisible}, left={Left:0}, top={Top:0}, size={Width:0}x{Height:0}, work={work.Width:0}x{work.Height:0}");
    }

    private void FitToWorkArea()
    {
        var work = WindowEffects.GetWorkArea(this);
        Width = Math.Min(460, Math.Max(420, work.Width - 24));
        Height = Math.Min(150, Math.Max(110, work.Height - 60));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough(!_adjusting);
    }

    public void SetCaption(string english, string chinese, string status)
    {
        Dispatcher.Invoke(() =>
        {
            var en = english?.Trim() ?? string.Empty;
            var zh = chinese?.Trim() ?? string.Empty;
            if (en.Length > 0 || zh.Length > 0)
                UpsertCaption(en, zh, final: !string.IsNullOrWhiteSpace(zh));
            StatusText.Text = _adjusting
                ? "拖动字幕调整位置，完成后点主窗口的“完成调整”"
                : status ?? string.Empty;
            ApplyOverlayVisibility();
            UpdateAutoHeight();
            if (_overlayVisible) EnsureTopmost();
        });
    }

    private void UpdateAutoHeight()
    {
        if (!IsLoaded || !_overlayVisible) return;
        var chromeWidth = Panel.Margin.Left + Panel.Margin.Right +
                          Panel.Padding.Left + Panel.Padding.Right +
                          Panel.BorderThickness.Left + Panel.BorderThickness.Right;
        var availableWidth = Math.Max(200, Width - chromeWidth);
        CaptionList.Measure(new Size(availableWidth, double.PositiveInfinity));
        StatusText.Measure(new Size(availableWidth, double.PositiveInfinity));
        var desired = Panel.Margin.Top + Panel.Margin.Bottom +
                      Panel.Padding.Top + Panel.Padding.Bottom +
                      CaptionList.DesiredSize.Height +
                      StatusText.DesiredSize.Height +
                      StatusText.Margin.Top + 10;
        var work = WindowEffects.GetWorkArea(this);
        var newHeight = Math.Clamp(desired, 64, Math.Min(320, work.Height - 20));
        if (Math.Abs(newHeight - Height) < 0.5) return;
        var bottom = Top + Height;
        Height = newHeight;
        Top = Math.Clamp(bottom - newHeight, work.Top, Math.Max(work.Top, work.Bottom - newHeight));
    }

    private void EnsureTopmost()
    {
        if (!IsLoaded) return;
        Topmost = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    public void SetPartialTranslation(string english, string chinese)
    {
        Dispatcher.Invoke(() =>
        {
            var en = english?.Trim() ?? string.Empty;
            var zh = chinese?.Trim() ?? string.Empty;
            if (en.Length == 0 || zh.Length == 0 || _captions.Count == 0) return;
            var last = _captions[^1];
            if (last.Final || last.English != en) return;
            last.Chinese = zh;
            RebuildCaptions();
            ApplyOverlayVisibility();
            UpdateAutoHeight();
            if (_overlayVisible) EnsureTopmost();
        });
    }

    private void ApplyOverlayVisibility()
    {
        Visibility = _overlayVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsOverlayVisible => _overlayVisible;

    public void SetOverlayVisible(bool visible)
    {
        _overlayVisible = visible;
        ApplyOverlayVisibility();
        if (visible)
        {
            UpdateLayout();
            UpdateAutoHeight();
            EnsureTopmost();
        }
    }

    private void UpsertCaption(string english, string chinese, bool final)
    {
        if (_captions.Count > 0)
        {
            var last = _captions[^1];
            if (last.English == english && !last.Final)
            {
                last.Chinese = chinese;
                last.Final = final;
                RebuildCaptions();
                return;
            }
            if (last.English == english && last.Chinese == chinese) return;
            if (last.Chinese.Length == 0 && chinese.Length == 0 && last.English != english)
            {
                last.English = english;
                RebuildCaptions();
                return;
            }
        }

        _captions.Add(new OverlayCaption { English = english, Chinese = chinese, Final = final });
        while (_captions.Count > MaxCaptions) _captions.RemoveAt(0);
        RebuildCaptions();
    }

    private void RebuildCaptions()
    {
        CaptionList.Children.Clear();
        var maxTextWidth = Math.Max(300, (double.IsFinite(Width) ? Width : 460) - 76);
        var fontScale = FontScaleFactor;
        foreach (var caption in _captions)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };
            var showEnglish = _settings.ShowEnglish && !string.IsNullOrWhiteSpace(caption.English);
            if (showEnglish)
            {
                item.Children.Add(CreateOutlinedText(
                    caption.English, "Segoe UI", 8 * fontScale, FontWeights.Normal,
                    EnglishOutlineBrush, maxTextWidth));
            }
            if (!string.IsNullOrWhiteSpace(caption.Chinese))
            {
                var text = CreateOutlinedText(
                    caption.Chinese, "Microsoft YaHei UI", 11 * fontScale, FontWeights.Normal,
                    ChineseOutlineBrush, maxTextWidth);
                if (showEnglish) text.Margin = new Thickness(0, 2, 0, 0);
                item.Children.Add(text);
            }
            if (item.Children.Count > 0) CaptionList.Children.Add(item);
        }
    }

    private double FontScaleFactor
    {
        get
        {
            var value = Math.Clamp(_settings.FontScale, 0, 100);
            return value <= 50 ? 0.5 + value / 100.0 : 1.0 + (value - 50) / 50.0;
        }
    }

    private static FrameworkElement CreateOutlinedText(
        string text,
        string fontFamily,
        double fontSize,
        FontWeight weight,
        Brush outline,
        double maxTextWidth)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = maxTextWidth,
        };
        foreach (var offset in OutlineOffsets)
        {
            grid.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(fontFamily),
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = outline,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxTextWidth,
                Margin = offset,
                Tag = "outline",
                IsHitTestVisible = false,
            });
        }
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(fontFamily),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = maxTextWidth,
        });
        return grid;
    }

    public void Clear()
    {
        Dispatcher.Invoke(() =>
        {
            _captions.Clear();
            RebuildCaptions();
            StatusText.Text = "已停止";
            ApplyOverlayVisibility();
            UpdateAutoHeight();
        });
    }

    public void ShowPreview(bool showOverlay = true)
    {
        Dispatcher.Invoke(() =>
        {
            _captions.Clear();
            _captions.Add(new OverlayCaption { English = "LiveSub subtitle preview", Chinese = "这是一行字幕预览", Final = true });
            _captions.Add(new OverlayCaption { English = "Adjust font opacity to see it live", Chinese = "调整字体透明度会实时变化", Final = true });
            RebuildCaptions();
            StatusText.Text = _adjusting
                ? "拖动字幕调整位置，完成后点主窗口的“完成调整”"
                : "预览 · 开始翻译后自动替换为真实内容";
            if (showOverlay) SetOverlayVisible(true);
            else
            {
                ApplyOverlayVisibility();
                UpdateAutoHeight();
                if (_overlayVisible) EnsureTopmost();
            }
        });
    }

    public void SetBackgroundOpacity(double value)
    {
        ApplyBackgroundOpacity(value, save: true);
    }

    public void SetTextOpacity(double value)
    {
        ApplyTextOpacity(value, save: true);
    }

    public void SetFontScale(double value)
    {
        _settings.FontScale = Math.Clamp(value, 0, 100);
        RebuildCaptions();
        UpdateAutoHeight();
        SettingsStore.Save(_settings);
    }

    public void SetShowEnglish(bool showEnglish)
    {
        _settings.ShowEnglish = showEnglish;
        RebuildCaptions();
        UpdateAutoHeight();
        SettingsStore.Save(_settings);
    }

    public void SetAsrEngine(string engine)
    {
        _settings.AsrEngine = engine;
        SettingsStore.Save(_settings);
    }

    public void SetSourceLanguage(string language)
    {
        _settings.SourceLanguage = SourceLanguages.Get(language).Code;
        SettingsStore.Save(_settings);
    }

    public void SetAdjusting(bool adjusting)
    {
        _adjusting = adjusting;
        ApplyClickThrough(!adjusting);
        Panel.BorderThickness = new Thickness(adjusting ? 2 : 1);
        Panel.Cursor = adjusting ? Cursors.SizeAll : Cursors.Arrow;
        UpdateBorder();
        if (adjusting)
        {
            if (_captions.Count == 0) ShowPreview();
            SetOverlayVisible(true);
            StatusText.Text = "拖动字幕调整位置，完成后点主窗口的“完成调整”";
            AppPaths.Log("Overlay adjust mode on");
        }
        else
        {
            SavePosition();
            StatusText.Text = "字幕位置已保存";
            ApplyOverlayVisibility();
            AppPaths.Log("Overlay adjust mode off");
        }
    }

    public void ResetPosition()
    {
        _settings.Left = null;
        _settings.Top = null;
        PositionFromSettings();
        SavePosition();
    }

    private void ApplyBackgroundOpacity(double value, bool save)
    {
        _settings.BackgroundOpacity = Math.Clamp(value, 0.0, 1.0);
        var alpha = (byte)Math.Round(_settings.BackgroundOpacity * 255);
        Panel.Background = new SolidColorBrush(Color.FromArgb(alpha, 10, 13, 19));
        UpdateBorder();
        if (save) SettingsStore.Save(_settings);
    }

    private void ApplyTextOpacity(double value, bool save)
    {
        _settings.TextOpacity = Math.Clamp(value, 0.0, 1.0);
        TextGroup.Opacity = _settings.TextOpacity;
        if (save) SettingsStore.Save(_settings);
    }

    private void UpdateBorder()
    {
        var alpha = _adjusting
            ? (byte)220
            : (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0, 1) * 56);
        var color = _adjusting ? Color.FromArgb(alpha, 90, 200, 250) : Color.FromArgb(alpha, 255, 255, 255);
        Panel.BorderBrush = new SolidColorBrush(color);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjusting || e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can fail if the mouse is released during the call.
        }
        ClampToScreen();
        SavePosition();
    }

    private void PositionFromSettings()
    {
        var work = WindowEffects.GetWorkArea(this);
        var left = _settings.Left ?? (work.Left + (work.Width - Width) / 2);
        var top = _settings.Top ?? (work.Top + work.Height - Height - 32);

        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            left < work.Left - Width + 80 || left > work.Right - 80 ||
            top < work.Top - 20 || top > work.Bottom - 40)
        {
            left = work.Left + (work.Width - Width) / 2;
            top = work.Top + work.Height - Height - 32;
        }

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - Width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - Height - 8));
        Left = left;
        Top = top;
    }

    private void ClampToScreen()
    {
        var work = WindowEffects.GetWorkArea(this);
        Left = Math.Clamp(Left, work.Left, work.Right - Width);
        Top = Math.Clamp(Top, work.Top, work.Bottom - 40);
    }

    private void SavePosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        SettingsStore.Save(_settings);
    }

    private void ApplyClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if (enabled) style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
        else style &= ~(WsExTransparent | WsExNoActivate);
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private sealed class OverlayCaption
    {
        public string English = "";
        public string Chinese = "";
        public bool Final;
    }
}
