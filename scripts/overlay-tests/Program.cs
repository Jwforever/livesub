using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using LiveSub;
using LiveSub.Services;

internal static class Program
{
    private const string TestTitle = "LiveSub overlay regression test";
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        App? app = null;
        OverlayWindow? overlay = null;
        string? settingsBefore = null;
        string? settingsPath = null;
        IntPtr handle = IntPtr.Zero;
        bool closed = false;

        try
        {
            Require(args.Length == 1, "Pass the absolute repository root.");
            Require(Path.IsPathFullyQualified(args[0]), "Repository root must be absolute.");
            settingsPath = (string?)typeof(SettingsStore)
                .GetField("Path", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            Require(settingsPath is not null, "Could not identify the product settings path.");
            settingsBefore = FileHash(settingsPath!);
            Console.WriteLine($"SETTINGS before SHA256={settingsBefore}; path={settingsPath}");
            // Fail before constructing the product if Load() would encounter invalid JSON and log.
            if (File.Exists(settingsPath))
                _ = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(settingsPath!));

            Case("No automatic-hide scheduling (static source + declared timer fields)", () =>
            {
                var source = File.ReadAllText(Path.Combine(args[0], "src", "LiveSub", "OverlayWindow.xaml.cs"));
                var xaml = File.ReadAllText(Path.Combine(args[0], "src", "LiveSub", "OverlayWindow.xaml"));
                // Deliberately conservative: any new scheduling mechanism requires review.
                var scheduling = @"\b(?:DispatcherTimer|PeriodicTimer|Timer|Task\s*\.\s*Delay|Thread\s*\.\s*Sleep|CancelAfter|Storyboard|BeginAnimation|CompositionTarget|Observable\s*\.\s*(?:Interval|Timer))\b";
                Require(!Regex.IsMatch(source + "\n" + xaml, scheduling), "Scheduling primitive found in overlay source/XAML.");
                var timerFields = typeof(OverlayWindow).GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(f => f.FieldType.Name.Contains("Timer", StringComparison.OrdinalIgnoreCase));
                Require(!timerFields.Any(), "Overlay declares a timer field.");
            });

            app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            // WPF's public setter rejects null. Clear only this test App's pending startup URI
            // before pumping, so the real MainWindow/services/models are never created.
            var startupUri = typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WPF startup field changed; cannot safely isolate MainWindow.");
            startupUri.SetValue(app, null);
            Require(app.StartupUri is null, "MainWindow startup URI was not cleared.");
            overlay = new OverlayWindow();
            overlay.Closed += (_, _) => closed = true;
            overlay.Title = TestTitle;
            overlay.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // OnLoaded ends in AppPaths.Log. Remove this handler ONLY on our disposable instance.
            // The actual visibility/caption methods, XAML, rendering, HWND, and OnSourceInitialized
            // remain the product implementation. Product load-time positioning/logging is out of scope.
            var onLoaded = typeof(OverlayWindow).GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new InvalidOperationException("Product Loaded handler changed; review log isolation before running.");
            overlay.Loaded -= (RoutedEventHandler)onLoaded.CreateDelegate(typeof(RoutedEventHandler), overlay);
            int loadedCount = 0;
            overlay.Loaded += (_, _) => loadedCount++;

            var window = overlay;
            window.SetCaption("[TEST] initial caption", "[测试] 独立浮窗回归测试", "[TEST / 测试] isolated regression window");
            window.Show();
            Pump();
            handle = new WindowInteropHelper(window).Handle;

            Case("STA App resources, real loaded HWND, test title, own process only", () =>
            {
                Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Not STA.");
                Require(app.Resources.Contains("PageBg"), "Product App resources were not initialized.");
                Require(loadedCount > 0 && window.IsLoaded, "WPF Loaded did not execute.");
                Require(window.Title == TestTitle, "Wrong test window title.");
                Require(app.Windows.Count == 1 && ReferenceEquals(app.Windows[0], window), "Unexpected application window.");
                GetWindowThreadProcessId(handle, out uint owner);
                Require(owner == Environment.ProcessId, "HWND is not owned by this test process.");
                Console.WriteLine($"WINDOW testPID={owner}; HWND=0x{handle.ToInt64():X}; title={window.Title}");
                AssertVisibility(window, true);
            });

            Case("SetOverlayVisible(false) hides WPF and native window", () =>
            {
                window.SetOverlayVisible(false);
                AssertVisibility(window, false);
            });

            Case("New subtitle updates never cancel manual hiding", () =>
            {
                for (int i = 1; i <= 4; i++)
                {
                    window.SetCaption($"[TEST] hidden update {i}", $"[测试] 隐藏字幕 {i}", "[TEST] hidden update");
                    AssertVisibility(window, false);
                    Require(ChineseRows(window).Last() == $"[测试] 隐藏字幕 {i}", "Hidden content was not updated.");
                }
            });

            Case("Partial English and translation completion remain hidden", () =>
            {
                window.SetOverlayVisible(false);
                window.Clear();
                window.SetCaption("[TEST] partial", "", "[TEST] partial");
                AssertVisibility(window, false);
                window.SetCaption("[TEST] partial complete", "", "[TEST] revised partial");
                AssertVisibility(window, false);
                window.SetCaption("[TEST] partial complete", "[测试] 翻译补全", "[TEST] completed");
                AssertVisibility(window, false);
                Require(CaptionList(window).Children.Count == 1, "Partial/completion created extra rows.");
                Require(ChineseRows(window).SequenceEqual(new[] { "[测试] 翻译补全" }), "Completion text was not rendered.");
            });

            Case("Clear empties content without revealing hidden window", () =>
            {
                window.SetOverlayVisible(false);
                window.Clear();
                MarkStatus(window, "Clear while hidden");
                AssertVisibility(window, false);
                Require(CaptionList(window).Children.Count == 0, "Clear left caption children.");
            });

            Case("Streaming partial updates the pending row without duplicating it", () =>
            {
                window.SetOverlayVisible(true);
                window.Clear();
                window.SetCaption("[TEST] streaming", "", "翻译中…");
                window.SetPartialTranslation("[TEST] streaming", "[测试] 流");
                AssertVisibility(window, true);
                Require(CaptionList(window).Children.Count == 1, "First partial did not create exactly one row.");
                Require(ChineseRows(window).SequenceEqual(new[] { "[测试] 流" }), "Partial translation was not rendered.");
                window.SetPartialTranslation("[TEST] streaming", "[测试] 流式完成");
                window.SetCaption("[TEST] streaming", "[测试] 流式完成", "Windows 实时字幕 → 中文");
                AssertVisibility(window, true);
                Require(CaptionList(window).Children.Count == 1, "Final translation duplicated the streaming row.");
                Require(ChineseRows(window).SequenceEqual(new[] { "[测试] 流式完成" }), "Final translation replaced partial incorrectly.");
            });

            Case("Streaming translation stays hidden and finalizes in place", () =>
            {
                window.SetOverlayVisible(false);
                window.Clear();
                window.SetCaption("[TEST] hidden streaming", "", "翻译中…");
                window.SetPartialTranslation("[TEST] hidden streaming", "[测试] 隐藏流");
                window.SetCaption("[TEST] hidden streaming", "[测试] 隐藏流完成", "Windows 实时字幕 → 中文");
                AssertVisibility(window, false);
                Require(CaptionList(window).Children.Count == 1, "Hidden streaming duplicated its row.");
                Require(ChineseRows(window).SequenceEqual(new[] { "[测试] 隐藏流完成" }), "Hidden streaming final text is wrong.");
            });

            Case("Repeated identical sentence after streaming completion starts a new row", () =>
            {
                window.SetOverlayVisible(true);
                window.Clear();
                window.SetCaption("[TEST] repeat", "", "翻译中…");
                window.SetCaption("[TEST] repeat", "[测试] 重复完成", "Windows 实时字幕 → 中文");
                window.SetCaption("[TEST] repeat", "", "翻译中…");
                window.SetCaption("[TEST] repeat", "[测试] 重复完成", "Windows 实时字幕 → 中文");
                Require(CaptionList(window).Children.Count == 2, "Repeated speech was merged into the finalized row or duplicated.");
                Require(ChineseRows(window).SequenceEqual(new[] { "[测试] 重复完成", "[测试] 重复完成" }), "Repeated speech was not retained twice.");
            });

            Case("ShowPreview(showOverlay:false) updates preview but stays hidden", () =>
            {
                window.SetOverlayVisible(false);
                window.ShowPreview(showOverlay: false);
                MarkStatus(window, "Passive preview while hidden");
                AssertVisibility(window, false);
                Require(CaptionList(window).Children.Count == 2, "Preview should contain two rows.");
                window.SetCaption("[TEST] after passive preview", "[测试] 预览后仍隐藏", "[TEST] after passive preview");
                AssertVisibility(window, false);
                Require(CaptionList(window).Children.Count == 3, "New caption did not append to hidden preview.");
            });

            Case("ShowPreview() explicitly reveals previously hidden window", () =>
            {
                window.SetOverlayVisible(false);
                window.ShowPreview();
                MarkStatus(window, "Explicit preview");
                AssertVisibility(window, true);
                Require(CaptionList(window).Children.Count == 2, "Explicit preview should reset to two rows.");
            });

            Case("Passive preview preserves an already visible window", () =>
            {
                window.SetOverlayVisible(true);
                window.ShowPreview(showOverlay: false);
                MarkStatus(window, "Passive preview while visible");
                AssertVisibility(window, true);
            });

            Case("Visible subtitle updates and Clear preserve visibility", () =>
            {
                window.SetOverlayVisible(true);
                window.SetCaption("[TEST] visible update", "[测试] 可见字幕更新", "[TEST] visible update");
                AssertVisibility(window, true);
                window.Clear();
                MarkStatus(window, "Clear while visible");
                AssertVisibility(window, true);
                Require(CaptionList(window).Children.Count == 0, "Visible Clear left rows.");
            });

            Case("Visible captions retain exactly the newest three rows", () => CheckCapacity(window, true));
            Case("Hidden captions retain exactly the newest three without showing", () => CheckCapacity(window, false));

            Case("Explicit show restores content; Dispatcher idle does not hide it", () =>
            {
                window.SetOverlayVisible(true);
                for (int i = 0; i < 4; i++) AssertVisibility(window, true);
                Require(CaptionList(window).Children.Count == 3, "Showing lost retained captions.");
            });
        }
        catch (Exception ex)
        {
            Fail("Test setup/execution", ex);
        }
        finally
        {
            try
            {
                if (overlay is not null)
                {
                    // Never enumerate, signal, activate, or close any pre-existing product process/window.
                    overlay.Close();
                    Pump();
                    Case("finally closed only the test instance and destroyed its HWND", () =>
                    {
                        Require(closed, "Test window Closed did not fire.");
                        Require(handle == IntPtr.Zero || !IsWindow(handle), "Test HWND is still alive.");
                        Require(app is null || app.Windows.Count == 0, "Unexpected surviving test application window.");
                    });
                }
            }
            catch (Exception ex)
            {
                Fail("Close test instance", ex);
            }
            finally
            {
                try { app?.Shutdown(); }
                catch (Exception ex) { Fail("Shutdown test App", ex); }
                if (settingsPath is not null && settingsBefore is not null)
                {
                    Case("Original settings SHA256 unchanged after window close", () =>
                    {
                        string after = FileHash(settingsPath);
                        Console.WriteLine($"SETTINGS after  SHA256={after}; path={settingsPath}");
                        Require(after == settingsBefore, "Original settings changed; no restoration attempted.");
                    });
                }
            }
        }

