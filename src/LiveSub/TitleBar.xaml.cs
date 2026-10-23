using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiveSub;

public partial class TitleBar : UserControl
{
    private const string MaximizeIcon = "M 0.5,0.5 L 9.5,0.5 9.5,9.5 0.5,9.5 Z";
    private const string RestoreIcon = "M 2.5,0.5 L 9.5,0.5 9.5,7.5 M 0.5,2.5 L 7.5,2.5 7.5,9.5 0.5,9.5 Z";

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("LiveSub"));

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;
        _window.StateChanged -= OnWindowStateChanged;
        _window.StateChanged += OnWindowStateChanged;
        UpdateMaximizeGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        MaxIcon.Data = Geometry.Parse(maximized ? RestoreIcon : MaximizeIcon);
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
