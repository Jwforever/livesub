using System.Windows;
using LiveSub.Services;

namespace LiveSub;

public partial class AddModelWindow : Window
{
    public string ModelName { get; private set; } = "";

    public AddModelWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var work = WindowEffects.GetWorkArea(this);
            Height = Math.Min(Height, work.Height - 16);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyDarkFrame(this);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var url = UrlBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请填写模型名称。", "LiveSub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "下载地址需要以 http(s):// 开头，请粘贴 resolve 直链。", "LiveSub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        url = url.Replace("/blob/", "/resolve/");
        var queryIndex = url.IndexOf('?');
        if (queryIndex >= 0) url = url[..queryIndex];

        var kind = KindCombo.SelectedIndex == 1 ? "translation" : "asr";
        var fileName = FileNameBox.Text.Trim();
        if (fileName.Length == 0)
        {
            try { fileName = Path.GetFileName(new Uri(url).AbsolutePath); } catch { }
        }
        if (fileName.Length == 0) fileName = kind == "translation" ? name + ".gguf" : name + ".bin";
        foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');

        long approxBytes = 0;
        if (double.TryParse(SizeBox.Text.Trim(), out var megabytes) && megabytes > 0)
            approxBytes = (long)(megabytes * 1024 * 1024);

        CustomModelStore.Add(new CustomModel
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
            Kind = kind,
            DisplayName = name,
            FileName = Path.Combine("custom", fileName),
            Url = url,
            ApproxBytes = approxBytes,
            MinBytes = approxBytes > 0 ? Math.Max(1_000_000, approxBytes - 2_000_000) : 0,
        });

        ModelName = name;
        DialogResult = true;
    }
}