        Console.WriteLine($"RESULT: PASS={_passed} FAIL={_failed}");
        Console.WriteLine("SCOPE: Real product App/overlay methods/XAML/HWND. Product Loaded layout/log handler and MainWindow startup excluded. No long-wait timer test; scheduling checked statically.");
        return _failed == 0 ? 0 : 1;
    }

    private static void CheckCapacity(OverlayWindow window, bool visible)
    {
        window.SetOverlayVisible(visible);
        window.Clear();
        for (int i = 1; i <= 7; i++)
        {
            window.SetCaption($"[TEST] capacity row {i}", $"[测试] 容量行 {i}", "[TEST / 测试] max-three regression");
            AssertVisibility(window, visible);
            Require(CaptionList(window).Children.Count == Math.Min(i, 3), $"Wrong row count after caption {i}.");
            var expected = Enumerable.Range(Math.Max(1, i - 2), Math.Min(i, 3)).Select(n => $"[测试] 容量行 {n}");
            Require(ChineseRows(window).SequenceEqual(expected), $"Wrong retained row order after caption {i}.");
        }
        window.SetCaption("[TEST] capacity row 7", "[测试] 容量行 7", "[TEST] duplicate");
        AssertVisibility(window, visible);
        Require(CaptionList(window).Children.Count == 3, "Duplicate exceeded row limit.");
        Require(ChineseRows(window).SequenceEqual(new[] { "[测试] 容量行 5", "[测试] 容量行 6", "[测试] 容量行 7" }), "Duplicate changed retained rows.");
    }

    private static StackPanel CaptionList(OverlayWindow window) => window.FindName("CaptionList") as StackPanel
        ?? throw new InvalidOperationException("CaptionList was not found in product XAML.");

    private static IEnumerable<string> ChineseRows(OverlayWindow window) => CaptionList(window).Children
        .Cast<StackPanel>().SelectMany(TextBlocks)
        .Where(text => !Equals(text.Tag, "outline"))
        .Select(text => text.Text).Where(text => text.StartsWith("[测试]", StringComparison.Ordinal));

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in TextBlocks(child))
                yield return nested;
    }

    private static void MarkStatus(OverlayWindow window, string text) =>
        ((TextBlock)window.FindName("StatusText")).Text = "[TEST / 测试] " + text;

    private static void AssertVisibility(OverlayWindow window, bool visible)
    {
        Pump();
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Require(hwnd != IntPtr.Zero && IsWindow(hwnd), "No real HWND.");
        Require(window.IsOverlayVisible == visible, "User visibility intent changed.");
        Require(window.Visibility == (visible ? Visibility.Visible : Visibility.Collapsed), "Wrong WPF Visibility.");
        Require(window.IsVisible == visible, "Wrong WPF IsVisible after Dispatcher pump.");
        Require(IsWindowVisible(hwnd) == visible, "Wrong native HWND visibility after Dispatcher pump.");
    }

    private static void Pump()
    {
        // Two real dispatcher turns drain Loaded, render, layout and native window work; no sleeps.
        for (int i = 0; i < 2; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static string FileHash(string path)
    {
        if (!File.Exists(path)) return "MISSING";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Case(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { Fail(name, ex); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Fail(string name, Exception ex)
    {
        _failed++;
        Console.WriteLine($"FAIL: {name}\n{ex}");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
