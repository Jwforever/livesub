using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace LiveSub.Services;

public sealed class WindowsCaptionsService : IDisposable
{
    private const string ProcessName = "LiveCaptions";
    private const string WindowClassName = "LiveCaptionsDesktopWindow";
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const string FloatingPositionId = "PositionFloatMenuFlyoutButton";

    public event Action<string>? CaptionReady;
    public event Action<string>? Error;

    private readonly CaptionTextTracker _text = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private IntPtr _captionsHwnd;
    private Process? _process;
    private bool _ownsProcess;
    private AutomationElement? _window;
    private AutomationElement? _textBlock;
    private long _originalStyle;
    private byte _originalAlpha = 255;
    private uint _originalColorKey;
    private uint _originalLayerFlags;
    private string? _originalPositionId;
    private bool _originallyMinimized;
    private bool _hidden;
    private bool _floatingSelected;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
    public bool IsWindowShownForUser => _captionsHwnd != IntPtr.Zero && IsWindow(_captionsHwnd) && !_hidden;

    public async Task StartAsync(CancellationToken ct = default)
    {
        Stop();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            _captionsHwnd = FindWindow(WindowClassName, null);
            if (_captionsHwnd == IntPtr.Zero)
            {
                _process = Process.GetProcessesByName(ProcessName).FirstOrDefault();
                if (_process == null)
                {
                    _process = Process.Start(new ProcessStartInfo(ProcessName) { UseShellExecute = false });
                    _ownsProcess = true;
                }
                _captionsHwnd = await Task.Run(() => WaitForWindow(_cts.Token), _cts.Token);
            }
            if (_process == null)
            {
                GetWindowThreadProcessId(_captionsHwnd, out var processId);
                _process = Process.GetProcessById((int)processId);
            }
            _originalStyle = GetWindowLongPtr(_captionsHwnd, GwlExStyle).ToInt64();
            _window = AutomationElement.FromHandle(_captionsHwnd);
            _originallyMinimized = IsIconic(_captionsHwnd);
            _originalLayerFlags = 0;
            if ((_originalStyle & WsExLayered) != 0)
                GetLayeredWindowAttributes(_captionsHwnd, out _originalColorKey, out _originalAlpha, out _originalLayerFlags);
            WaitForElement("SettingsButton");
            if (FindElement("DownloadProgressText") != null || FindElement("DownloadButton") != null ||
                FindElement("SpeechModelDropDown") != null)
            {
                _ownsProcess = false; // Leave Windows setup open so its language download can finish.
                ShowWindow(_captionsHwnd, 9);
                throw new InvalidOperationException("系统字幕语言包尚未就绪，请在 Windows 实时字幕中完成对应语言设置后重新开始。");
            }
            _textBlock = FindElement("CaptionsTextBlock");
            _text.Reset(_textBlock?.Current.Name ?? "", Environment.TickCount64);
            PrepareVisibleForRecognition();
            var token = _cts.Token;
            _loopTask = Task.Factory.StartNew(() => Loop(token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            AppPaths.Log("Windows Live Captions attached; waiting for first caption before hiding.");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private static IntPtr WaitForWindow(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var hwnd = FindWindow(WindowClassName, null);
            if (hwnd != IntPtr.Zero) return hwnd;
            ct.WaitHandle.WaitOne(50);
        }
        throw new InvalidOperationException("找不到 Windows 实时字幕窗口，请先按 Win+Ctrl+L 完成语言包设置。");
    }

    private void PrepareVisibleForRecognition()
    {
        ShowWindow(_captionsHwnd, 9);
        if (_floatingSelected) return;
        try
        {
            SelectPosition(FloatingPositionId, rememberOriginal: true);
            _floatingSelected = true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Switch Live Captions to floating failed; leaving it visible: " + ex.Message);
        }
    }

    private void Loop(CancellationToken token)
    {
        var previousText = "";
        var firstCaptionSeen = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsWindow(_captionsHwnd))
                {
                    Error?.Invoke("Windows 实时字幕已关闭，请停止后重新开始翻译。");
                    break;
                }
                // A visible native window at startup initializes Live Captions' audio capture.
                // Once it has produced text, hide it and keep it transparent.
                if (_hidden)
                    SetLayeredWindowAttributes(_captionsHwnd, 0, 0, 2);
                _textBlock ??= FindElement("CaptionsTextBlock");
                if (_textBlock != null)
                {
                    var snapshot = _textBlock.Current.Name;
                    if (!firstCaptionSeen && !string.IsNullOrWhiteSpace(snapshot))
                    {
                        firstCaptionSeen = true;
                        if (!_hidden)
                        {
                            try
                            {
                                HideWindowForUser();
                            }
                            catch (Exception ex)
                            {
                                AppPaths.Log("Auto-hide Live Captions failed: " + ex.Message);
                            }
                        }
                    }
                    if (snapshot != previousText)
                    {
                        AppPaths.Log($"Caption snapshot ticks={Stopwatch.GetTimestamp()}: {snapshot}");
                        previousText = snapshot;
                    }
                    foreach (var sentence in _text.Update(snapshot, Environment.TickCount64))
                    {
                        AppPaths.Log($"Captions ready ticks={Stopwatch.GetTimestamp()}: {sentence}");
                        CaptionReady?.Invoke(sentence);
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                _textBlock = null;
            }
            catch (COMException)
            {
                _textBlock = null;
            }
            token.WaitHandle.WaitOne(40);
        }
    }

    private AutomationElement? FindElement(string id)
    {
        if (_window == null) return null;
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, id);
        var element = _window.FindFirst(TreeScope.Descendants, condition);
        if (element != null) return element;
        AutomationElement? found = null;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != _process?.Id || hwnd == _captionsHwnd) return true;
            try
            {
                found = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, condition);
            }
            catch (ElementNotAvailableException) { }
            return found == null;
        }, IntPtr.Zero);
        return found;
    }

    private AutomationElement WaitForElement(string id, int timeoutMilliseconds = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var element = FindElement(id);
            if (element != null) return element;
            Thread.Sleep(40);
        }
        throw new InvalidOperationException("无法切换系统字幕位置，请在系统字幕设置中选择“覆盖在屏幕上”后重试。");
    }

    private AutomationElement? WaitForElementOrNull(string id, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var element = FindElement(id);
            if (element != null) return element;
            Thread.Sleep(40);
        }
        return null;
    }

    private void SelectPosition(string positionId, bool rememberOriginal)
    {
        var settings = WaitForElement("SettingsButton", 3000);
        var invoke = (InvokePattern)settings.GetCurrentPattern(InvokePattern.Pattern);
        AutomationElement? target = null;

        // The flyout occasionally needs a second open/expand pass while Windows is starting.
        for (var attempt = 0; attempt < 3 && target == null; attempt++)
        {
            target = FindElement(positionId);
            if (target != null) break;

            var position = FindElement("DockPositionButton");
            if (position == null)
            {
                invoke.Invoke();
                position = WaitForElement("DockPositionButton", 3000);
            }
            var expand = (ExpandCollapsePattern)position.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                expand.Expand();
            target = WaitForElementOrNull(positionId, 1800);
            if (target != null) break;

            try { invoke.Invoke(); } catch { }
            Thread.Sleep(250);
        }

        if (target == null)
            throw new InvalidOperationException(
                "无法切换系统字幕位置，请在系统字幕设置中选择“覆盖在屏幕上”后重试。");

        if (rememberOriginal && _originalPositionId == null)
        {
            foreach (var id in new[] { "DockTopMenuFlyoutButton", "DockBottomMenuFlyoutButton", FloatingPositionId })
            {
                if (FindElement(id)?.GetCurrentPattern(TogglePattern.Pattern) is TogglePattern pattern &&
                    pattern.Current.ToggleState == ToggleState.On)
                {
                    _originalPositionId = id;
                    break;
                }
            }
        }
        var toggle = (TogglePattern)target.GetCurrentPattern(TogglePattern.Pattern);
        if (toggle.Current.ToggleState != ToggleState.On)
            toggle.Toggle();
        else
            invoke.Invoke();
    }

    public void HideWindowForUser()
    {
        if (_captionsHwnd == IntPtr.Zero || !IsWindow(_captionsHwnd))
            throw new InvalidOperationException("Windows 实时字幕窗口已关闭。");
        if (_hidden) return;
        try
        {
            ShowWindow(_captionsHwnd, 9);
            if (!_floatingSelected)
            {
                try
                {
                    SelectPosition(FloatingPositionId, rememberOriginal: true);
                    _floatingSelected = true;
                }
                catch (Exception ex)
                {
                    // Hide anyway: leaving the native bar visible is worse than a failed docking switch.
                    AppPaths.Log("Switch Live Captions to floating failed; hiding current window: " + ex.Message);
                }
            }
            var style = GetWindowLongPtr(_captionsHwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(_captionsHwnd, GwlExStyle,
                new IntPtr((style | WsExLayered | WsExTransparent | WsExToolWindow) & ~WsExAppWindow));
            // SW_HIDE/minimizing stops UIA updates and off-screen windows can delay audio capture.
            EnsureOnScreen();
            if (!SetLayeredWindowAttributes(_captionsHwnd, 0, 0, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _hidden = true;
            AppPaths.Log("Live Captions window hidden; floating, on-screen and fully transparent.");
        }
        catch (Exception ex)
        {
            RestoreWindowStyle();
            throw new InvalidOperationException("隐藏系统字幕失败：" + ex.Message, ex);
        }
    }

    public void ShowWindowForUser()
    {
        if (_captionsHwnd == IntPtr.Zero || !IsWindow(_captionsHwnd))
            throw new InvalidOperationException("Windows 实时字幕窗口已关闭。");
        RestoreWindowStyle();
        ShowWindow(_captionsHwnd, 9);
        SetForegroundWindow(_captionsHwnd);
        AppPaths.Log("Live Captions window shown for user.");
    }

    private void EnsureOnScreen()
    {
        if (!GetWindowRect(_captionsHwnd, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return;

        var work = new NativeRect();
        SystemParametersInfo(48, 0, ref work, 0);
        if (work.Right <= work.Left || work.Bottom <= work.Top) return;
        var visible = bounds.Right > work.Left && bounds.Left < work.Right &&
                      bounds.Bottom > work.Top && bounds.Top < work.Bottom;
        if (visible) return;

        SetWindowPos(_captionsHwnd, IntPtr.Zero, work.Left + 40,
            Math.Max(work.Top, work.Bottom - height - 24), width, height, 0x0004 | 0x0010);
    }

    private void RestoreWindowStyle()
    {
        if (_captionsHwnd == IntPtr.Zero) return;
        if (_originalLayerFlags != 0)
            SetLayeredWindowAttributes(_captionsHwnd, _originalColorKey, _originalAlpha, _originalLayerFlags);
        else
            SetLayeredWindowAttributes(_captionsHwnd, 0, 255, 2);
        SetWindowLongPtr(_captionsHwnd, GwlExStyle, new IntPtr(_originalStyle));
        _hidden = false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _loopTask = null;
        if (_captionsHwnd != IntPtr.Zero && IsWindow(_captionsHwnd))
        {
            RestoreWindowStyle();
            if (_ownsProcess)
            {
                PostMessage(_captionsHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                try
                {
                    if (_originalPositionId != null && _originalPositionId != FloatingPositionId)
                        SelectPosition(_originalPositionId, rememberOriginal: false);
                }
                catch (Exception ex) { AppPaths.Log("Restore captions position failed: " + ex.Message); }
                if (_originallyMinimized) ShowWindow(_captionsHwnd, 6);
            }
        }
        _cts?.Dispose();
        _cts = null;
        _process?.Dispose();
        _process = null;
        _ownsProcess = false;
        _window = null;
        _textBlock = null;
        _captionsHwnd = IntPtr.Zero;
        _originalPositionId = null;
        _hidden = false;
        _floatingSelected = false;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(int action, int param, ref NativeRect rect, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
